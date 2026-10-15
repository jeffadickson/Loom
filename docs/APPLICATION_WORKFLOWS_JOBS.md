# Application workflows and transactional jobs

## Authorized sessions

`IApplicationSession` is the narrow authorized document capability shared by workflow and job
services. `ApplicationSession` binds a host-selected Actor to DocumentService for any provider.
`TenantSession` implements the same interface while rechecking trusted tenant membership.
Never construct the Actor from an unverified browser role. Atomic callbacks receive an expiring
session; writes, validation and audit retain the existing provider transaction semantics.

## Guarded application states

`ApplicationWorkflow` names its state field by stable schema field ID. Each `WorkflowAction`
declares source states, destination, allowed roles, and optional synchronous C# guard and effect.
This state is independent of Loom's built-in Draft/Submitted/Cancelled document lifecycle.

```csharp
var statusId = catalog.Get("Request").Fields.Single(f => f.Name == "Status").Id!;
var workflow = new ApplicationWorkflow(statusId,
[
    new("Approve", ["AwaitingReview"], "Approved", ["Reviewer"],
        Guard: context =>
        {
            if (context.Document.Values["Operator"]!.ToString() == context.Session.Identity.Id)
                throw new LoomException(403, "Independent review required.");
        })
]);
var updated = workflow.Execute(session, "Request", requestId, expectedVersion, "Approve");
```

Execution checks role, readable/writable state field, current version, source state and guard.
Effects use the scoped session to create related documents and can edit the destination values.
The engine assigns the declared destination after the effect, then validates/saves normally.
Failure rolls back related records and audit with the state change. Guards receive document copies.
Async-void guards/effects are rejected before execution; do not start background tasks from callbacks.

Definitions are trusted application code. Do not expose a generic Update endpoint for workflow-owned
records: an application state graph is enforced when Execute is used, not by every raw document write.
This increment deliberately preserves C# domain rules rather than introducing an expression DSL or
visual workflow editor. Record/Review in Facility retain their existing atomic domain actions;
Start, Cancel and QueueRelease use the shared engine.

## Durable database-only handlers

`TransactionalJobs` uses application-owned job documents with Status, Attempts and Error fields;
`JobFields` can map alternative field names. No schema change is required for existing ReleaseJob.
Enqueue inside the same Atomic callback as the initiating business state change. A trusted host
selects pending records and invokes Run with the observed document/version and a synchronous handler.
The host owns polling, batch size, tenant selection and service identity.

```csharp
var runner = new TransactionalJobs(maxAttempts: 3);
var outcome = runner.Run(session, candidate, (transaction, job) =>
{
    transaction.Create("Certificate", BuildCertificate(job));
});
// Log outcome.Exception server-side; persisted Error contains only the supplied public message.
```

The handler and Complete update share a transaction. An exception rolls back its writes, then a
separate transaction increments Attempts on that exact pending version. Exhaustion becomes Failed.
Stale candidates cannot run or consume attempts. Retry requires the current failed version and the
session's write permission; the host may require a stronger administrator role. Cancellation throws
without consuming an attempt. Infrastructure errors recording failure propagate to the host.

After a process crash the transaction rolls back, leaving the job pending. Concurrent database
transactions may conflict; losing writes roll back, and the host must tolerate provider transaction
errors. A crash between rollback and recording a failure may leave that attempt uncounted.
Bounded attempts count recorded outcomes, not process crashes.

**Handlers must perform only synchronous work whose effects share this database transaction.**
Do not send email, invoke instruments, write external files or make HTTP calls inside Run. External
operations need a leased outbox with at-least-once delivery and idempotency keys. Identity's existing
email outbox remains separate. DeliveryOutbox now provides due scheduling, capped backoff, HTTP delivery and worker leases; see
DELIVERY_OUTBOX.md. Recurring schedules and generalized cancellation administration remain open in #24. Long computation inside a transaction holds
locks; this runner is intended for small database-derived publications, not instrument acquisition.

## Application and verification

Facility release generation now uses this runner for snapshot/publication, completion, bounded
retries and manager retry. Existing queued jobs remain compatible. Its browser demo still exercises
real login, evidence, independent review, process restart and one downloadable release.

Shared Memory, SQLite, PostgreSQL and SQL Server contracts verify state/role/version checks,
transaction rollback including audit, failed-attempt rollback, safe diagnostic messages, exhaustion,
retry, stale candidate suppression and cancellation. Unit tests reject async-void callbacks and
verify mutation of the caller's role/state arrays cannot alter an accepted workflow definition.
