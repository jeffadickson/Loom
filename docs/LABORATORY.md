# Laboratory register and framework verification

Run `python tools/dev.py setup`, then `python tools/dev.py run`. Open
http://127.0.0.1:5080/laboratory.html. The in-memory host seeds instruments,
specimens, results and a linked laboratory run. Use Manage runs or the navigation
links to create and edit records with the framework-generated forms. The run
view includes units/uncertainty, waveform downloads, external series references
and an HTML report suitable for printing. External references are stored links;
Loom does not fetch the referenced time-series database.

## Persistent providers and migrations

Choose `Database__Provider=PostgreSql` or `SqlServer` and supply
`Database__ConnectionString`, then run the demo DLL with `--science-demo`.
Existing server databases require a reviewed migration to add the scientific
models. Use `--science-demo --plan-model`, inspect the plan, then
`--science-demo --apply-model=<plan-id>` with the same configuration.
For required additions supply `--backfills=<json-file>` containing
`[{"fieldId":"<permanent-id>","value":"initial value"}]`.

Server migrations support adding document types, adding optional or explicitly
backfilled required scalar fields, renaming fields by permanent ID, captions,
and model-version changes. Removing fields/types, type conversions, changing
constraints or adding links to existing types remain blocked. Increment the
changed type's model version. Existing record IDs and values survive, versions
increment, and historical audit snapshots retain their original schema.
Stable-ID report definitions resolve the new names automatically.

Apply locks the accepted schema, transforms records in 200-row batches inside
one transaction, rebuilds numeric indexes and records a migration receipt.
Failure rolls back every batch, the schema and the receipt. Plans are held by
the server (at most 64), tied to the source schema and revalidated on apply.
Reapplying a committed plan is idempotent while its target remains current.
CLI plan IDs are deterministic; the CLI re-previews before apply after restart.

## Accounts and access policies

Default Demo mode remains a clearly marked, loopback-only role simulation.
Set `Authentication__Mode=Accounts` to require real password/cookie sign-in.
Configure each account using environment variables (or an uncommitted local
configuration file):

- `Authentication__Accounts__0__Id=analyst`
- `Authentication__Accounts__0__Role=Editor`
- `Authentication__Accounts__0__PasswordHash=<ASP.NET Identity password hash>`

Generate a hash with the built demo DLL's `--hash-password` option; enter the
password on standard input, not as a command-line argument. Do not commit
passwords or account configuration. Accounts mode fails startup with no accounts.
There are no default passwords. Password hashes use ASP.NET Identity and cookies
use ASP.NET Data Protection, expire after an hour and are HttpOnly/SameSite Strict.
Login is rate-limited. Role headers are ignored in Accounts mode.
This host remains loopback-only HTTP; an internet deployment needs HTTPS, secure
cookies, managed key persistence, account provisioning/recovery and operational
security review. It is not an identity-provider replacement.

`IDocumentAccessPolicy` composes trusted application scopes and field permissions
with role grants. Scopes run before counting/paging; direct reads, updates,
deletes, transitions, links, audit, downloads and reports enforce access.
Restricted field queries/search are rejected to prevent value inference; read
values are removed, and writes to restricted fields are denied. Hidden values
survive edits to other fields. Hooks remain trusted application code.

The laboratory example restricts LabRun to its Operator account, with an Admin
bypass. TestResult Cost is Admin-only. Instruments, specimens and results are
otherwise shared by the catalog's read grants; linked records do not implicitly
inherit a run's ownership. The seeded run belongs to account `analyst`.

## Query execution and verification

Server-side paging/filtering supports numeric and text-like fields (including
links, dates and URIs). Text ordering matches UTF-16 ordinal ordering. PostgreSQL
uses an immutable ordinal-key function; SQL Server uses BIN2 collation and
OPENJSON to avoid JSON_VALUE's 4,000-character limit for text. Numeric fields
have PostgreSQL expression indexes and SQL Server persisted computed-column
indexes. Audit lookup has a document index. Uniqueness/reference checks use
bounded provider queries instead of materializing a collection.

Case-insensitive substring search, Boolean sorting and unrestricted Decimal on
SQL Server still use the bounded reference fallback. No claim is made that all
query shapes are indexed or that the framework has production-scale performance
budgets. Database tests include a 10,005-record paging case; timings vary by host.

`python tools/dev.py test`, `browser` and `database` exercise the shared contracts,
cookie/permission HTTP tests, browser flows and PostgreSQL/SQL Server containers.
The migration rollback test fails after a full 200-record batch and checks that
records, accepted schema and ledger remain unchanged.

Better Design guidance informed the register/detail hierarchy, copy, keyboard
controls and mobile navigation. Browser checks cover 390, 1440 and 1728 pixels.
This local register has no public Better Design dashboard approval receipt.
