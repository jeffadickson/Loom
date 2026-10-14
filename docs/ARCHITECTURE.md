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
