# Scheduled external delivery

DeliveryOutbox is a provider-independent outbox for asynchronous external handlers. Add its explicit
Definition to an application's Catalog, then provision/migrate that application normally. Existing
Facility and Identity schemas are unchanged. Identity's encrypted email-specific outbox remains in use.

## Enqueue with business data

Inside `session.Atomic`, write the business record and call `outbox.Enqueue(scoped, "Delivery",
"certificate.ready", payload, due, key)`. Both writes and audit commit or roll back together. The host
chooses the handler name; it is never a URL taken from the payload. Optional due time is UTC. An omitted
key becomes a random GUID. Supplied keys are unique per outbox type; duplicate enqueue fails with 409.
Namespace caller-supplied keys across tenants if the receiver shares one deduplication namespace.
Payloads are JSON objects limited to 64 KiB, stored through Loom's compressed Binary type.

Only trusted producers may create outbox records. Only trusted worker/operator roles should write
status, lease and attempt fields. Do not expose a generic mutation endpoint for these records.
The service retains normal document/tenant permissions; it does not grant a privileged bypass.
The host must distinguish operator Retry/Cancel authorization from ordinary producer permissions.

## Poll, claim and deliver

```csharp
var outbox = new DeliveryOutbox(new(MaxAttempts: 5));
var transport = new HttpOutboxTransport(httpClient, new Uri(configuredEndpoint));
var handlers = new Dictionary<string, Func<OutboxMessage, CancellationToken, Task>>
{
    ["certificate.ready"] = transport.SendAsync
};
foreach (var candidate in outbox.Candidates(session, "Delivery", take: 25))
{
    var lease = outbox.Claim(session, candidate);
    if (lease is null) continue;
    var outcome = await outbox.DeliverAsync(session, lease, handlers, stoppingToken);
    // Log outcome.Exception in the host, without copying raw exception details into public records.
}
```

Use a fresh tenant-bound/session capability per host work scope; never share it concurrently or call
DeliverAsync inside an Atomic callback. The host owns polling cadence, cancellation, concurrency,
credential configuration and shutdown. Configure HttpClient timeouts and redirect policy for the
trusted endpoint. HTTP sends JSON with `Idempotency-Key`; all non-2xx responses enter bounded retry.
No external endpoint or secret is bundled, and no messages are sent by the offline demo/tests.

Candidates issues bounded provider queries for due Pending and expired Leased records, omitting
payloads. Claim checks the observed document version, status and deadline in one transaction. A new
random lease token fences each claim. The attempt counter increments at claim, so repeated process
crashes eventually exhaust the budget. An expired final claim becomes Failed on the next poll.
Concurrent claim transaction conflicts may surface as provider exceptions; hosts should log and
retry polling, not terminate permanently. Strict fairness and large-queue indexing need qualification.

Default lease is two minutes. Complete, Fail and Renew require the current token and an unexpired
lease. A stale worker cannot acknowledge, record failure or revive a lease. DeliverAsync passes a
linked host/lease-deadline cancellation token to the handler. Cancellation is cooperative: a handler
that ignores it may still perform an external action. Host cancellation leaves the persisted claim
for expiry/recovery. Long-running handlers can use Renew explicitly through a separate authorized
session; DeliverAsync's original deadline remains, so choose a sufficient lease or manage dispatch
and renewal directly with Claim/Complete/Fail.

Failures schedule 5, 10, 20, ... seconds, capped at 15 minutes by default; options configure initial
and maximum delay. After five claims by default the item becomes Failed. Failed items retain payloads
for inspection/retry. Retry requires the current failed version, preserves the key and resets attempts.
Cancel requires a current Pending or Failed version. It cannot recall work already sent or in flight.
Complete/Cancel clear payloads from the current record. **Audit snapshots retain prior payloads**;
this is not cryptographic erasure. Do not place passwords/tokens or unnecessary personal data in this
outbox. Sensitive-message encryption, retention and audit redaction remain separate work.

## Bounded worker host

`DeliveryWorker` provides reusable polling and bounded asynchronous dispatch:

```csharp
// OpenWorkerScope creates a fresh store/connection and an authorized application or tenant
// session on every call. ApplicationSessionScope owns the store or dependency-injection scope.
var worker = new DeliveryWorker(OpenWorkerScope, outbox, "Delivery", handlers,
    new DeliveryWorkerOptions(Concurrency: 4, BatchSize: 25,
        PollInterval: TimeSpan.FromSeconds(1)),
    prepare: session => schedules.Tick(session, "Schedule", "Delivery"));
await worker.RunAsync(batch => LogBatch(batch), stoppingToken);
```

The host supplies `OpenWorkerScope` and `LogBatch`; optional preparation can be omitted. The factory
must return an independent session and disposable lifetime for every scan and active delivery. Bind
authorization (including tenant membership where applicable) in that factory. Handlers are registered
by the host and copied at construction; they must be safe for concurrent invocation.

Each batch closes its scan scope before dispatch. A candidate is claimed only after a processing slot
is available, so waiting candidates do not consume their leases. Defaults are four active deliveries,
25 candidates per batch and a one-second delay after each batch. Supported limits are 1–32 active
items, 1–100 candidates and a polling delay of 1 ms–1 hour. Concurrent calls on the same worker serialize;
separate worker instances coordinate through persisted version checks and lease tokens.

Preparation failures are reported while existing queued deliveries continue. Scan failures return a
`ScanFailed` result and the polling loop tries again. Item failures/provider conflicts are isolated;
unacknowledged leases recover after expiry. Results distinguish completion, skipped claims, delivery
failure and interruption; result order is unspecified. Diagnostic callbacks must be synchronous, fast
and non-throwing: a callback exception stops the host loop. Database operations remain synchronous.

Host cancellation waits for cooperative active handlers and disposes their scopes. It leaves any
unacknowledged claims durable. This is not a forced shutdown timeout or automatic lease renewal.
The host must configure suitable handler timeouts and lease durations. `RunOnceAsync` also supports
explicit polling/testing without starting a background loop.

## Delivery guarantee

This is at-least-once delivery with a finite automatic retry budget. Delivery can fail permanently.
If a receiver accepts a message but the sender crashes before acknowledgement, the next worker sends
it again with the same key. The receiver must persist that key and its business effect atomically to
suppress duplicates. The sender cannot provide exactly-once remote effects by itself. Recipients must
also retain keys long enough for manual retries. A lease protects the sender's state, not the receiver.

## Running example and checks

Run `dotnet run --project src/Loom.Demo -- --outbox-demo`. It uses a temporary SQLite database,
advances a virtual clock, closes/reopens storage, simulates lost acknowledgement, waits through a
persisted retry deadline, and verifies two deliveries produce one deduplicated receiver effect.
It sends no network traffic. The normal MSBuild Test target runs this example on all CI platforms.

Shared Memory/SQLite/PostgreSQL/SQL Server contracts cover scheduling boundaries, capped backoff,
claim fencing, renewal, crash exhaustion, terminal states, atomic enqueue rollback, unique keys,
permissions and duplicate delivery. HTTP tests inspect the real request headers/body through a fake
HttpMessageHandler and verify non-success responses. Unit coverage includes host cancellation and four independent SQLite connections competing for one claim.

Recurring fixed UTC schedules and an authenticated local operations UI/host are now implemented;
see RECURRING_SCHEDULES.md. Remaining scope in #24: cron/calendar schedules, retention/encryption,
large-queue/distributed-worker stress qualification, and integration of additional real consumers.
The example host is not a production multi-tenant scheduler deployment.

For explicit handler registration and a host spanning multiple authorized tenant databases, see
[Tenant delivery hosting](TENANT_DELIVERY_HOSTING.md). Run `--tenant-jobs-demo` for the offline example.

The opt-in [protected tenant operations host](PROTECTED_TENANT_OPERATIONS.md) now adds organization-scoped administration, encrypted payloads, new-audit redaction and failed-payload retention. Existing unprotected hosts retain their previous behavior.
