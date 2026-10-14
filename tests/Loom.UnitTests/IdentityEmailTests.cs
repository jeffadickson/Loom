using System.Security.Cryptography;
using Loom.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Loom.UnitTests;

/// <summary>Real SQLite transactions and persisted keys, with an isolated substitute for the external mail service.</summary>
public sealed class IdentityEmailTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "loom-mail-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private readonly IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?> { ["Identity:PublicOrigin"] = "https://accounts.example.test" }).Build();

    public IdentityEmailTests() => Directory.CreateDirectory(directory);
    private IdentityDatabase Database() => new(new DbContextOptionsBuilder<IdentityDatabase>()
        .UseSqlite("Data Source=" + Path.Combine(directory,"accounts.db") + ";Pooling=False").Options);
    private IDataProtectionProvider Protection() => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys")));
    private IdentityEmailOutbox Sender(IdentityDatabase db) => new(db, Protection(), configuration, clock);
    private IdentityEmailDispatcher Dispatcher(IdentityDatabase db, IIdentityEmailTransport transport) => new(db, Protection(), transport, clock);
    private async Task Queue(IdentityDatabase db) => await Sender(db).SendEmailAsync("person@example.test","Account recovery",
        "<a href=\"https://attacker.example/Identity/Account/ResetPassword?code=private-token\">Reset</a>");
    private static IIdentityEmailTransport Transport()
    {
        var transport = Substitute.For<IIdentityEmailTransport>();
        transport.DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return transport;
    }

    [Fact]
    public async Task UpgradePreservesAccountsAndQueueRollsBackWithAccountCreation()
    {
        await using var db = Database();
        await db.GetService<IMigrator>().MigrateAsync("20261014210000_InitialAccounts");
        db.Users.Add(new LoomUser { Id="existing",UserName="existing",NormalizedUserName="EXISTING" });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.True(await db.Users.AnyAsync(u=>u.Id=="existing"));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.Users.Add(new LoomUser { Id="rolled-back",UserName="new",NormalizedUserName="NEW" });
            await Queue(db);
            // Deliberately omit commit, as on a failed registration handler.
        }
        await using var reopened=Database();
        Assert.False(await reopened.Users.AnyAsync(u=>u.Id=="rolled-back"));
        Assert.Empty(await reopened.EmailOutbox.ToListAsync());
    }

    [Fact]
    public async Task RestartDeliversProtectedPayloadWithPinnedOriginAndErasesIt()
    {
        await using(var db=Database())
        {
            await db.Database.MigrateAsync();await Queue(db);
            var raw=(await db.EmailOutbox.SingleAsync()).ProtectedPayload;
            Assert.DoesNotContain("private-token",raw);Assert.DoesNotContain("person@example.test",raw);
        }
        var transport=Transport();
        await using var reopened=Database();
        Assert.True(await Dispatcher(reopened,transport).DispatchOneAsync());
        await transport.Received(1).DeliverAsync(Arg.Is<IdentityEmail>(m=>
            m.Html.Contains("https://accounts.example.test/")&&!m.Html.Contains("attacker.example")),Arg.Any<CancellationToken>());
        var stored=await reopened.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal("Sent",stored.Status);Assert.Equal("",stored.ProtectedPayload);Assert.Equal(1,stored.Attempts);
        Assert.False(await Dispatcher(reopened,transport).DispatchOneAsync());
    }

    [Fact]
    public async Task FailureRetriesOnlyWhenDueThenExhaustsWithoutLeakingException()
    {
        await using var db=Database();await db.Database.MigrateAsync();await Queue(db);
        var transport=Transport();
        transport.DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("password=secret; private-token")));
        var dispatcher=Dispatcher(db,transport);
        for(var attempt=1;attempt<=IdentityEmailDispatcher.MaximumAttempts;attempt++)
        {
            Assert.True(await dispatcher.DispatchOneAsync());
            var entry=await db.EmailOutbox.AsNoTracking().SingleAsync();
            Assert.Equal(attempt,entry.Attempts);
            Assert.Equal("DeliveryFailed",entry.FailureCode);
            Assert.False(await dispatcher.DispatchOneAsync());
            clock.Advance(TimeSpan.FromSeconds(60*(1 << (attempt-1))));
        }
        var final=await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal("Dead",final.Status);Assert.Empty(final.ProtectedPayload);
    }

    [Fact]
    public async Task ExpiredMessagesAreNeverDeliveredAndMetadataIsEventuallyPurged()
    {
        await using var db=Database();await db.Database.MigrateAsync();await Queue(db);
        clock.Advance(TimeSpan.FromHours(1));var transport=Transport();
        Assert.False(await Dispatcher(db,transport).DispatchOneAsync());
        var expired=await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal("Expired",expired.Status);Assert.Empty(expired.ProtectedPayload);
        await transport.DidNotReceive().DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>());
        clock.Advance(TimeSpan.FromDays(8));await Dispatcher(db,transport).DispatchOneAsync();
        Assert.Empty(await db.EmailOutbox.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CompetingWorkersDoNotDeliverAnActiveLeaseTwice()
    {
        await using var db=Database();await db.Database.MigrateAsync();await Queue(db);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport=Transport();
        transport.DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>()).Returns(async _=>
        { entered.SetResult();await release.Task; });
        var first=Dispatcher(db,transport).DispatchOneAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second=Database();
        try { Assert.False(await Dispatcher(second,transport).DispatchOneAsync()); }
        finally { release.SetResult(); }
        Assert.True(await first);
        await transport.Received(1).DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InterruptedDeliveryRecoversLeaseAfterRestart()
    {
        await using var db=Database();await db.Database.MigrateAsync();await Queue(db);
        using var stop=new CancellationTokenSource();
        var interrupted=Transport();
        interrupted.DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>()).Returns(_=>
        { stop.Cancel();return Task.FromCanceled(stop.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Dispatcher(db,interrupted).DispatchOneAsync(stop.Token));
        await using var restarted=Database();var transport=Transport();
        Assert.False(await Dispatcher(restarted,transport).DispatchOneAsync());
        clock.Advance(TimeSpan.FromSeconds(IdentityEmailDispatcher.LeaseSeconds+1));
        Assert.True(await Dispatcher(restarted,transport).DispatchOneAsync());
        Assert.Equal(2,(await restarted.EmailOutbox.AsNoTracking().SingleAsync()).Attempts);
    }

    [Fact]
    public async Task UnreadablePayloadIsDeadLetteredWithoutTransportCall()
    {
        await using var db=Database();await db.Database.MigrateAsync();await Queue(db);
        await db.EmailOutbox.ExecuteUpdateAsync(s=>s.SetProperty(e=>e.ProtectedPayload,"corrupt"));
        var transport=Transport();Assert.True(await Dispatcher(db,transport).DispatchOneAsync());
        var entry=await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal("Dead",entry.Status);Assert.Equal("PayloadUnavailable",entry.FailureCode);Assert.Empty(entry.ProtectedPayload);
        await transport.DidNotReceive().DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleWorkerCannotAcknowledgeAnotherWorkersLease()
    {
        await using var db=Database();await db.Database.MigrateAsync();await Queue(db);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow=Transport();
        slow.DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>()).Returns(async _=>
        { entered.SetResult();await release.Task; });
        var oldWorker=Dispatcher(db,slow).DispatchOneAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(IdentityEmailDispatcher.LeaseSeconds+1));
        await using var newer=Database();
        var unavailable=Transport();
        unavailable.DeliverAsync(Arg.Any<IdentityEmail>(),Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException()));
        try { Assert.True(await Dispatcher(newer,unavailable).DispatchOneAsync()); }
        finally { release.SetResult(); }
        await oldWorker;
        var row=await newer.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal("Pending",row.Status);Assert.Equal(2,row.Attempts);
        Assert.Equal("DeliveryFailed",row.FailureCode);Assert.NotEmpty(row.ProtectedPayload);
    }

    [Fact]
    public async Task DevelopmentPickupDeduplicatesTheStableEnvelopeId()
    {
        var pickup=Path.Combine(directory,"mail");
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Identity:PublicOrigin"]="http://localhost:5080",
            ["Identity:Email:PickupDirectory"]=pickup
        }).Build();
        var environment=Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        environment.EnvironmentName.Returns("Development");environment.ContentRootPath.Returns(directory);
        var transport=new IdentityEmailSender(config,environment);
        var envelope=new IdentityEmail(Guid.NewGuid().ToString("N"),"person@example.test","Test","Protected test body");
        await transport.DeliverAsync(envelope,CancellationToken.None);
        await transport.DeliverAsync(envelope,CancellationToken.None);
        Assert.Single(Directory.GetFiles(pickup));
        Assert.EndsWith(envelope.Id+".json",Directory.GetFiles(pickup)[0]);
    }

    public void Dispose() => Directory.Delete(directory,true);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan amount)=>now+=amount;
    }
}
