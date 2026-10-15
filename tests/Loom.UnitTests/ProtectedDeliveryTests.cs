using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loom.Core;
using Loom.Sqlite;
using Xunit;

namespace Loom.Tests;

/// <summary>Real storage checks for authenticated envelopes, audit minimization and bounded retention.</summary>
public sealed class ProtectedDeliveryTests
{
    private static readonly byte[] Key=Enumerable.Range(0,32).Select(i=>(byte)i).ToArray();
    private static AesDeliveryPayloadProtection Protection(string tenant="north")=>new(tenant,"one",new Dictionary<string,byte[]>{{"one",Key}});
    private static Catalog Model()=>new([DeliveryOutbox.Definition("Delivery",new(){["Admin"]=["read","create","write","audit"]}),RecurringSchedules.Definition("Schedule",new(){["Admin"]=["read","create","write","audit"]})]);
    private static AuditRedactionPolicy Redaction(Catalog model)=>new(model.Types.Values.SelectMany(t=>t.Fields).Where(f=>f.Name is "Payload" or "LeaseToken").Select(f=>f.Id!));
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    [Fact] public void ProtectionAuthenticatesTenantPurposeAndBytesAndSupportsKeyRotation()
    {
        var plaintext=Encoding.UTF8.GetBytes("confidential evidence");var protector=Protection();var envelope=protector.Protect(plaintext,"delivery-key");
        Assert.NotEqual(envelope,protector.Protect(plaintext,"delivery-key"));
        Assert.Equal(plaintext,protector.Unprotect(envelope,"delivery-key"));
        Assert.ThrowsAny<CryptographicException>(()=>Protection("south").Unprotect(envelope,"delivery-key"));
        Assert.ThrowsAny<CryptographicException>(()=>protector.Unprotect(envelope,"other-key"));
        var corrupt=envelope.ToArray();corrupt[^1]^=1;Assert.ThrowsAny<CryptographicException>(()=>protector.Unprotect(corrupt,"delivery-key"));
        Assert.ThrowsAny<CryptographicException>(()=>protector.Unprotect(plaintext,"delivery-key"));
        var rotated=new AesDeliveryPayloadProtection("north","two",new Dictionary<string,byte[]>{{"one",Key},{"two",RandomNumberGenerator.GetBytes(32)}});
        Assert.Equal(plaintext,rotated.Unprotect(envelope,"delivery-key"));
        Assert.ThrowsAny<CryptographicException>(()=>protector.Unprotect(rotated.Protect(plaintext,"delivery-key"),"delivery-key"));
    }
    [Fact] public void ProtectedPayloadSurvivesReopenButDoesNotEnterRawAuditSnapshots()
    {
        var path=Path.Combine(Path.GetTempPath(),"loom-protected-"+Guid.NewGuid().ToString("N")+".db");var model=Model();string id;
        DocumentService Service(SqliteStore store)=>new(model,store,[],auditRedaction:Redaction(model));
        try
        {
            using(var store=new SqliteStore("Data Source="+path+";Pooling=False"))
            {
                store.Migrate(model);var service=Service(store);var session=new ApplicationSession(service,new("producer","Admin"));
                var outbox=new DeliveryOutbox(protection:Protection());
                var document=session.Atomic(s=>outbox.Enqueue(s,"Delivery","send",new(){["secret"]="confidential evidence"},key:"stable"));id=document.Id;
                Assert.DoesNotContain("confidential evidence",Encoding.UTF8.GetString(Convert.FromBase64String(document.Values["Payload"]!.ToString())));
                var audit=store.Transaction(s=>s.Audit("Delivery",id));Assert.Single(audit);
                Assert.False(audit[0].After!.Values.ContainsKey("Payload"));Assert.False(audit[0].After!.Values.ContainsKey("LeaseToken"));
                Assert.NotNull(session.Get("Delivery",id).Values["Payload"]);
            }
            using(var store=new SqliteStore("Data Source="+path+";Pooling=False"))
            {
                store.ReadCatalog();var service=Service(store);var session=new ApplicationSession(service,new("worker","Admin"));
                var outbox=new DeliveryOutbox(protection:Protection());var lease=outbox.Claim(session,outbox.Candidates(session,"Delivery").Single())!;
                Assert.Equal("confidential evidence",lease.Message.Payload["secret"]!.ToString());Assert.True(outbox.Complete(session,lease));
                Assert.Null(session.Get("Delivery",id).Values["Payload"]);
                Assert.All(store.Transaction(s=>s.Audit("Delivery",id)),entry=>
                {
                    foreach(var snapshot in new[]{entry.Before,entry.After}.Where(d=>d!=null))
                    {Assert.False(snapshot!.Values.ContainsKey("Payload"));Assert.False(snapshot.Values.ContainsKey("LeaseToken"));}
                });
            }
        }
        finally{File.Delete(path);}
    }
    [Fact] public void WrongScopeAndUnprotectedRecordsFailClosedWithoutExecutingHandlers()
    {
        var model=Model();using var store=new MemoryStore();store.Migrate(model);var session=new ApplicationSession(new(model,store,[],auditRedaction:Redaction(model)),new("worker","Admin"));
        new DeliveryOutbox(protection:Protection("south")).Enqueue(session,"Delivery","send",new(){["secret"]="private"});
        new DeliveryOutbox().Enqueue(session,"Delivery","send",new(){["secret"]="legacy"});
        var outbox=new DeliveryOutbox(protection:Protection());
        foreach(var candidate in outbox.Candidates(session,"Delivery"))Assert.Null(outbox.Claim(session,candidate));
        Assert.All(session.Query("Delivery",new(model.Get("Delivery").Fields[0].Id!)).Items,d=>Assert.Equal("Failed",d.Values["Status"]!.ToString()));
    }
    [Fact] public void RetentionOnlyExpiresOldFailuresInBoundedBatchesAndBlocksRetry()
    {
        var model=Model();using var store=new MemoryStore();store.Migrate(model);var clock=new Clock();
        var session=new ApplicationSession(new(model,store,[],auditRedaction:Redaction(model)),new("worker","Admin"));
        var outbox=new DeliveryOutbox(new(MaxAttempts:1),clock,Protection());
        for(var i=0;i<3;i++)outbox.Enqueue(session,"Delivery","send",new(){["secret"]="private"});
        foreach(var candidate in outbox.Candidates(session,"Delivery"))Assert.True(outbox.Fail(session,outbox.Claim(session,candidate)!));
        var pending=outbox.Enqueue(session,"Delivery","send",new(),clock.Now.AddDays(30));
        Assert.Equal(0,outbox.ExpireFailedPayloads(session,"Delivery",TimeSpan.FromDays(7)));
        clock.Now=clock.Now.AddDays(8);
        Assert.Equal(2,outbox.ExpireFailedPayloads(session,"Delivery",TimeSpan.FromDays(7),2));
        Assert.Equal(1,outbox.ExpireFailedPayloads(session,"Delivery",TimeSpan.FromDays(7),2));
        Assert.Equal("Pending",session.Get("Delivery",pending.Id).Values["Status"]!.ToString());
        foreach(var job in session.Query("Delivery",new(model.Get("Delivery").Fields[0].Id!)).Items.Where(d=>d.Id!=pending.Id))
        {
            Assert.Null(job.Values["Payload"]);Assert.Equal("Cancelled",job.Values["Status"]!.ToString());
            Assert.Equal(409,Assert.Throws<LoomException>(()=>outbox.Retry(session,"Delivery",job.Id,job.Version)).Status);
        }
    }
    [Fact] public void RetentionStartsAtFailureRatherThanOriginalDueTime()
    {
        var model=Model();using var store=new MemoryStore();store.Migrate(model);var clock=new Clock();
        var session=new ApplicationSession(new(model,store,[]),new("worker","Admin"));
        var outbox=new DeliveryOutbox(new(MaxAttempts:1),clock,Protection());
        outbox.Enqueue(session,"Delivery","send",new(),clock.Now.AddDays(-30));
        Assert.True(outbox.Fail(session,outbox.Claim(session,outbox.Candidates(session,"Delivery").Single())!));
        Assert.Equal(0,outbox.ExpireFailedPayloads(session,"Delivery",TimeSpan.FromDays(7)));
        clock.Now=clock.Now.AddDays(8);Assert.Equal(1,outbox.ExpireFailedPayloads(session,"Delivery",TimeSpan.FromDays(7)));
    }
    [Fact] public void RecurrenceKeepsScheduleAndOccurrenceProtectedWithRedactedAudit()
    {
        var model=Model();using var store=new MemoryStore();store.Migrate(model);var clock=new Clock();
        var session=new ApplicationSession(new(model,store,[],auditRedaction:Redaction(model)),new("worker","Admin"));
        var outbox=new DeliveryOutbox(clock:clock,protection:Protection());var schedules=new RecurringSchedules(outbox,clock);
        var schedule=schedules.Create(session,"Schedule","daily","send",new(){["secret"]="private"},60,clock.Now);
        Assert.DoesNotContain("private",Encoding.UTF8.GetString(Convert.FromBase64String(schedule.Values["Payload"]!.ToString())));
        Assert.Equal(1,schedules.Tick(session,"Schedule","Delivery"));
        var lease=outbox.Claim(session,outbox.Candidates(session,"Delivery").Single())!;Assert.Equal("private",lease.Message.Payload["secret"]!.ToString());
        Assert.All(store.Transaction(s=>s.Audit("Schedule",schedule.Id)),a=>Assert.False(a.After!.Values.ContainsKey("Payload")));
    }
}
