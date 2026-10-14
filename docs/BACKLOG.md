# Ordered implementation backlog

This file is tracked with the source; checkboxes mean completed work, not estimates.

- [x] Metadata/catalog, scalar field validation and link targets.
- [x] Central CRUD service, role/action permissions, version conflicts.
- [x] C# calculation hook and read-only value protection.
- [x] SQLite tables, transactional audit, memory adapter.
- [x] Conservative additive migrations and schema-version guard.
- [x] Generated UI, linked pickers, search/sort/page and report/CSV.
- [x] Submit/cancel lifecycle and history.
- [x] C# record generator and validation CLI.
- [x] Typed CRUD/lifecycle/history facade, identity-based property selectors and generated record binding.
- [ ] Build-integrated generation, broader schema diagnostics and query translation (remaining META-07/08).
- [ ] Full metadata diagnostics and constraints (META-06/11).
- [ ] Provider-side query AST and indexes (DB-14).
- [ ] PostgreSQL adapter with live conformance CI (DB-05).
- [x] Stable type/field IDs, exact-name legacy adoption, SQLite field rename/addition plans and caption edits.
- [ ] General legacy identity mapping and DocType renames (remaining MIG-02/05).
- [ ] Declarative backfills, conversion/constraint preflight and safe field retirement (MIG-07–11).
- [x] SQLite plan/apply, transactional ledger, column/index drift checks and rollback.
- [ ] Production CLI, deployment coordination and recovery (remaining DB-11, MIG-12–15/19).
- [ ] Coordinate generated code/API/report references and provider migration conformance (MIG-16–18).
- [x] Demonstrate field rename/addition with populated equipment, retained links/history and full host restart.
- [ ] Complete remaining backfill/conversion/retirement demo acceptance (MIG-20).
- [ ] Real authentication and secure deployment (SEC-02/07).
- [ ] Record/field permissions and tenant boundaries (SEC-03/04).
- [ ] Backup/restore and engine-transfer utilities (DB-16).
- [ ] Child tables and complete document hook lifecycle (META-04/DOC-05).
- [x] Basic visual model editor with migration preview and accepted JSON export.
- [ ] Full model designer (remaining META-09).
- [x] Basic visual table-report designer: palette, drag/reorder, relative widths, alignment, typography, grouping/totals, preview and SQLite save.
- [ ] Advanced report layout: free-position objects, repeated page bands, nested groups and multiple saved layouts (remaining RPT-02).
- [ ] Workflow engine, durable jobs and transactional outbox (DOC-07/08/09).
- [ ] Attachments (UI-06).
- [x] Programmer-defined HTML report writer with grouping, totals, linked titles and browser printing (partial RPT-04).
- [ ] Server PDF generation and advanced pagination.
- [ ] SQL Server and MariaDB adapters (DB-06/07).
- [ ] OpenAPI, OpenTelemetry, CI/deployment and performance gates (API-02/OPS-02/04/05/06).

## v0.3 implementation update

- `AttributeCatalog` reads Document/Field/Grant attributes into the canonical catalog. Explicit permanent IDs survive property renames. `Models:Source=CSharp` selects the supplied equivalent C# example; browser model mutation is blocked in this mode. Source/database mismatch refuses startup.
- `--plan-model`, `--apply-model=<reviewed-id>` and `--export-model` expose local source-model planning/application and inspection. Production deployment/recovery coordination remains open.
- `SavedReports.Run` resolves persisted type/field identities for columns, equality filter and sort. `/api/saved-reports/EquipmentRegister` runs the source-controlled example, enforcing read permission and a 200-row cap. Results return readable column names/captions and row arrays.
- External SQL compatibility views, arbitrary code dependency analysis, full report designer, aggregates/joins and backfills remain open. See README for exact authoring and CLI examples.


## v0.7 implementation update

The visual designer now supports movable static text blocks in report-start and report-end bands, plus repeating printed page header/footer text and optional Page N of M numbering. Saved layouts retain these settings. Band constraints: 120px height, 12 blocks maximum, 500 characters per block, font 9–40px; page header/footer text is limited to 120 characters. Coordinates are validated server-side, HTML text is escaped and CSS generated-content strings use hexadecimal escaping. Images, arbitrary data-bound objects, nested groups, multiple named layouts and server PDF generation remain open. See README for operation and browser-print limits. This supersedes earlier deferrals of these implemented features.


## v0.8 implementation update

Positioned report-start/end blocks support static text, filtered report record count and selected numeric-field sum. `ReportTextBlock` adds `Source` (`text`, `count`, `sum`; default `text` for compatibility) and optional `FieldId` (sum only). Text is the literal prefix for bound values. Numeric sums resolve permanent IDs, exclude nulls and refuse decimal overflow. Invalid/missing/nonselected/nonnumeric bindings and unknown sources are rejected. Saved blocks are evaluated using the same authorized rows as the report table; no expression, SQL or template execution is accepted. Counts/sums are report-wide, not group/page-specific. Individual-record fields, detail-band layouts, images and configurable numeric formats remain open.


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
