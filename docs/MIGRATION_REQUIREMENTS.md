# Automatic schema evolution — requirements v0.2

Added 2026-10-13; implementation status updated 2026-10-14. These requirements extend DB-10/11/16 and META-07/08/09. The full acceptance criteria remain the target; v0.2 implements a limited SQLite slice.

| IDs | v0.2 status |
|---|---|
| MIG-01, 06 | Implemented permanent identities and optional field additions. |
| MIG-02 | Partial: exact-name legacy adoption; no interactive ambiguous mapping tool. |
| MIG-03–05 | Partial: deterministic supported-edit plans, captions and field renames; no general layout editor or DocType rename. |
| MIG-07–11, 17, 19 | Planned: defaults/backfills, conversion/constraint preflight, retirement, custom transformations and production recovery commands. |
| MIG-12–14 | Partial: SQL/row-count preview, checksums, column/index drift checks and ledger under SQLite transactions; no lock-duration estimates or distributed deployment coordinator. |
| MIG-15 | Implemented for supported SQLite steps; injected post-DDL failure verifies atomic rollback. Other providers remain unimplemented. |
| MIG-16 | Partial: title references, schema hash, known-hook guards and explicit JSON export; C# generation/rebuild remains manual, no external API compatibility layer. |
| MIG-18 | Partial: real SQLite conformance for this slice; backfills/constraints and server providers remain open. |
| MIG-20 | Partial: rename/caption/addition, retained values/links/history, restart and rollback demonstrated; later backfill/conversion/retirement scenarios remain open. |

## User experience

An application developer edits a model. Loom detects the changes and generates a reviewable migration plan. Routine additions, label changes and explicit renames require no handwritten SQL or C# migration program. The framework's shared engine applies generated steps using the configured database provider. Custom code is reserved for transformations that need application-specific knowledge.

Development databases may opt into automatic application of safe changes. Production deployment applies a reviewed plan explicitly. Unchanged deployments do nothing. Opening an ordinary form never alters the schema.

## Requirements

| ID | Requirement | Acceptance criterion | Priority |
|---|---|---|---|
| MIG-01 | Assign permanent IDs to DocTypes and fields, independent of display labels, source property names and SQL identifiers. | Renaming a model/field retains its ID; a distinct new field receives a new ID; duplicate IDs fail validation. | P0 |
| MIG-02 | Bootstrap permanent IDs for existing v0.1 models without recreating columns. | An explicit adoption plan records the existing name-to-ID mapping; existing IDs, values, links and audit are unchanged. | P0 |
| MIG-03 | Generate a deterministic diff between desired metadata and the database's last accepted schema. | Equivalent inputs produce the same ordered plan/checksum; the plan identifies additions, renames, metadata edits, removals and conversions. | P0 |
| MIG-04 | Treat display-label/layout changes as metadata changes. | Changing the caption from Serial number to Asset tag updates the form without changing values or executing column DDL. | P0 |
| MIG-05 | Generate field and DocType renames using stable identity or an explicit developer mapping. Never infer identity solely from similar names. | A populated SerialNumber field becomes AssetTag with identical values; an ambiguous legacy rename asks for a mapping instead of guessing. | P0 |
| MIG-06 | Generate nullable field additions without custom code. | Existing records receive null, row count stays constant and existing values are unchanged. | P0 |
| MIG-07 | Distinguish an existing-record backfill from a new-record default. | Setting a default for future records does not silently overwrite old records; each choice is visible in the plan. | P0 |
| MIG-08 | Support required-field additions and optional-to-required changes with declarative backfill rules. | The planner reports missing values, accepts a constant/copy-field rule, validates all rows and enforces the constraint only after successful backfill. | P0 |
| MIG-09 | Preflight type, precision, scale, length and collation changes. | Report affected counts and authorized sample errors; unsafe narrowing, overflow, truncation and lossy conversions are blocked unless an explicit supported transformation is chosen. | P0 |
| MIG-10 | Preflight uniqueness, select-option and relationship changes. | Duplicate values, removed options in use and orphan links are detected before applying constraints; provide a correction plan rather than discarding records. | P0 |
| MIG-11 | Preserve removed fields by default through a documented retirement policy. | Removing a field from a form/model can hide/retire it without physical deletion; purge is a separate reviewed destructive operation with backup/retention requirements. | P0 |
| MIG-12 | Display a human-readable plan and provider-specific SQL preview. | Show affected types/fields, row counts, backfills, expected locks, destructive steps and transactional capability; secrets and unauthorized field values are not exposed. | P0 |
| MIG-13 | Detect drift and stale plans at application time. | A changed source schema, checksum mismatch, already-applied conflicting migration or unrecognized physical database alteration causes a clear refusal. | P0 |
| MIG-14 | Record an ordered migration ledger and serialize migration execution. | Exactly one deployment owns the database migration lock; successful IDs/checksums are recorded once; rerunning a completed plan is a no-op. | P0 |
| MIG-15 | Apply atomic migrations where the provider supports them, with explicit recovery for nontransactional steps. | Injected failure leaves schema/data/ledger unchanged for atomic plans; unsupported atomic operations are refused or use an explicit checkpoint/recovery design. Never claim rollback solely because a transaction API exists. | P0 |
| MIG-16 | Coordinate model, generated C#, API contracts, reports, hooks and database versions. | Metadata-owned references follow field IDs; generated types refresh; renamed handwritten C# references fail compilation or diagnostics; incompatible clients require aliases/versioning or a declared breaking change. | P0 |
| MIG-17 | Supply a trusted custom transformation extension for business-specific conversions. | An application can split a full name using a reviewed rule; dry-run, row errors and transactional boundaries are supported. The system does not guess name-splitting semantics. | P1 |
| MIG-18 | Gate provider support on real conformance tests. | SQLite and each future server provider demonstrate rename, backfill, constraint change, restart, rollback/recovery and drift detection on a real database. Unsupported steps fail before mutation. | P0 |
| MIG-19 | Provide deployment and recovery commands with maintenance coordination. | Document plan/apply/status, preflight backup checks, maintenance/write exclusion, restore rehearsal and forward-repair behavior; irreversible transformations are not represented as automatically reversible. | P0 |
| MIG-20 | Demonstrate ordinary schema evolution using the equipment example without handwritten migration code. | The walkthrough below passes on SQLite with retained values, relationships and readable history; changing database providers is separately explained as data transfer, not schema migration. | P0 |

## Demonstration acceptance scenario

1. Create two equipment records with different serial numbers and linked work orders; record IDs, values and audit history.
2. Change only the serial-number display label. Inspect the generated plan: no data rewrite or column rename.
3. Rename the underlying field to AssetTag while retaining its stable ID. Preview and apply the generated rename; verify both values, record IDs and work-order links remain unchanged.
4. Add optional WarrantyExpires. Existing records show an empty value; edit one record to set a date and verify it persists.
5. Add required Site. Choose Lab A as the existing-record backfill and independently choose a new-record default. Verify both policies separately.
6. Attempt to convert a populated text field containing both numbers and non-numeric text to Integer. Preflight identifies the invalid row and leaves the database untouched.
7. Attempt a uniqueness constraint with duplicate values. Preflight refuses it and identifies the duplicates to an authorized operator.
8. Retire WarrantyExpires. Its populated value remains recoverable. Purging it requires a distinct reviewed operation.
9. Reapply the same migration set: no duplicate work and no value changes.
10. Inject a failure during an atomic migration; verify schema, records and ledger are restored together.

Audit snapshots must remain interpretable after a rename. Retain the historical schema version/name mapping or stable field IDs with snapshots; do not rewrite history merely to match current captions. A migration ledger records the actor/deployment, source/target versions, plan checksum, result and time. Backfills need traceable provenance without generating misleading user-edit events.

## Data-safety and scope notes

Stable IDs solve identity, not every dependency. Database indexes/constraints, generated properties, saved reports, application code and external clients must be considered. Physical SQL renaming may be unnecessary when a provider deliberately uses stable internal column names; the public result and preservation guarantees still apply.

Preflight and apply must not race with incompatible writes. Use the provider's schema/transaction locks or a maintenance mode appropriate to the operation. Large backfills need bounded batches, progress, cancellation and a resumable strategy; the initial production implementation may require maintenance downtime rather than promise online migration.

This engine is shared framework infrastructure. Normal application authors supply models and review plans. They should not write a separate migration application. Switching SQLite to PostgreSQL requires explicit export/import or transfer tooling; selecting a new connection string does not copy the old database.

## v0.3 implementation update

- `AttributeCatalog` reads Document/Field/Grant attributes into the canonical catalog. Explicit permanent IDs survive property renames. `Models:Source=CSharp` selects the supplied equivalent C# example; browser model mutation is blocked in this mode. Source/database mismatch refuses startup.
- `--plan-model`, `--apply-model=<reviewed-id>` and `--export-model` expose local source-model planning/application and inspection. Production deployment/recovery coordination remains open.
- `SavedReports.Run` resolves persisted type/field identities for columns, equality filter and sort. `/api/saved-reports/EquipmentRegister` runs the source-controlled example, enforcing read permission and a 200-row cap. Results return readable column names/captions and row arrays.
- External SQL compatibility views, arbitrary code dependency analysis, full report designer, aggregates/joins and backfills remain open. See README for exact authoring and CLI examples.


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
