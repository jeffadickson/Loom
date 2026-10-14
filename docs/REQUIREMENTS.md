# Loom — framework requirements v0.6

Working name only. Independent .NET framework project; unrelated to any existing application project. Prepared 2026-10-13. This is a requirements baseline and a tested vertical slice, not a claim of Frappe parity.

## Product objective

Provide a Frappe-inspired, metadata-driven business application platform with the productivity of a visual data dictionary and ordinary C# extension code. One authoritative definition produces storage structures, forms, lists, validation, document APIs, permissions and report inputs. Application developers should concentrate on business rules rather than repeating CRUD code.

Primary users: application developers, administrators configuring approved customizations, staff working in generated applications, and external integration clients. The initial sample is an equipment maintenance desk, deliberately separate from any domain-specific product.

## Decisions

1. JSON DocTypes are authoritative and version-controlled. Generated C# is disposable output, not an independently editable model.
2. C# implements trusted business rules through explicit dependency-injected hooks. User-entered code is not executed.
3. The first release follows design → validate → generate → build → migrate → deploy. Live production schema editing is deferred.
4. Storage is a configurable provider contract. Provider selection is explicit and unsupported providers fail startup. Changing a setting does not migrate existing data between engines.
5. The prototype implements SQLite and volatile memory storage. PostgreSQL is the intended first server database, SQL Server second, MariaDB/MySQL a later adapter. None of these server adapters is represented as implemented.
6. Metadata-driven dynamic storage uses a provider abstraction, not an EF Core model for each generated type in this slice. EF Core remains an option within future adapters and application extensions; its own migration infrastructure must not become a second schema authority.
7. Frappe concepts guide the design. This is newly written code, not a Python translation, ERPNext runtime, Frappe API clone or drop-in replacement.

## Status and priorities

**Done** means implemented in this slice; **Partial** means a deliberately constrained implementation; **Planned** means no implementation claim. P0 is required before any production pilot; P1 is the next general framework release; P2 is a later expansion. Prototype functionality can be Done while its production extension remains Planned.

## Metadata and developer experience

| ID | Requirement | Acceptance criterion | Priority / status |
|---|---|---|---|
| META-01 | Load versioned JSON DocTypes with labels, title field, fields and permission declarations. | Both sample types load; unknown field types, missing targets and case-colliding identifiers fail startup. | P0 / Done |
| META-02 | Support Text, Integer, Decimal, Boolean, Date, Select and Link. | Invalid scalar values fail server validation; valid values round-trip across implemented stores. | P0 / Done |
| META-03 | Support required, unique and server-calculated read-only fields. | Empty required fields, duplicates and client assignments to calculated fields are rejected. | P0 / Done |
| META-04 | Support child tables and aggregate save. | Parent and all children save/roll back together; child ordering is preserved. | P1 / Planned |
| META-05 | Support singleton, tree and virtual/external DocTypes. | Each type advertises its CRUD/query capabilities and appropriate UI. | P2 / Planned |
| META-06 | Support defaults, constraints, precision/scale, descriptions and conditional display. | UI and server agree on values; client conditions never bypass server validation. | P1 / Planned |
| META-07 | Generate C# types from metadata without overwriting handwritten logic. | Generated classes compile; Date maps to DateOnly, Decimal to decimal; properties preserve JSON names. | P0 / Partial: file generator and types implemented; build-time generator integration deferred |
| META-08 | Provide typed repository APIs and schema fingerprints. | Stale generated code cannot silently access an incompatible schema; compile-time APIs expose document identity/version. | P1 / Partial |
| META-09 | Build a visual model designer with source export and reviewable diffs. | An added field produces a deterministic JSON change and migration preview. | P1 / Partial |
| META-10 | Package applications, dependencies and fixtures with semantic versions. | Install/upgrade/uninstall validates dependencies and preserves owned data unless explicitly removed. | P1 / Planned |
| META-11 | Diagnose invalid hook declarations, field names and permission actions. | Build/validation errors identify the model and offending element before deployment. | P0 / Partial: basic catalog validation implemented |

## Storage and migrations

| ID | Requirement | Acceptance criterion | Priority / status |
|---|---|---|---|
| DB-01 | Select storage through configuration and dependency injection. | SQLite and Memory use the same document-service tests; unknown provider fails startup. | P0 / Done |
| DB-02 | Keep SQL dialect and connection management outside the document service. | Loom.Core has no database package dependency. | P0 / Done |
| DB-03 | Provide durable SQLite with real columns per DocType and unique indexes. | Records survive restart; querying database externally exposes named columns. | P0 / Done |
| DB-04 | Provide a volatile transactional memory adapter for isolated tests. | Rollback restores records and audit; restart loses data as documented. | P0 / Done |
| DB-05 | Add PostgreSQL adapter with provider conformance suite. | CRUD, concurrency, decimals, migrations and rollback pass against a real PostgreSQL service. | P0 / Planned |
| DB-06 | Add SQL Server adapter. | Same conformance suite runs against a supported SQL Server release. | P1 / Planned |
| DB-07 | Add MariaDB/MySQL adapter only with a tested version matrix. | Equivalent tests pass; collation and transaction differences are documented. | P2 / Planned |
| DB-08 | Make validation, link checks, writes and audit one atomic transaction. | An injected failure after write/audit leaves neither committed. | P0 / Done for implemented stores |
| DB-09 | Enforce optimistic concurrency with explicit versions. | Eight concurrent updates based on one version result in one success and seven conflicts. | P0 / Done |
| DB-10 | Apply only recognized additive migrations automatically in local demo. | Initial schema and new nullable field work; changed/removal/type/required-field migrations fail closed. | P0 / Done |
| DB-11 | Provide migration plan/apply CLI, deployment lock, checksums and reviewed data backfills. | Production server never mutates schema during ordinary startup; rehearsed upgrade preserves records. | P0 / Planned |
| DB-12 | Parameterize values and allowlist identifiers. | SQL-looking values round-trip; SQL-looking query field identifiers are rejected. | P0 / Done |
| DB-13 | Preserve exact decimal values. | Calculation and persistence do not convert financial values through binary floating point. | P0 / Done: SQLite decimals stored as invariant text |
| DB-14 | Push search, sorting, joins and pagination into providers. | Explain plans and load tests meet stated budgets on large datasets without full-table materialization. | P0 / Planned; demo scans at most 10,000 rows per SQLite collection |
| DB-15 | Advertise provider capabilities instead of assuming identical features. | Unsupported full-text/DDL/isolation operations fail explicitly. | P1 / Partial: capability info only |
| DB-16 | Provide export/import for engine changes and backup/restore commands. | Restore tested against a clean instance; transfer preserves IDs, links, audit and decimals. | P0 / Planned |

## Documents, rules and lifecycle

| ID | Requirement | Acceptance criterion | Priority / status |
|---|---|---|---|
| DOC-01 | Central document service governs CRUD from every transport. | UI and REST cannot bypass normalization, permission or audit. | P0 / Done |
| DOC-02 | Validate links and block deleting referenced records. | Missing targets fail; referenced equipment cannot be deleted. | P0 / Done |
| DOC-03 | Assign stable opaque IDs and server-owned versions/timestamps. | Clients cannot mass-assign those attributes through the values API. | P0 / Done |
| DOC-04 | Implement Draft → Submitted → Cancelled for submittable types. | Illegal transitions fail; submitted/cancelled records reject ordinary editing. | P0 / Done |
| DOC-05 | Provide before-validate, validate, before-save, before-submit/cancel and after-commit hooks. | Failed precommit hooks roll back; after-commit failures cannot undo committed data. | P1 / Partial: synchronous BeforeSave only |
| DOC-06 | Implement an equipment work-order calculation in C#. | Positive bounded hours × nonnegative bounded rate gives rounded estimate; clients cannot override it. | Demo / Done |
| DOC-07 | Support workflow definitions with guards, role approvals, history and amendment. | Two-step approval and rejected approval are tested with no invalid state changes. | P1 / Planned |
| DOC-08 | Add durable jobs and transactional outbox. | Crash/retry does not lose committed events; idempotency prevents duplicate external effects. | P0 / Planned before integrations |
| DOC-09 | Support scheduled jobs, retries, cancellation and dead-letter inspection. | Operators can inspect/retry a failed job with bounded retries and audit. | P1 / Planned |

## Security and tenancy

| ID | Requirement | Acceptance criterion | Priority / status |
|---|---|---|---|
| SEC-01 | Enforce role/action permissions on the server. | Viewer write, editor delete/cancel and unauthorized audit calls fail with 403. | P0 / Done |
| SEC-02 | Integrate real identity: OIDC, secure sessions, service credentials and revocation. | A client-supplied role header cannot grant authority; authentication tests cover expiry and revocation. | P0 / Planned; demo role selector is NOT authentication |
| SEC-03 | Enforce row, field and linked-record visibility in reads, reports, exports and writes. | Hidden fields/rows cannot leak through search, counts, links or CSV. | P0 / Planned |
| SEC-04 | Support isolated sites/tenants with server-derived context. | Cross-tenant IDs, background jobs and caches cannot reveal or mutate another tenant. | P0 / Planned; demo is single-site |
| SEC-05 | Keep before/after audit with actor, action, time and document ID. | Only successful changes appear; history survives deletion. | P0 / Done; database admins can still modify it |
| SEC-06 | Add retention, redaction and tamper-evidence options. | Sensitive fields follow policy; retention is testable and operator changes are traceable. | P1 / Planned |
| SEC-07 | Deploy securely with TLS, CSRF protection, rate limits and secrets management. | Production security review passes and no demo-role configuration is deployable externally. | P0 / Planned; demo binds loopback and requires a custom API header |

## User experience and reports

| ID | Requirement | Acceptance criterion | Priority / status |
|---|---|---|---|
| UI-01 | Generate forms and lists from field metadata. | Equipment and work orders share one renderer, with no handwritten per-type form. | P0 / Done |
| UI-02 | Generate link pickers, selects, dates, Boolean and numeric controls. | Picker stores IDs and displays target titles; invalid input also fails server-side. | P0 / Done; link picker capped at first 200 rows |
| UI-03 | Provide search, sorting, paging and safe text rendering. | Searches work; pagination is bounded; stored HTML renders as text. | P0 / Done |
| UI-04 | Provide record-state actions and change history. | Buttons follow permitted actions; server independently enforces permissions. | P0 / Done |
| UI-05 | Meet WCAG 2.2 AA with keyboard and screen-reader testing. | Accessibility audit covers generated forms, modal focus and error association. | P0 / Partial: labels, focus styling, responsive layout and native dialog; formal audit pending |
| UI-06 | Provide saved views, configurable columns, bulk actions, attachments and comments. | Configurations persist per user; all actions pass normal authorization. | P1 / Planned |
| RPT-01 | Generate tabular report view and safe CSV export. | User can inspect all model columns; CSV neutralizes spreadsheet formulas and quotes values correctly. | P0 / Done, bounded demo export |
| RPT-02 | Build visual report designer with filters, grouping, aggregates and linked fields. | User builds a grouped report without SQL; numeric totals match reference data. | P1 / Partial |
| RPT-03 | Support trusted C# report providers and optional administrator SQL reports. | Parameters are bound; execution is permission-scoped, bounded and cancellable. | P1 / Planned |
| RPT-04 | Add HTML/PDF print layouts, letterheads, page breaks and repeatable headers. | Multipage sample renders consistently with totals and page numbering. | P1 / Planned |
| RPT-05 | Add charts, dashboards, XLSX and scheduled report delivery. | Each delivery uses recipient-specific permissions and explicit scheduling authorization. | P2 / Planned |

## API, operations and quality

| ID | Requirement | Acceptance criterion | Priority / status |
|---|---|---|---|
| API-01 | Expose metadata, CRUD, lifecycle, audit and report endpoints. | HTTP tests cover success, authorization, validation, conflicts and bad identifiers. | P0 / Done |
| API-02 | Publish versioned OpenAPI contracts, generated SDKs and stable structured errors. | Consumer contract tests detect incompatible changes. | P1 / Planned; demo uses plain error messages |
| API-03 | Add webhooks, idempotency keys and safe bulk import. | Retried requests do not duplicate work; row errors are reviewable before import commits. | P1 / Planned |
| OPS-01 | Add model-validation and C# generation commands. | Both operate before app startup without opening storage. | P0 / Done |
| OPS-02 | Provide reproducible builds and provider/UI test gates. | Source package rebuilds and behavioral tests pass using documented commands. | P0 / Done for local validation; hosted CI setup planned |
| OPS-03 | Expose health and standard structured logging. | Health reports provider; no connection secrets appear in responses. | P0 / Partial: basic health and ASP.NET logging |
| OPS-04 | Add OpenTelemetry traces, metrics and correlation. | One API write trace includes authorization, hook and storage spans without sensitive field contents. | P1 / Planned |
| OPS-05 | Add production packaging, recovery runbooks and upgrade rehearsals. | Fresh install, upgrade, failed migration, restore and rollback are demonstrated. | P0 / Planned |
| OPS-06 | Establish performance budgets after provider-side querying exists. | Proposed target: p95 list <300 ms for indexed 100k-row types and 50 concurrent users on a declared reference machine. | P1 / Planned; target is not a benchmark result |

## Automatic schema evolution extension

[Automatic migration requirements](MIGRATION_REQUIREMENTS.md) adds **20 requirements, MIG-01 through MIG-20**, for a shared migration engine. Normal model additions and explicit renames must not require handwritten SQL or an application-specific migration program. The design introduces stable type/field IDs, deterministic planning, data preflight, backfill policies, a migration ledger, provider conformance and deployment/recovery behavior. The SQLite rename/addition/label planner and basic visual editor are implemented in v0.2; the extension document records remaining gaps. The baseline and extension contain 83 numbered requirements in total.

## Release boundaries

**v0.1 demo:** implemented behavior described above, local-only use, generated C# data shapes, SQLite/Memory, no authentication or tenant isolation.

**Next foundation milestone:** complete catalog validation; build-integrated generation; PostgreSQL; query pushdown; explicit migration tooling; automated CI.

**Production pilot gate:** real identity, row/field authorization, tenant policy, secure deployment, backup restore, migrations and provider conformance. No demo-mode promotion by changing a URL.

**Later application-builder scope:** model designer, child tables and workflows/jobs. Visual report building and print-layout editing are deferred; prioritize the developer framework.

## Frappe reference map

Consulted official documentation on 2026-10-13. Inspired behavior is distinguished from compatibility; source wording and Python implementations are not copied.

| Frappe concept | Loom decision | Official reference |
|---|---|---|
| DocType/DocField | JSON metadata defines records and generated UI. | https://docs.frappe.io/framework/user/en/basics/doctypes |
| Controllers and lifecycle | C# hooks, eventually a fuller transaction-aware lifecycle. | https://docs.frappe.io/framework/user/en/basics/doctypes/controllers |
| Document status | Draft/Submitted/Cancelled with immutable submitted records in the first slice. | https://docs.frappe.io/framework/doctypes/docstatus |
| Users and permissions | Start with role/action checks; plan record and field permissions. | https://docs.frappe.io/framework/user/en/basics/users-and-permissions |
| Reports | Separate simple generated view, visual builder and code-defined reports. | https://docs.frappe.io/framework/user/en/desk/reports |
| Hooks/jobs | Explicit trusted extension points; durable execution added separately. | https://docs.frappe.io/framework/user/en/python-api/hooks |
| Architecture/sites | Separate application definition, site context, persistent data and background workers. | https://docs.frappe.io/framework/user/en/basics/architecture |
| Database provider ecosystem | Provider-specific packages require version and capability testing. | https://learn.microsoft.com/en-us/ef/core/providers/ |

## Open decisions

- Final project name and license before public distribution.
- Supported .NET LTS baseline: demo targets .NET 8 for portability; production baseline should move to a supported LTS with a dependency/security review.
- Site-per-database versus shared-database tenant isolation (default proposal: database per site initially).
- Client framework for a larger UI; current renderer is intentionally dependency-free JavaScript.
- Exact decimal precision/scale conventions, collations and Unicode uniqueness rules across providers.
- Whether Frappe DocType import is useful. No Python execution or binary compatibility is assumed.

**v0.2 migration slice:** SQLite field identities, rename/addition/caption plans, atomic application, ledger, drift checks and a basic visual designer with source export. META-09 and DB-11 are partial; production CLI/backfills and other providers remain open. DB-10 now also allows explicit reviewed rename/caption plans.

## Authoring and reporting clarification (2026-10-14)

The implementation supports JSON metadata with generated C# record shapes, or attribute-authored C# models. Attribute-decorated C# properties must be supported as an additional future authoring mode under META-07/08; this is implemented in v0.3 for supported scalar properties. Both authoring modes must compile to the same canonical catalog and migration engine. Each model must declare one authority; a designer must not silently overwrite an authoritative handwritten class. Permanent identities must survive source/property renames through explicit mapping or tracked identity metadata.

Permanent field IDs are internal metadata, not opaque SQL column names. C# properties, SQL columns and report pickers remain readable. Display labels may change without a physical rename. Saved framework report columns, filters, groups and sorts must reference stable field IDs and resolve current names when executing. External SQL cannot be rewritten reliably: expose deliberately stable reporting views/aliases or declare and diagnose a breaking report contract before applying a rename. v0.3 adds persisted report columns, a single equality filter and one sort key by stable identity, with an HTTP example. Grouping and external compatibility views remain unimplemented under RPT-02 and MIG-16.

## v0.4 priority and implementation update

Per the user's direction, defer the report builder (RPT-02) and focus on programmer-facing models, persistence, validation, relationships and schema evolution. Keep existing report functionality without extending a report designer. META-08 is partial: typed CRUD/lifecycle/history, property-based equality/sort, generated-model attributes, identity/version envelopes and schema binding guards now work on Memory/SQLite. Full query translation, build-time diagnostics, navigation properties and build-integrated generation remain open.

## v0.5 report-writing update

The user's subsequent instruction authorizes report writing. Implemented a programmer-defined HTML writer with grouped sections, numeric totals, readable linked titles, permission checks and a download action. RPT-04 is partial: browser printing/Save as PDF is supported; server PDF generation and advanced pagination remain open. The visual report designer (RPT-02) remains deferred. There are still 83 numbered requirements.

## v0.6 visual-layout decision

The user's later explicit instruction supersedes the earlier designer deferral. RPT-02 is now partial: a visual table-layout editor supports field palette, drag/reorder, relative widths, headings/alignment, report typography, grouping/totals, preview and SQLite save. One saved layout per model is supported. Free-position elements, repeating page header/footer bands, multiple layouts and advanced pagination remain open. The requirement count remains 83.


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


## Identity and account lifecycle — critical framework requirement (2026-10-14)

This section supersedes the prototype-only authentication scope. Applications
must not implement their own password cryptography or account recovery tokens.

| ID | Priority | Requirement and acceptance |
|---|---|---|
| ID-01 | P0 | Reusable ASP.NET Core Identity module with durable accounts, unique normalized emails, stable user IDs and separately owned account schema. |
| ID-02 | P0 | Configurable self-registration; unconfirmed accounts cannot sign in. Confirmation links expire, reject tampering and permit resend without exposing tokens in HTTP responses/logs. |
| ID-03 | P0 | Password change requires the current password. Forgotten-password recovery uses emailed expiring tokens; unknown accounts receive the same response; successful reset invalidates previous sessions and token replay. |
| ID-04 | P0 | Administrators create accounts, assign application roles, disable/re-enable accounts and initiate reset email. Disabled users cannot sign in or continue existing sessions. Protect the last active administrator. |
| ID-05 | P0 | Role changes invalidate existing sessions. Stable account IDs map to Loom Actor; client role headers never determine authenticated permissions. |
| ID-06 | P0 | Authenticator TOTP MFA enrollment, verified activation, MFA sign-in, recovery codes and reset flows use Identity implementations. |
| ID-07 | P0 | CSRF protection, lockout, rate limits, HttpOnly cookies, configured secure transport and persistent protected deployment keys. |
| ID-08 | P0 | Account lifecycle UI is included; applications supply branding/configuration rather than rebuilding login, registration and recovery. |
| ID-09 | P0 | Email delivery is a replaceable service. Development capture is explicit, local and prohibited in production. No confirmation/reset tokens in ordinary audit records. |
| ID-10 | P1 | External OIDC login uses standard middleware with explicit authority/client configuration. No implicit cross-provider account linking or automatic administrator grants. |
| ID-11 | P0 | Administrative account actions have durable audit records. Account registration never accepts role/tenant privilege input. |
| ID-12 | P0 | Automated HTTP/browser tests cover registration, confirmation, reset replay, password change, restart durability, disabled sessions, role spoofing, CSRF, MFA and administrator boundaries. |

Team/league/tenant membership remains an application authorization concern;
authenticating an account does not by itself grant access to another tenant.

### Durable account email (ID-13 / DOC-08, first outbox consumer)

- Account creation and its confirmation/setup message must commit or roll back
  together, including built-in registration pages and administrator operations.
- HTTP responses mean **queued**, not delivered. SMTP failure must not lose the
  recovery request or hold the account database transaction open.
- Persist protected message payloads using the application's Data Protection keys;
  no recipient, body, token or raw transport exception in delivery diagnostics.
- Delivery must survive restart, use exclusive expiring leases, bound retries,
  stop before token expiry and erase completed/expired/dead-letter payloads.
- Expose administrator-only delivery metadata and cancellation of unclaimed work,
  protected by antiforgery and recorded in the administrative audit.
- Verify migration from the prior account schema, atomic rollback, restart recovery,
  competing workers, retry timing/exhaustion, expiry and payload protection.
- State the delivery guarantee accurately: SMTP is at least once. A crash after
  mail-service acceptance can cause duplicate email; this is not exactly-once delivery.

This increment implements an Identity-specific email queue. General document outbox
events, durable workflow scheduling and arbitrary application jobs remain open in #24.
