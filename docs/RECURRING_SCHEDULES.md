# Recurring schedules and jobs administration

## Fixed UTC intervals

`RecurringSchedules` builds on DeliveryOutbox. Add its explicit Definition and the delivery Definition
to an application Catalog. `Create` takes name, registered handler, JSON payload, interval seconds,
first UTC due time and missed-run policy. Intervals are 1 second through 365 days. The stored cursor
is a UTC Unix-millisecond instant. This is fixed elapsed time, not cron or local-calendar recurrence:
24 hours stays 24 hours across daylight-saving changes.

`Tick(session, scheduleType, deliveryType, limit: 25)` queries enabled due schedules, omitting payloads.
Each schedule's occurrence enqueue and cursor advance share one atomic session. The outbox key is
`schedule:{scheduleId}:{scheduledInstant}`, so retries identify the same occurrence. Workers recheck
the schedule version and enabled flag under the transaction. Provider conflicts propagate to the host
for retry; the application does not claim lock-free or globally fair distributed scheduling.

- **Skip** emits the most recent due occurrence, advances to the next aligned interval, and discards
  older missed occurrences. It does not shift the cadence to restart time.
- **CatchUp** emits occurrences in order, limited by the total per-tick budget. Remaining missed work
  stays due for subsequent ticks. A long backlog can occupy several batches.

Pause/resume uses expected document versions and preserves the cursor. Resuming applies the saved
missed-run policy. Pausing cannot cancel deliveries already materialized: cancel those separately.
To change cadence/payload/handler, pause the old schedule and create a replacement. Do not expose
unrestricted document writes for worker-owned cursor or outbox fields.

## Authenticated operations screen

Run `dotnet run --project src/Loom.Demo -- --jobs-demo`, then open
`http://127.0.0.1:5080/jobs.html`. Demo accounts use password `Loom-jobs-demo!`:
`jobs-admin` can administer work; `jobs-viewer` demonstrates denied access. This flag cannot overwrite
configured Accounts/Identity authentication. With existing authentication, set `Jobs:Enabled=true`.
Use `Jobs:Database` to locate the separate SQLite file outside the web root. Default is `data/jobs.db`.
`Jobs:PauseWorker=true` is an operator/test setting; the UI does not remotely start a paused worker.
The host remains the existing loopback demo host, not an internet deployment configuration.

The screen provides paged deliveries, status filters, safe failure text, attempt counts, due/lease times,
manual retry/cancel confirmation, recurring schedule creation and pause/resume, and persisted receipts.
Refresh is explicit; the screen shows when data was last loaded. It does not present stale rows as live
streaming updates. Mutation requests carry expected versions; conflicts ask the user to reload.
Only signed-in Admin claims pass the server gate. A supplied role header cannot elevate access.
Existing same-origin/custom-header request protection applies. APIs omit payload and lease-token fields
from lists. Logout clears displayed records. All mutations use document validation and audit.

This host registers **only two local demonstration handlers**: demo.receipt persists one receipt per
idempotency key; demo.failure exercises bounded retries. Neither sends network messages. The separate
SQLite receipt transaction models a receiver deduplicating independently of sender acknowledgement.
Production applications must register their own trusted handlers using the core APIs. No endpoint URL
or credential can be injected through the administration form.

The hosted worker polls every second, materializes at most 25 occurrences, then processes at most 25
deliveries through the shared DeliveryWorker, with at most four active handlers. Scanning and each
active delivery own independent database connections and authorized worker sessions. Claims are
acquired only when a processing slot is available. Graceful shutdown
cancels delivery; durable records survive restart. Multiple-process/distributed throughput, fairness,
queue retention and recurring calendar expressions remain qualification/feature work.

## Verification

Shared Memory/SQLite/PostgreSQL/SQL Server contracts cover bounded catch-up, skip alignment, pause,
resume, stale administration versions, repeat ticks without duplicate occurrence keys, and rollback
of the cursor when enqueue fails. Browser tests use real cookie login and check role spoofing, missing
request headers, 25-row paging, retry/cancel, schedule creation/pause/resume, logout and mobile width.
A restart drill starts with the worker paused, saves a schedule, restarts the same database with the
worker enabled and verifies one scheduled receipt and a future cursor. These tests run in BrowserTest
on Windows, macOS and Linux. The failed-delivery UI fixture sets a terminal failure directly; existing
outbox contracts separately verify actual automatic retry exhaustion.
