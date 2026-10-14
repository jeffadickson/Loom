using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Loom.Identity;

/// <summary>Delivery envelope. Treat recipient and HTML as credentials; never log this record.</summary>
public sealed record IdentityEmail(string Id, string To, string Subject, string Html);

/// <summary>Replace this transport to integrate a mail provider; keep the durable IEmailSender registered.</summary>
public interface IIdentityEmailTransport
{
    Task DeliverAsync(IdentityEmail message, CancellationToken cancellationToken);
}

/// <summary>Persistent delivery state. ProtectedPayload is erased on completion, exhaustion or expiry.</summary>
public sealed class IdentityEmailEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProtectedPayload { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long DueAt { get; set; }
    public long LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
    public int Attempts { get; set; }
    public string? FailureCode { get; set; }
    public long? CompletedAt { get; set; }
}

/// <summary>
/// Enlists email in the scoped Identity database transaction. SMTP never runs in an HTTP request.
/// Fixed-origin links and encrypted payloads prevent host poisoning and plaintext token storage.
/// </summary>
public sealed class IdentityEmailOutbox(
    IdentityDatabase db, IDataProtectionProvider protection,
    Microsoft.Extensions.Configuration.IConfiguration configuration, TimeProvider clock) : IEmailSender
{
    internal const string ProtectionPurpose = "Loom.Identity.EmailOutbox.v1";

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var entry = new IdentityEmailEntry { CreatedAt = now, DueAt = now, ExpiresAt = now + 3300 };
        var message = new IdentityEmail(entry.Id, email, subject,
            IdentityEmailSender.PinOrigin(htmlMessage, configuration["Identity:PublicOrigin"]!));
        entry.ProtectedPayload = protection.CreateProtector(ProtectionPurpose).Protect(JsonSerializer.Serialize(message));
        db.EmailOutbox.Add(entry);
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Claims work with a compare-and-set lease. Delivery is at least once: a crash after SMTP
/// acceptance but before acknowledgement can resend the same envelope ID. SMTP is not transactional.
/// </summary>
public sealed class IdentityEmailDispatcher(
    IdentityDatabase db, IDataProtectionProvider protection, IIdentityEmailTransport transport, TimeProvider clock)
{
    public const int MaximumAttempts = 5;
    public const int LeaseSeconds = 120;

    /// <summary>Processes at most one due message; returns false when there is no claimable work.</summary>
    public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        // Retain only safe delivery metadata for seven days.
        await db.EmailOutbox.Where(e => e.CompletedAt != null && e.CompletedAt < now - 604800)
            .ExecuteDeleteAsync(cancellationToken);
        await db.EmailOutbox.Where(e => e.Status == "Pending" && e.LeaseUntil <= now &&
                (e.ExpiresAt <= now || e.Attempts >= MaximumAttempts))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, e => e.ExpiresAt <= now ? "Expired" : "Dead")
                .SetProperty(e => e.FailureCode, e => e.ExpiresAt <= now ? "Expired" : "AttemptsExhausted")
                .SetProperty(e => e.ProtectedPayload, "")
                .SetProperty(e => e.LeaseToken, (string?)null)
                .SetProperty(e => e.CompletedAt, now), cancellationToken);
        var id = await db.EmailOutbox.AsNoTracking()
            .Where(e => e.Status == "Pending" && e.DueAt <= now && e.LeaseUntil <= now &&
                e.ExpiresAt > now && e.Attempts < MaximumAttempts)
            .OrderBy(e => e.DueAt).ThenBy(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync(cancellationToken);
        if (id == null) return false;
        var lease = Guid.NewGuid().ToString("N");
        var claimed = await db.EmailOutbox.Where(e => e.Id == id && e.Status == "Pending" &&
                e.LeaseUntil <= now && e.DueAt <= now && e.ExpiresAt > now && e.Attempts < MaximumAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LeaseToken, lease)
                .SetProperty(e => e.LeaseUntil, now + LeaseSeconds)
                .SetProperty(e => e.Attempts, e => e.Attempts + 1), cancellationToken);
        if (claimed == 0) return false;
        var entry = await db.EmailOutbox.AsNoTracking().SingleAsync(e => e.Id == id, cancellationToken);
        string? failure = null;
        try
        {
            var json = protection.CreateProtector(IdentityEmailOutbox.ProtectionPurpose).Unprotect(entry.ProtectedPayload);
            var message = JsonSerializer.Deserialize<IdentityEmail>(json)
                ?? throw new System.Security.Cryptography.CryptographicException();
            // Do not hold a database transaction while contacting the mail service.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await transport.DeliverAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the lease for restart recovery; acceptance by the mail service is unknown.
            throw;
        }
        catch (System.Security.Cryptography.CryptographicException) { failure = "PayloadUnavailable"; }
        catch (JsonException) { failure = "PayloadUnavailable"; }
        catch (Exception) { failure = "DeliveryFailed"; } // Never persist exception text: it may contain credentials.

        var finished = clock.GetUtcNow().ToUnixTimeSeconds();
        var terminal = failure == null || failure == "PayloadUnavailable" || entry.Attempts >= MaximumAttempts;
        await db.EmailOutbox.Where(e => e.Id == id && e.Status == "Pending" && e.LeaseToken == lease)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, failure == null ? "Sent" : terminal ? "Dead" : "Pending")
                .SetProperty(e => e.FailureCode, failure)
                .SetProperty(e => e.ProtectedPayload, terminal ? "" : entry.ProtectedPayload)
                .SetProperty(e => e.CompletedAt, terminal ? finished : (long?)null)
                .SetProperty(e => e.DueAt, finished + 60L * (1L << (entry.Attempts - 1)))
                .SetProperty(e => e.LeaseUntil, 0)
                .SetProperty(e => e.LeaseToken, (string?)null), cancellationToken);
        return true;
    }
}

/// <summary>Bounded background pump. Each delivery uses a fresh scope and the persisted queue survives restarts.</summary>
internal sealed class IdentityEmailWorker(IServiceScopeFactory scopes, ILogger<IdentityEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < 10; i++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<IdentityEmailDispatcher>().DispatchOneAsync(stoppingToken))
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Identity email queue temporarily unavailable; retrying."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
