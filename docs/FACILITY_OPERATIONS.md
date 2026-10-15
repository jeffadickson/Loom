# Test Facility Operations

A runnable application on Loom for two independent engineering laboratories. Application rules
live in ordinary C# in `examples/Loom.Facility`; the browser host and assets live in
`src/Loom.Demo`. Keeping this example in the framework repository gives it the same locked
build, release packaging and Windows/macOS/Linux verification. It is an initial working
application, not a qualified laboratory information management system.

## Run

From the repository root, after the standard setup/build:

```sh
dotnet run --project src/Loom.Demo -- --facility-demo
```

Open **http://127.0.0.1:5080/facility.html**. The explicit demo flag creates synthetic records,
uses real cookie authentication and enables six local example accounts. The page supplies an
account picker and the demonstration password `Loom-facility-demo!`.

- `north-manager`: registers, reservations, cancellation, release and amendments.
- `north-operator`: start assigned tests, upload evidence and submit results.
- `north-reviewer`: review another person's results, approve or request changes.
- The corresponding `south-*` accounts belong to the separate South laboratory.

The host binds to loopback and enforces its existing host/origin and request-header boundary.
Demo passwords are intentionally public and only enabled by this explicit flag. The flag
refuses to replace configured Accounts or Identity authentication.

The ordinary framework demo uses memory in this mode. Facility records persist separately in
`src/Loom.Demo/data/facility/{tenant}/documents.db`. Set `Facility:DataDirectory` to another
private directory to create a fresh exercise or use an independently restored instance.
The operator's original seeded reservation lasts eight hours after initial setup; cancel it
and make a new request if returning after that interval. Startup does not change existing records.

A framework-dependent release ZIP also includes the application. From its extracted directory:

```sh
dotnet Loom.Demo.dll --facility-demo
```

## Exercise the complete workflow

1. Sign in as **north-manager**. Inspect the instrument, calibration, specimen, procedure and
   campaign under **Facility register**. Register entries are immutable; add revisions to correct them.
2. The initial **TR-001** reservation is ready to run. For another test, choose **New test request**,
   select compatible records and an operator, and reserve a non-overlapping interval. Creation times
   are explicitly entered in UTC; displayed dates use the browser's local timezone.
3. Sign out and sign in as **north-operator**. Open TR-001 and choose **Start test**.
   The server checks the reservation interval, assigned operator and calibration.
4. Choose **Record results**. Enter `20.125`, retain `degC`, add acquisition notes, and attach
   a small CSV or binary evidence file. Optional dataset URIs are references, never fetched by Loom.
   Evidence is limited to 1 MiB per attempt and compressed transparently.
5. Sign in as **north-reviewer**, open the request and choose **Review results**. Download and
   inspect the evidence, record a disposition, then approve. A value outside the procedure's
   inclusive limits cannot be approved: request changes, repeat acquisition as the operator, then
   review the new attempt. Prior attempts and dispositions remain immutable.
6. Sign in as **north-manager** and choose **Queue release report**. In **Reports & queue**, refresh
   after a few seconds and download the released HTML report. It includes exact costs, revision
   references, acquisition evidence hashes and independent review. Browser Print produces a
   paginated document, with the evidence section beginning on a new page.
7. Use **Create amendment** on the released request to schedule a new linked request.
   The original release and evidence remain unchanged. Sign in as **south-manager** to verify that
   its independent request is still scheduled and contains none of North's new data.

Managers can cancel scheduled/running/changes-requested work with a required explanation.
Cancellation releases the reservation without deleting the request or its audit.
Results must be submitted before the reservation expires; an expired run must be cancelled and
scheduled again. Costs and budgets are excluded from operator records and audit snapshots.
Released reports contain costs, so only managers and reviewers can download them.

## Real accounts

Do not use `--facility-demo`. Configure the existing Loom Accounts or Identity host, then:

```json
{
  "Facility": {
    "Enabled": true,
    "DataDirectory": "/private/loom/facility",
    "Memberships": [
      { "UserId": "stable-account-subject-1", "TenantId": "north", "Role": "Manager" },
      { "UserId": "stable-account-subject-2", "TenantId": "north", "Role": "Operator" },
      { "UserId": "stable-account-subject-3", "TenantId": "north", "Role": "Reviewer" }
    ]
  }
}
```

For Identity, UserId is the authenticated stable subject (Identity user ID), not the email address.
Memberships are server configuration and take effect on host restart. Global Admin does not grant
facility membership. One configured role per user per lab is supported in this increment.
The reserved worker identity cannot enter through the HTTP application boundary.

Facility uses its own catalog and storage. Generic `/api/documents` and `/api/tenants` routes
cannot mutate its controlled records. Application HTTP routes are under `/api/facility` and use
the host's existing authentication, request-header checks and error handling. For a production
deployment, use the hardened Identity configuration and deployment boundary documented elsewhere;
this example host remains local.

## Atomicity and durable release

`DocumentService.Atomic` and `TenantSession.Atomic` run synchronous multi-document operations in
one provider transaction. Exceptions escaping the callback roll back documents and audit together.
The scoped capability expires after the callback; do not retain it or start asynchronous work inside it.
Report-design and schema mutations are outside this API.

Reservations are checked and inserted under the SQLite write transaction. Evidence creation and
submission, review and state change, queue creation and state change, and release publication and
job completion each commit together. Optimistic versions reject stale browser commands.

The local worker polls every two seconds. A pending report's snapshot, compressed HTML release,
request transition and job completion are one SQLite transaction. A process failure before commit
leaves it pending; after commit the completed state prevents duplicate publication. No network
side effect is part of the job. Failures are logged, retried at most three times and shown as Failed;
a manager can retry a failed job. `Facility:PauseWorker=true` leaves jobs queued for an operator drill.

This is a small, application-specific durable worker, not a general distributed job scheduler.
It holds a write transaction while rendering a bounded report. External email/upload jobs require
a separate outbox/idempotency design.

## Backup and verification

Use the tenant recovery commands against the **facility data root**:

```sh
dotnet run --project src/Loom.Demo -- --tenant-backup /private/loom/facility north /private/backups/north.zip
dotnet run --project src/Loom.Demo -- --tenant-restore /private/loom/recovered north /private/backups/north.zip
```

A new host pointed at the recovered root can resume queued releases. Account/Identity data and
Data Protection keys still need their own coordinated recovery, as described in TENANCY_RECOVERY.md.

The xUnit application tests cover request-to-release, exact costs, concurrent reservations,
expired calibration, optimistic conflicts, self-review denial, role and tenant isolation,
failed/repeated attempts, immutable evidence, cancellation, amendments, transaction rollback and
expired callback capabilities. Recovery from a pending-job snapshot reproduces the same release
HTML with an injected clock. The Playwright suite performs the real cookie-authenticated workflow,
creates an instrument, uploads evidence, restarts the host with a queued report, downloads the
release, checks mobile layout and confirms the other lab remains unchanged.

```sh
dotnet test tests/Loom.UnitTests --configuration Release
python tools/test_facility.py dotnet
```

Browser verification is included in the normal MSBuild BrowserTest target and CI.

## Remaining scope

The initial fixture is deliberately small. The proposed 50-instrument/200-specimen/1,000-run
scale exercise is not yet a measured acceptance result. Overview currently loads all records,
including decoding evidence before removing binary fields from its response; server-side projection,
paging and streaming are required before large datasets.

Further work includes multi-instrument reservations, membership administration, certificate
attachments, generalized discrepancy/provenance models, native PDF export, OpenTelemetry,
offline acquisition synchronization, TestForge and instrument-library integration, PostgreSQL/
SQL Server facility routing and coordinated account recovery. The application does not claim
electronic signature certification, flight qualification or remote internet deployment.
