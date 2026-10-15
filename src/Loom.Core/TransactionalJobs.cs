using System.Globalization;
using System.Text.Json.Nodes;

namespace Loom.Core;

/// <summary>Map an application-owned durable job record; no hidden database schema is installed.</summary>
public sealed record JobFields(string Status="Status",string Attempts="Attempts",string Error="Error");
public sealed record JobOutcome(bool Completed,bool Failed,Exception? Exception=null);

/// <summary>
/// Runs synchronous database-only work and completion in one transaction. Rollback leaves pending work
/// restartable. This is not a lease scheduler or an exactly-once guarantee for external side effects.
/// </summary>
public sealed class TransactionalJobs
{
    private readonly JobFields fields;
    private readonly int maxAttempts;
    public TransactionalJobs(int maxAttempts=3,JobFields? fields=null)
    {
        if(maxAttempts<1)throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        this.maxAttempts=maxAttempts;this.fields=fields??new();
    }
    private string Status(Document job)=>job.Values[fields.Status]?.ToString()??"";
    private int Attempts(Document job)=>int.Parse(job.Values[fields.Attempts]?.ToString()??"0",CultureInfo.InvariantCulture);
    private Document Save(IApplicationSession s,Document job,string state,int attempts,string? error)
    {
        var values=(JsonObject)job.Values.DeepClone();values[fields.Status]=state;values[fields.Attempts]=attempts;values[fields.Error]=error;
        return s.Update(job.Type,job.Id,job.Version,values);
    }
    /// <summary>Only the observed version may execute or record a failed attempt; stale candidates are skipped.</summary>
    public JobOutcome Run(IApplicationSession session,Document candidate,Action<IApplicationSession,Document> handler,
        string publicError="Job failed. Consult the server log.")
    {
        ApplicationWorkflow.Synchronous(handler);
        try
        {
            return session.Atomic(s=>
            {
                var job=s.Get(candidate.Type,candidate.Id);
                if(job.Version!=candidate.Version||Status(job)!="Pending")return new JobOutcome(false,false);
                handler(s,job.Copy());
                Save(s,job,"Complete",checked(Attempts(job)+1),null);
                return new JobOutcome(true,false);
            });
        }
        catch(Exception ex) when(ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // The handler transaction has rolled back. Recheck the version before recording failure;
            // another worker may already have completed or retried this candidate.
            return session.Atomic(s=>
            {
                var job=s.Get(candidate.Type,candidate.Id);
                if(job.Version!=candidate.Version||Status(job)!="Pending")return new JobOutcome(false,false,ex);
                var attempts=checked(Attempts(job)+1);var failed=attempts>=maxAttempts;
                Save(s,job,failed?"Failed":"Pending",attempts,publicError);
                return new JobOutcome(false,failed,ex);
            });
        }
    }
    /// <summary>Authorization remains with the session's write policy and the host's retry action.</summary>
    public Document Retry(IApplicationSession session,string type,string id,int version)=>session.Atomic(s=>
    {
        var job=s.Get(type,id);
        if(job.Version!=version||Status(job)!="Failed")throw new LoomException(409,"Only a current failed job can be retried.");
        return Save(s,job,"Pending",0,null);
    });
}
