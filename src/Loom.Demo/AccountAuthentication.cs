using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;

namespace Loom.Demo;

public sealed record LoomAccount(string Id, string Role, string PasswordHash);
public static class AccountAuthentication
{
    public static bool Configure(WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Authentication:Mode"] ?? "Demo";
        if (mode is not ("Demo" or "Accounts")) throw new InvalidOperationException("Authentication:Mode must be Demo or Accounts.");
        if (mode == "Demo") return false;
        var accounts = builder.Configuration.GetSection("Authentication:Accounts").Get<LoomAccount[]>() ?? [];
        if (accounts.Length == 0 || accounts.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != accounts.Length || accounts.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Role is not ("Admin" or "Editor" or "Viewer") || string.IsNullOrWhiteSpace(a.PasswordHash)))
            throw new InvalidOperationException("Accounts mode requires distinct configured accounts with password hashes and valid roles.");
        builder.Services.AddSingleton(accounts);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "Loom.Session"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
            // The demo binds exclusively to loopback HTTP. Internet hosts must use HTTPS + Always.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(1); options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = c =>
            {
                var account = accounts.SingleOrDefault(a => a.Id == c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
                if (account == null || c.Principal?.FindFirstValue("stamp") != Stamp(account)) c.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddRateLimiter(options => options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return true;
    }
    private static string Stamp(LoomAccount account) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Id + account.Role + account.PasswordHash)));
    public static void Map(WebApplication app, bool enabled)
    {
        app.MapGet("/auth/session", (HttpContext c) => new { enabled, authenticated = c.User.Identity?.IsAuthenticated == true, name = c.User.FindFirstValue(ClaimTypes.NameIdentifier) });
        if (!enabled) return;
        app.MapPost("/auth/login", async (Login login, HttpContext c, LoomAccount[] accounts) =>
        {
            if (c.Request.Headers["X-Loom-Demo"] != "local") return Results.StatusCode(403);
            var account = accounts.SingleOrDefault(a => a.Id == login.Id);
            var hasher = new PasswordHasher<LoomAccount>();
            // A valid dummy hash keeps unknown-account verification on the password hashing path.
            var candidate = account ?? new LoomAccount("", "Viewer", DummyHash);
            PasswordVerificationResult verified;
            try { verified = hasher.VerifyHashedPassword(candidate, candidate.PasswordHash, login.Password ?? ""); }
            catch (FormatException) { verified = PasswordVerificationResult.Failed; }
            if (account == null || verified == PasswordVerificationResult.Failed) return Results.Json(new { error = "Invalid account or password." }, statusCode: 401);
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, account.Id), new Claim(ClaimTypes.Role, account.Role), new Claim("stamp", Stamp(account)) }, CookieAuthenticationDefaults.AuthenticationScheme);
            await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new { name = account.Id });
        }).RequireRateLimiting("login");
        app.MapPost("/auth/logout", async (HttpContext c) =>
        {
            if (c.Request.Headers["X-Loom-Demo"] != "local") return Results.StatusCode(403);
            await c.SignOutAsync(); return Results.NoContent();
        });
    }
    private static readonly string DummyHash = new PasswordHasher<LoomAccount>().HashPassword(new("", "Viewer", ""), Guid.NewGuid().ToString());
    public sealed record Login(string Id, string Password);
}
