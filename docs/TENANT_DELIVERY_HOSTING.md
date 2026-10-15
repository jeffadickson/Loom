# Tenant-aware delivery hosting

Loom applications register trusted C# handlers and operator-owned tenant routes. `TenantDeliveryWorker`
uses the existing `DeliveryWorker` and persisted outbox leases. No tenant, service identity, endpoint,
or handler implementation is chosen from message contents. Handler names in records only select an
entry in the explicit registry; unknown names follow ordinary bounded failure/retry handling.

## Configure a host

```csharp
var handlers = new DeliveryHandlerRegistry(
    new Dictionary<string, Func<TenantDeliveryContext, CancellationToken, Task>>
    {
        ["certificate.ready"] = async (context, token) =>
        {
            // Use an independent DI scope for each invocation, not a captured scoped service.
            await using var scope = services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<CertificatePublisher>();
            await publisher.SendAsync(context.TenantId, context.Message.Payload,
                context.IdempotencyKey, token);
        }
    });
var workspace = new TenantWorkspace(databaseDirectory, configuredTenantIds, ResolveMembership);
var routes = configuredTenantIds.Select(tenant =>
    workspace.DeliveryRoute("certificate-worker", tenant,
        session => schedules.Tick(session, "Schedule", "Delivery")));
var worker = new TenantDeliveryWorker(routes, outbox, "Delivery", handlers,
    new DeliveryWorkerOptions(Concurrency: 4, BatchSize: 25));
await worker.RunAsync(result => LogBatch(result.TenantId, result.Batch), stoppingToken);
```

This is host integration code: `CertificatePublisher`, `ResolveMembership`, configuration and logging
are supplied by the application. `services` is the root service provider; each invocation creates and
asynchronously disposes its own dependency-injection scope. The registry copies its registrations,
rejects duplicate/empty names and null callbacks, and never scans assemblies or loads payload-selected
code. Delegates must support concurrent calls; do not capture a shared database connection or scoped
service. Endpoint credentials and tenant-to-destination mappings belong to trusted host configuration.

Use `RunAsync` in a `BackgroundService.ExecuteAsync` with the host stopping token. Log diagnostic
exceptions only to trusted logs. The synchronous logging callback must not throw. `RunOnceAsync`
provides deterministic one-round execution for operator jobs and tests. Register one long-lived worker
instance per configured route set; do not construct one on each HTTP request. Configuration is a
startup snapshot: changing route or handler lists requires constructing a new worker, normally on
host restart. Membership remains live and is rechecked during processing.

## Storage and authorization

Provision each tenant explicitly before startup, with an outbox definition (and schedule definition
if used) in its catalog. Existing schemas need reviewed migrations. `DeliveryRoute` never provisions
or migrates databases. Its session factory uses `TenantWorkspace.Open`, so it checks operator routing,
physical database binding and current service membership before opening a session and on subsequent
operations. Assign only the document permissions needed by that service, not a global administrator
bypass. Revoking membership prevents future authorized operations, including acknowledgement; it
cannot recall an already-started remote action.

Each scan and active delivery owns a separate session/connection. Preparation runs only inside that
tenant's session. The SQLite adapter uses the existing database-per-tenant workspace. The core route
record is provider-independent: custom hosts may supply other session factories, but are responsible
for binding the returned session to the configured tenant and resolving current authority. The core
cannot verify an arbitrary factory's physical routing.

## Receiver keys and execution limits

`context.TenantId` comes from the configured route. Ignore payload fields that claim a tenant identity.
`context.Message.Key` remains the original tenant-local persisted key; `context.IdempotencyKey` is
`tenant-id:original-key` for shared receivers. Tenant IDs are normalized lowercase slugs without colons,
so this prefix does not introduce ambiguous tenant boundaries. Keys remain stable through retries and
host restarts. Applications sharing a receiver across multiple outbox types must additionally namespace
their original keys. Receivers must commit the key and effect atomically and retain keys for manual
retries; the worker still provides at-least-once delivery with bounded attempts.

One round visits the fixed tenant list in order, processing at most one bounded batch per tenant.
The next round starts after the configured delay. This bounds active handler concurrency globally
within this host instance, instead of multiplying connections by tenant count. A busy tenant cannot
consume an unbounded batch before the next tenant gets a turn. Scan/preparation/item errors are reported
per tenant without abandoning later tenants. Concurrent rounds on the same instance serialize.

Slow handlers delay later tenants; cancellation and lease deadlines remain cooperative. A handler that
ignores cancellation can block a round. Configure timeouts and use separate host instances for workload
isolation when required. This is not weighted fairness, automatic lease renewal, a distributed leader
election system or a throughput guarantee. Other host instances coordinate through existing durable
leases, not an in-memory global lock. Production capacity and failure-injection qualification remain open.

## Run the example

```
dotnet run --project src/Loom.Demo -- --tenant-jobs-demo
```

The offline example provisions two temporary SQLite tenant databases, queues the same local key in
each, closes the enqueue sessions and reopens through the worker routes. It verifies two distinct
receiver keys, persisted completion and an empty second round. The demonstration receiver is in memory;
it is not a durable remote deduplication implementation. No network messages are sent. The standard
MSBuild Test target runs it on Windows, macOS and Linux.

Tests exercise payload spoofing, physical isolation, revocation before scanning and during a handler,
recovery with the same receiver key, bounded progress to another tenant, host cancellation/restart,
and invalid registrations. Existing provider tests cover the underlying outbox concurrency and recovery
on PostgreSQL/SQL Server. The concrete tenant-routing adapter here is SQLite only. A production
multi-tenant operations UI, sensitive-payload retention/encryption and distributed load qualification
remain separate tasks in #24.
