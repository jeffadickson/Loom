# Architecture and database handlers

## Layers

Source JSON bootstraps a new database. On subsequent starts the accepted database catalog is loaded and validated. Designer exports bring that accepted catalog back into source control. `Loom.Core` contains metadata, document service, transaction contract and memory adapter. `Loom.Sqlite` implements real relational tables and migration tracking using Microsoft.Data.Sqlite. `Loom.Demo` is an ASP.NET Core host with an ordinary C# business hook and a metadata-rendered web interface. `Loom.Tests` is an executable behavioral suite, not a mock HTTP demonstration.

An HTTP request resolves its actor → enters DocumentService → checks permission → opens provider transaction → validates values, links and uniqueness → invokes/calculates business rules → saves document with expected version → writes before/after audit → commits. Errors before commit roll back. The prototype BeforeSave hook runs before scalar normalization so it can populate read-only values; all resulting fields are then validated. Hooks are trusted in-process code and must not perform irreversible external effects.

## Provider configuration

```json
{
  "Database": {
    "Provider": "Sqlite",
    "ConnectionString": "Data Source=data/loom.db"
  }
}
```

ASP.NET configuration permits environment overrides: `Database__Provider=Memory`, or `Database__ConnectionString=...`. Provider selection is case-insensitive. Unknown names fail; there is no silent switch to empty memory storage. `Memory` is a real second adapter with transaction rollback but no durability. SQLite uses one connection serialized within a host, WAL and transactions. This is a simple correctness-first demo, not a high-throughput connection-pool design.

Adding a server provider means implementing `IDocumentStore` and `IStoreSession`, registering it in the host, adding configuration validation, and running the same conformance tests against that database. A provider name alone is not an implementation. PostgreSQL, SQL Server and MariaDB must bring their own parameter binding, DDL/migrations, type mapping, transactions, concurrency handling and query translation. Connection secrets belong in deployment secret configuration, never model metadata.

Provider changes select a different database. They do not transfer records. Cross-provider export/import requires an explicit, validated migration tool that is not implemented yet.

## Physical SQLite layout

Each DocType has a named table. Framework columns are `id`, `version`, `state`, `createdAt`, `modifiedAt`; declared fields have their own columns. Integer/Boolean use INTEGER; text/date/link/select use TEXT; Decimal uses invariant TEXT to avoid binary floating-point loss. `_schemas` records the accepted JSON model. `_audit` stores immutable-at-application-level before/after snapshots. Unique fields have unique indexes. Links are checked transactionally by the document service; they are not database foreign keys in this slice. Out-of-band SQL can bypass business rules, so the store must be application-owned.

The core currently materializes documents to implement search/filter/sort/uniqueness and link-delete checking. SQLite refuses scans above 10,000 records; lists return at most 200 per request. This is explicitly temporary. A production provider contract needs a typed query AST, SQL pushdown, indexes and provider-specific performance tests. SQL reports cannot be assumed portable.

## Schema evolution

Types and fields have permanent IDs independent of names. Legacy IDs are adopted using an exact-name deterministic mapping; similar names never imply a rename. The SQLite engine separates plan from apply. Plans include source/target hashes, affected row counts, generated SQL and immutable target metadata held on the server. Apply accepts a plan ID, never client-supplied SQL.

Supported edits are field renames, captions and optional scalar additions. Renames preserve identity and update the metadata title-field reference. `_migrations` records actor, time, checksums and before/after catalog snapshots. Historical audit values retain their original field names; snapshots provide their historical interpretation. Model changes do not increment individual document versions.

DDL, accepted metadata and the ledger commit in one SQLite transaction. Stale plans and detected physical drift fail before mutation; failed ledger writes roll back earlier DDL. Physical checks cover declared columns and unique indexes, not every possible trigger or external database customization. Repeat application is a no-op only while the database still matches that plan's target.

The host serializes requests around catalog changes. Another process with an outdated catalog rejects writes and must restart; coordinated distributed rollout is not implemented. Pending previews are process-local and must be recreated after restart. Known C# hook dependencies cannot be renamed. External clients and handwritten code do not receive automatic aliases or arbitrary dependency analysis.

Required additions/backfills, conversions, field retirement, model renames, generalized recovery, production deployment commands and migration support in other providers remain future work. Normal startup loads the accepted catalog rather than applying source-file differences. Export JSON, regenerate C# and rebuild explicitly. See `MIGRATION_REQUIREMENTS.md` for the complete scope.

## Generated C#

`--generate` writes partial record data shapes under `Generated/`. The next build compiles them. They are not EF entities; their emitted field attributes now let them bind to the shared typed repository facade. Nullable properties represent possibly incomplete drafts; runtime validation enforces required fields. Handwritten hooks remain outside this folder. Runtime-added properties require regeneration to become typed properties; generic values remain available dynamically.

## Security boundary

The demo has no login. A role selector sets `X-Demo-Role`; this intentionally demonstrates authorization with synthetic users. It binds to IPv4 loopback, checks loopback peer/host and requires an API-specific header to block ordinary cross-origin form submissions. These controls do not turn it into production authentication. No externally hosted mode is supplied. Production must replace actor resolution, introduce tenant and field/row policies, and apply deployment hardening before remote access.

The interface renders record values with `textContent`, not HTML. SQL values are parameters. CSV cells are quoted and formula-like prefixes are escaped. Permissions are checked in the document service independently of hidden UI buttons. The UI and APIs intentionally operate against a single site and a bounded dataset.

## v0.3 implementation update

- `AttributeCatalog` reads Document/Field/Grant attributes into the canonical catalog. Explicit permanent IDs survive property renames. `Models:Source=CSharp` selects the supplied equivalent C# example; browser model mutation is blocked in this mode. Source/database mismatch refuses startup.
- `--plan-model`, `--apply-model=<reviewed-id>` and `--export-model` expose local source-model planning/application and inspection. Production deployment/recovery coordination remains open.
- `SavedReports.Run` resolves persisted type/field identities for columns, equality filter and sort. `/api/saved-reports/EquipmentRegister` runs the source-controlled example, enforcing read permission and a 200-row cap. Results return readable column names/captions and row arrays.
- External SQL compatibility views, arbitrary code dependency analysis, full report designer, aggregates/joins and backfills remain open. See README for exact authoring and CLI examples.

## Typed repository (v0.4)

`DocumentService.Repository<T>(actor)` binds decorated properties to accepted catalog fields by permanent identity. Create/save serialize editable properties; reads materialize typed values and a document envelope. CRUD, lifecycle and history delegate to DocumentService. There is no parallel SQL or authorization implementation. Binding validates field coverage, supported scalar shape, public accessors and optional value nullability. Numeric Integer properties use 64-bit long to avoid narrowing after a write commits. Equality and sort selectors resolve only direct properties and use the existing bounded query semantics. Rebind after catalog migration. Reflection-based authoring/materialization and synchronous I/O remain prototype choices, with no trimming/AOT claim.


## v0.9 implementation update: required additions

MIG-07/08/18/20 are now partially implemented for explicit constant backfills on new required, nonunique scalar fields in SQLite. The browser offers required additions and initial values; C# and JSON source authors can pass `--backfills=PATH` to both plan and apply commands. The normalized backfill specification is part of plan identity and migration receipts. Existing schema-only plan IDs remain unchanged. Apply revalidates and rebuilds affected tables atomically, copies old fields by permanent identity, fills new required fields, recreates framework unique indexes and commits metadata plus ledger together. No permanent future-record default is installed. Record identities/versions and old audit snapshots remain unchanged. Records added between preview and apply receive the same reviewed value.

Remaining: optional-to-required conversion, copy-field rules, new-record default policies, type conversions, field retirement, uniqueness/link changes, larger/online migration coordination and additional database providers. Rebuild refuses custom indexes/triggers, database views/physical foreign keys, custom defaults/generated columns and detected custom constraints. See README for exact supported scalar types and CLI use. This update supersedes earlier blanket statements that backfills are unimplemented.


## v0.10 implementation update: optional-to-required and onboarding

MIG-08 now also supports making existing optional, nonunique/noncomputed scalar fields required. Preview counts nulls and validates populated values. Nulls require an explicit constant backfill; fully populated fields need no backfill. Apply repeats preflight inside its transaction, preserves non-null values and fills only nulls before enforcing NOT NULL. A no-backfill plan refuses if new nulls appear. Simultaneous field renaming preserves identity; old audit snapshots remain unchanged. Read-only, unique and Link transitions, type conversions, copy-field backfills and field retirement remain open. This supersedes the earlier optional-to-required deferral.

A new getting-started guide and independently runnable C# console project demonstrate attribute models, typed records, a real SQLite migration, preserved values and required-field validation. The example uses a temporary database and verifies its own results. See `docs/GETTING_STARTED.md` and `examples/Loom.GettingStarted/Program.cs`.


## v0.11 implementation update: exact numeric conversion

MIG-09/18/20 now cover Integer → Decimal and Decimal → Integer for nonunique, noncomputed fields. Preflight checks every populated value and reports invalid counts; apply repeats preflight under its transaction. Narrowing requires whole values within signed 64-bit range. Conversion avoids floating-point intermediates, preserves nulls, and can combine with renaming and an explicit required-field backfill. Failure after copying still rolls back the entire migration. Other type pairs, custom rounding/truncation and precision/scale rules remain open. Conversion SQL uses provider-registered functions and must be applied through Loom. The C# getting-started program now demonstrates both success and refusal. This supersedes earlier blanket deferrals of numeric conversions.

## v0.12 implementation update

Money, explicit decimal precision/rounding, text/date/select conversions, required-to-optional migration, Binary with transparent SQLite compression, and external Uri references are implemented. The API transports numeric field values as strings. The new `--memory-demo` mode runs seeded test results without database setup. See [DATA_TYPES.md](DATA_TYPES.md) for authoritative contracts and remaining limits; this supersedes earlier deferrals of these features. Streaming/object storage, time-series querying/adapters, FX, additional durable providers and production authentication remain open.

## EF Core boundary

PostgreSQL and SQL Server use EF Core behind the existing `ServerDocumentStore`
API. EF maps the fixed schema, document and audit tables, translates their LINQ
queries, performs writes, manages serializable transactions, and checks the
original document version on updates and deletes. Each transaction gets a fresh
context; writes flush inside the transaction so subsequent session reads see them.
Loom continues to own authorization, validation, hooks, audit policy, scientific
values and application metadata. Decimal money and binary payload semantics remain
in Loom's serialized document representation.

Existing server databases keep their table names, columns and JSON representation.
Bootstrap DDL is retained deliberately: `EnsureCreated` is not an adoption or
migration mechanism for an existing database. Application catalog changes still
fail closed until explicit server schema migration support exists. EF does not
currently map each dynamic application field to a relational column: numeric-only queries now use parameterized database-side filtering, sorting,
counts and paging. Supported types are Integer, Money and fixed-precision Decimal;
PostgreSQL also supports unrestricted Decimal. Search, text sorts/filters and
unrestricted Decimal on SQL Server retain the bounded reference scan to preserve
comparison semantics. Indexes and the remaining query shapes are open work. SQLite retains its existing stable-field-ID migration and query engine.
Do not run two independent migration engines against the same schema.

Testcontainers checks both engines against the shared repository contract and
additional tests for legacy table adoption, direct stale writes/deletes, recovery
after a failed insert, read-your-writes, concurrent connections and schema drift.


Numeric server queries extract JSON values using provider-specific SQL through
EF Core `FromSqlRaw`. All field paths, values, document kinds and page bounds are
parameters; only validated type definitions and fixed SQL operators form SQL text.
Explicit null ordering and ID tie-breaking match the in-memory implementation.
Count and page execute inside the same serializable store transaction. This removes
the 10,000-document application scan cap for supported query shapes, but JSON
extraction can still scan the database table; this is not a claim of indexed access.


## Core account-management architecture (2026-10-14)

`Loom.Identity` owns the reusable ASP.NET Core Identity integration. Microsoft
Identity owns password hashing, lockout, confirmation/reset tokens, authenticator
MFA and account-management Razor Pages. Loom adds configuration, durable EF
storage, application-role claims, disabled-account enforcement, administrative
operations and audit records. The previous configured-password Accounts mode is
a compatibility demonstration; it is not the full account lifecycle.

Identity is a host capability, not a dynamically editable DocType. Its EF account
schema and deployment keys are separate from scientific/business document data.
The first implementation uses a dedicated SQLite account database, including when
the document provider is Memory, PostgreSQL or SQL Server. Provider expansion must
have its own Identity migration/conformance tests; document-provider configuration
must never silently relocate accounts.

Self-registration is opt-in and creates an unprivileged account. Email verification
is required. Roles are assigned by an administrator, and a missing role maps only
to Viewer. Actor IDs use Identity's stable user ID, not a mutable email address.
All document permissions remain enforced by DocumentService. Account enablement,
security stamps and role changes are checked on requests so existing cookies cannot
retain revoked access. Administration runs in a database transaction with audit.
The last active Admin cannot be disabled or demoted.

The included account UI is Identity's Razor Class Library, with its antiforgery
validation, backed by Loom's sender service. Email destinations are taken from
verified account state where appropriate; confirmation/recovery links use a fixed
configured public origin rather than an arbitrary request Host. SMTP is configurable;
explicit Development-only pickup supports local tests without sending email.
Data Protection keys are persisted to a dedicated directory. Production must use
HTTPS and protect that directory with deployment ACLs/encryption at rest. Cookie
and recovery-token isolation uses a configured Data Protection application name.

OIDC is optional external authentication through Microsoft's middleware. External
identities do not inherit Loom Admin privileges. Account linking remains the
explicit Identity flow. League/team membership, invitations and tenant isolation
are separate authorization requirements, not claims of identity-provider parity.

### Identity email outbox

Account email uses the dedicated Identity database rather than the document store.
The scoped `IEmailSender` queues a Data Protection-protected envelope. A page filter
wraps email-producing Identity POST handlers in a database transaction; administrative
creation/reset endpoints also explicitly transact account/audit/queue writes. The
transport runs only after commit in a background worker. Applications replace
`IIdentityEmailTransport` to use another mail provider; replacing `IEmailSender`
would bypass this durability contract.

The dispatcher claims one due row with a compare-and-set update and a unique lease
token. A lease lasts 120 seconds; each delivery has a 30-second cancellation budget.
A worker processes at most ten envelopes per pass, with a two-second idle interval.
No SQLite write transaction is held during network delivery. Completion updates
require the same lease token, preventing stale workers from acknowledging a newer
claim. Cooperative cancellation leaves the lease for recovery after restart.

The five-attempt budget uses one-, two-, four- and eight-minute retry delays.
Unclaimed envelopes expire 55 minutes after creation, before the default one-hour
Identity token expiry. Sent, cancelled, expired and dead-letter rows discard their
protected payload; safe metadata is retained for seven days. Lost Data Protection
keys cause a sanitized PayloadUnavailable dead letter. Operators request a fresh
confirmation/reset rather than reviving an expired token.

SMTP cannot participate in the database transaction. A crash between acceptance and
acknowledgement can duplicate the same envelope, so delivery is at least once.
Custom transports receive a stable envelope ID for provider-side deduplication.
Development pickup files use that ID and atomic publication, making repeated pickup
delivery idempotent. This is not a claim of exactly-once SMTP or inbox delivery.

The additive EmailOutbox EF migration preserves existing users and audit records.
Delivery diagnostics expose only status, attempt count, timestamps and fixed failure
codes. Cancellation requires Admin and antiforgery validation and refuses an active
lease. The general document outbox and scheduler remain separate framework work.

## Tenant isolation and recovery

The first tenant architecture is **database per tenant**. This avoids relying on every
query and future report remembering a row discriminator. TenantWorkspace owns routing;
TenantSession is an authorized capability with a private DocumentService/store. The host
supplies a trusted membership resolver keyed by stable user ID and tenant slug. Sessions
refresh membership and role at each operation, while existing row/field access policies
apply within the selected database. There is no global-Admin bypass.

Tenant identifiers are restricted to lowercase path-safe slugs and an explicit allowlist.
Provisioned databases carry an immutable physical tenant binding. Existing unbound
single-workspace databases are not silently adopted. Each host checks its expected catalog
at provisioning; mismatches require reviewed migration. Tenant backups contain the complete
document database, including report layouts, audit and migration history.

Recovery uses SQLite online backup to a private staging directory, hashes the checkpointed
snapshot and packages it with a versioned manifest. Restore permits exactly two known ZIP
entries, bounds decompression, verifies tenant/checksum/schema/integrity, and moves the
validated database into a new destination without replacement. Backup files are sensitive;
checksums are not signatures. Restore is an offline operator command, never an ordinary
tenant HTTP action. A recovery drill restores to a separate root and runs representative
authorized reads before switching hosts.

The demo's original /api/documents workspace remains separate from /api/tenants/{tenant}.
Do not put tenant-owned data in the original workspace. Production applications should
expose only tenant routes when all their data is tenant-owned. Shared Identity accounts,
email outbox and Data Protection keys require a separate coordinated backup policy.
There is no cross-provider transfer, identity backup or automatic fleet migration claim.

## Facility application and atomic document operations

The example application in `examples/Loom.Facility` owns its catalog, immutable register revisions,
scientific validation, reservation rules, review independence and release workflow. The existing
demo host supplies cookie/Identity authentication and the `/facility.html` browser UI; a separate
TenantWorkspace prevents generic document routes from bypassing application state transitions.

`DocumentService.Atomic` wraps the provider session in a transaction-bound service; nested document
operations retain the ordinary validation, role/field policies and audit behavior without opening
nested provider transactions. The wrapper expires after the callback. `TenantSession.Atomic`
retains membership resolution on each operation and disallows report-design mutation in the callback.

Facility release jobs use the same tenant transaction for immutable HTML snapshot creation and
completion. This supports restart recovery and concurrent-worker rechecks without external side
effects. It is intentionally bounded local report work, not the generalized scheduler in issue #24.
See FACILITY_OPERATIONS.md for the security boundary, role matrix and remaining scale limitations.

## Query projection and form metadata

DocumentQuery.SelectFieldIds uses schema identities to separate the read shape from filter/sort
fields. Validation and authorization precede provider execution. SQLite reads explicit columns;
server JSON stores strip omitted properties before EF materialization; Memory copies selected
values only. Null selection preserves compatibility; empty selection supports identity-only
existence checks. Saved reports select their own columns. See QUERY_PROJECTIONS_FORMS.md for
server fallback limits and the distinction between returned bytes and physical storage reads.

DocumentService.Describe supplies a role-filtered DocumentForm contract. The shared browser form
renderer consumes field definitions directly; application workflow actions remain handwritten C#.
Facility lists are bounded, with separate totals and on-demand request detail.

## Application workflows and transactional jobs

Core's IApplicationSession binds authorized operations to atomic scopes for every document provider;
TenantSession implements it with fresh membership checks. ApplicationWorkflow applies stable-field
state transitions and trusted C# guards/effects within that scope. TransactionalJobs extracts Facility's
persisted database-only worker pattern, including version fencing, retry limits and failure accounting.
The application owns the job schema, host polling and domain rules. Identity's external email outbox
remains separate. See [application workflows and jobs](APPLICATION_WORKFLOWS_JOBS.md) for examples,
transaction boundaries and remaining scheduler/outbox requirements.

## External delivery outbox

DeliveryOutbox complements TransactionalJobs: its claim/acknowledgement transactions are short, and
asynchronous external I/O happens between them. Application-owned metadata defines queue storage for
all document providers. Version checks and random lease tokens fence stale workers; UTC millisecond
deadlines support bounded candidate queries, scheduling, capped backoff and abandoned-claim recovery.
HttpOutboxTransport posts to one host-configured endpoint with a stable Idempotency-Key. Receiver
cooperation is required for duplicate suppression. See [delivery outbox](DELIVERY_OUTBOX.md) for the
API, authorization, audit-payload retention, host integration and remaining scheduler scope.
