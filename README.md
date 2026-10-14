# Loom v0.12 — a Frappe-inspired .NET framework prototype

Working name; independent project. Define records with C# attributes or JSON and obtain a working equipment/work-order application: forms, lists, persistence, REST endpoints, validation, permissions, document states, reports and history. C# supplies custom behavior. The prototype is not a compatible port of Frappe and does not execute Python applications.

## Build and development

See [development setup](docs/DEVELOPMENT.md) for checksum-verified local tool setup,
locked dependencies, Windows/macOS/Linux verification, debugging and release ZIPs.
From a clone with Python 3.11.8+ installed:

```sh
python tools/dev.py setup
python tools/dev.py test
python tools/dev.py run
```

Add `--browser` to setup for the browser tooling. Database tests additionally need Docker.

## Browser demos

Start with [the interactive introduction](Loom_Introduction.html). Download it for a self-contained browser simulation, or run the memory demo and open `/introduction.html` for the same walkthrough using the real C# API. It includes model attributes, a generated form, exact-money validation, role checks, query paging and audit history.


Download and open either self-contained HTML file in your browser:

- [Scientific results preview](examples/browser/Loom_Science_Demo.html): synthetic thermal runs, quality masks, unit conversion, waveform inspection and a verified lossless compression test. Its scientific backend features are proposals, explicitly listed in the preview. This JavaScript preview does not run the C# framework.
- [Equipment/work-order preview](Loom_Browser_Demo.html): earlier standalone UI demonstration.

For the actual C# application with disposable in-memory data, run `dotnet run --project src/Loom.Demo -- --memory-demo` and open http://127.0.0.1:5080.

## Getting started

Start with [the getting-started guide](docs/GETTING_STARTED.md). It includes a complete runnable C# program, expected output and a browser walkthrough. From this directory:

```sh
dotnet run --project examples/Loom.GettingStarted
```

The sample defines models using attributes, creates two instruments in a temporary SQLite database, makes Site required, fills only the missing value, preserves the existing site, and validates a new record. It checks its results and removes its temporary database. No JavaScript programming is needed.

For the equipment/work-order UI, use the command below. Release sections later in this README document the project's progression; the latest sections supersede earlier feature deferrals.

## Run

Requires the .NET 8 SDK and internet access for NuGet package restore. Run from this repository root:

```sh
dotnet run --project src/Loom.Demo
```

Open **http://127.0.0.1:5080**. The application binds to loopback only. Demo accounts are simulated by the role selector; this is not authentication. Three equipment records and one work order are seeded when initializing a new database.

On Windows PowerShell the same `dotnet run` command works. To use disposable storage:

```powershell
$env:Database__Provider = "Memory"
dotnet run --project src/Loom.Demo
```

On Linux/macOS:

```sh
Database__Provider=Memory dotnet run --project src/Loom.Demo
```

SQLite is the default; edit `src/Loom.Demo/appsettings.json` or use `Database__ConnectionString` to configure its location. Relative paths are relative to the application's working directory. Changing providers does not move data. `LOOM_PORT` changes the loopback listening port if 5080 is occupied.

## Five-minute walkthrough

1. Equipment: create a record with a unique asset tag. Open an existing record and inspect its change history.
2. Work orders: create one linked to your equipment. Enter 2.5 hours at $120/hour. The C# hook supplies an estimate of $300.
3. Reopen it, change the hours and save. History shows the before/after values and actor.
4. Submit the document. Ordinary editing is now locked. Admin can cancel; Editor cannot cancel.
5. Select Viewer. Creation controls disappear and direct write requests are rejected server-side.
6. Use Report, search, sorting and CSV export. Model shows the same definitions used to generate the form.
7. Restart: SQLite retains changes. Memory intentionally does not.

`Loom_Browser_Demo.html` in the release package is a separate offline UI demonstration for immediate exploration. It uses JavaScript and browser storage, not the C# server; use the command above for the real framework.

## Validate and generate types

```sh
dotnet run --project src/Loom.Demo -- --validate
dotnet run --project src/Loom.Demo -- --generate
dotnet build src/Loom.Demo
dotnet run --project tests/Loom.Tests -- .
```

Generated files are under `src/Loom.Demo/Generated`. Metadata owns their definitions. Generated records carry field identities and work with the typed repository API.

## Change a model without writing a migration

On SQLite, choose **Admin → Equipment → Edit model**:

1. Rename `tag` to `serialNumber` and change its caption to “Serial number”.
2. Change the `location` caption to “Room”.
3. Add an optional Date field named `warrantyExpires`.
4. Click **Preview changes**. Inspect the generated steps, affected row counts and SQL.
5. Click **Apply**. Existing tag values stay intact; the new date starts empty. Links, record IDs and audit history remain intact.
6. Enter a warranty date, save, and restart the server. The accepted model and data persist.
7. Use **Export model** to download accepted JSON definitions. Copy these into `src/Loom.Demo/Metadata`, run `--generate`, and rebuild to update the C# data shapes.

JSON files bootstrap a fresh database. After initialization, the database's accepted catalog controls the running application; editing source JSON alone does not overwrite it on restart. Export is explicit, so browser changes do not silently rewrite your source files or recompile C#.

The shared C# engine generates ordinary field renames, caption edits and optional additions. It uses permanent field IDs, transactional SQL and a migration ledger. Required-field backfills, type conversions, removals and DocType renames remain blocked. Fields used by the example's calculation hook cannot be renamed. Memory has no live designer in this release.

## API example

```sh
curl -H 'X-Loom-Demo: local' -H 'X-Demo-Role: Viewer' \
  http://127.0.0.1:5080/api/documents/Asset
```

See [API](docs/API.md), [requirements](docs/REQUIREMENTS.md), [architecture](docs/ARCHITECTURE.md), [validation](docs/VALIDATION.md) and [backlog](docs/BACKLOG.md).

The requirements now include [20 automatic migration requirements](docs/MIGRATION_REQUIREMENTS.md): ordinary field additions and explicit renames should produce generated migration plans, with no handwritten migration program. The migration document distinguishes the implemented SQLite slice from the remaining requirements.

## Implemented providers

| Provider | Durable | Status |
|---|---|---|
| SQLite | Yes | Implemented and exercised against a real SQLite database |
| Memory | No | Implemented with rollback; tested using the same service cases |
| PostgreSQL | — | Specified, not implemented |
| SQL Server | — | Specified, not implemented |
| MariaDB/MySQL | — | Specified, not implemented |

## Important prototype limits

No real login, tenant isolation, field/row permissions, a full model designer, durable jobs, generalized workflow engine, PDF designer, child tables or production deployment. Queries use bounded in-process scans. Metadata migrations are intentionally conservative. Generated types need an explicit generate/rebuild. There is no Frappe/ERPNext compatibility promise. The requirements distinguish implemented features from the full product scope.

Dependencies: ASP.NET Core/.NET 8; Microsoft.Data.Sqlite and its transitive SQLite runtime packages. No Frappe source code was copied. Final project naming and public licensing are open decisions.

## C# attribute authoring (v0.3)

The working examples are in `src/Loom.Demo/Models/EquipmentModels.cs`. Set `Models:Source` to `CSharp` in appsettings, or on Linux/macOS:

```sh
Models__Source=CSharp dotnet run --project src/Loom.Demo
```

PowerShell: set `$env:Models__Source = "CSharp"` before the ordinary run command. The default remains `Json`. Both modes compile to the same validated catalog and database engine; the included definitions are tested for exact equivalence. CSharp mode disables the browser model editor and rejects startup if its compiled catalog differs from the accepted database schema.

```csharp
[Document("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", nameof(SerialNumber))]
[Grant("Admin", "read", "create", "write")]
public class Equipment
{
    [Field("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Label = "Serial number", Required = true)]
    public string? SerialNumber { get; set; }
}
```

Use newly generated GUIDs (32 hex digits without hyphens) for real models/fields. Keep those IDs when renaming. `Name` optionally decouples a C# property name from its database/API field name; changing only the property while preserving `Name` needs no column rename. `Label` controls captions. The current implementation requires explicit IDs; automatic IDE insertion is future work. Requiredness is explicit in the attribute, not inferred from C# nullability. Supported scalar types: string, int/long, decimal, bool and DateOnly; nullable variants are allowed. String fields may declare select options or a link target. Only decorated public instance properties participate.

To change an existing model: edit its attributes/properties, retain IDs, increase its Document version, and run these commands with the same model-source and database configuration:

```sh
dotnet run --project src/Loom.Demo -- --plan-model
dotnet run --project src/Loom.Demo -- --apply-model=PLAN_ID_FROM_PREVIEW
```

The CLI regenerates the plan and refuses a mismatched ID before applying. Use it with the application stopped. These are local demo administration commands, not a production deployment coordinator. New databases initialize normally on first launch; planning requires an initialized SQLite database. `--export-model` prints the chosen source catalog as a JSON array for inspection. CSharp mode does not regenerate its handwritten classes.

## Saved report that survives renaming

`Reports/EquipmentRegister.json` persists columns and sort by field identity. Read `/api/saved-reports/EquipmentRegister` with the usual demo headers. Rename `tag` in the JSON-mode designer and request the same report again: its values and ordering remain, while readable names/captions update. The response has `columns` and ordered array `rows`. An explicit report heading stays fixed. One equality filter and one sort key are supported, with a 200-row limit and document read authorization.

This is a saved-report engine and API example. The visual report builder is deferred by project decision. Grouping, joins, aggregates, PDF layouts and stable external SQL views are still open. Direct SQL column renames remain a breaking change for external SQL consumers. The offline browser preview remains the v0.2 JavaScript demonstration; it does not execute C# attributes or this saved-report engine.

## Typed C# persistence (v0.4)

```csharp
var equipment = service.Repository<AssetModel>(actor);
var scope = equipment.Create(new AssetModel {
    Name = "Oscilloscope", Tag = "LAB-101", Category = "Electronics",
    Location = "Lab A", Active = true
});

scope.Value.Location = "Lab B";
scope = equipment.Save(scope);
var active = equipment.Where(x => x.Active, (bool?)true,
                             sortBy: x => x.Name);
```

Run a complete example with `dotnet run --project src/Loom.Demo -- --typed-demo`. This command uses disposable Memory storage regardless of the configured application database. It creates linked equipment/work-order records, computes a $300 estimate, changes hours to produce $360, filters equipment, and submits the order. Source: `TypedExample.cs`.

`TypedDocument<T>` carries identity, version, lifecycle state, timestamps and `Value`. `Save` requires that version and returns the new one. The repository also supports Get/Delete/Submit/Cancel/History and paged List. All operations use the existing document service, including authorization, validation, link guards, audit and conflict detection. Calculated/read-only properties are omitted from writes and returned with server-calculated values; caller edits to those properties have no effect.

Both handwritten attribute classes and regenerated `Loom.Generated` records work. Property selectors such as `x => x.Tag` resolve through permanent IDs, so a refreshed catalog can map a familiar C# property to a renamed column. A repository binds to its service/catalog; construct a new service/repository after applying schema changes. CSharp host mode still requires its accepted schema to match the compiled source on startup.

This is a synchronous typed facade, not a LINQ-to-SQL provider. Queries currently offer one equality filter, one sort, search and paging through bounded provider scans. Selectors must be direct decorated properties. Models must cover all fields; an unknown/new field or incompatible scalar type causes binding refusal. This prevents full-record saves from silently clearing fields absent from stale classes. Use `long`/`long?` for Integer repository properties (the storage range is 64-bit); optional value-type properties must be nullable. Public getters/setters (including generated init setters) and a parameterless constructor are required. Relationships remain typed model properties containing document ID strings; automatic navigation and multi-record units of work remain future work.

The current priority is the programming framework. A visual report builder and print-layout designer are deferred; existing list/report endpoints remain available.

## Report writer (v0.5)

Run the real application and click **Download report**. Open the downloaded HTML in a browser; use **Print → Save as PDF** for a PDF copy. The equipment register groups by category. The work-order register groups by priority and totals estimates. Report downloads include all records, independently of the list search, with a 200-record limit. Linked records show readable titles, subject to permission on the linked model.

`examples/Equipment_Report.html` is an actual standalone output from the tested demo. It requires no server or JavaScript to view or print. `ReportWriter.Write` accepts a saved report definition and a C# `ReportLayout` containing title/subtitle, an optional group field and numeric total fields. These fields reference permanent identities; ordinary field renames keep their bindings. See `docs/REPORT_WRITER.md`.

This implements programmer-defined report writing. A drag-and-drop designer remains deferred. PDF output currently uses the browser's print facility, not a server PDF service. The older standalone application preview remains at v0.2 and does not include this download feature.

## Visual report designer (v0.6)

Run `dotnet run --project src/Loom.Demo`, choose Admin and click **Design report**. Drag fields from the palette into Columns (or click them), drag rows to reorder, use sliders to set relative column widths, and choose alignment. Arrow buttons also reorder columns for touch/keyboard use. Edit column headings, report title/subtitle/footer, font family/size, grouping, totals, sort and portrait/landscape orientation. The preview uses the real C# writer and current database records. Click **Save layout**, then **Download report** to use it.

The first release saves one layout per model in SQLite. Saved layouts survive restarts, retain stable field IDs, and reject stale concurrent saves. Only Admin can edit/save; record readers can download the resulting report under their own permissions. It works with both JSON-owned and C#-owned models. Memory does not expose this designer because it has no layout store. Existing generated/default reports are available until a layout is saved.

This is a visual table-report editor. It does not yet provide a free-position canvas, arbitrary text/image blocks, repeating page header/footer bands, page numbering, nested groups or server PDF generation. The footer is a report-end footer. Portrait/landscape affects printing; the live preview is a continuous document rather than a paginated paper simulation. The old offline application HTML remains v0.2; run the C# app for this feature.


## Positioned text and printed page bands (v0.7)

In **Design report**, open **Position text blocks**. Choose the report-start or report-end band, add a text block and drag it into place. Numeric controls set its position, size and font. Save layout to persist changes. These two bands appear once per report; separate Page header and Page footer fields repeat on printed pages. Enable Page numbers for “Page N of M”. Turn off the browser's own headers/footers when printing to avoid duplicate annotations.

Text bands are 120 pixels high, with up to 12 static text blocks each. Horizontal positions/widths are percentages; vertical positions/heights are pixels. Blocks cannot extend outside the band. Overlap is allowed, and overflowing text is clipped to its box: size boxes to fit. Images, data-bound free-position objects, nested groups and multiple named layouts remain future work. The live preview remains continuous; repeated page bands are visible when printing in a compatible browser.

`examples/Equipment_Multipage.html` and `examples/Equipment_Multipage.pdf` show 98 sample records over eight pages. PDF output was tested in Chromium 133, including repeated table headings, page headers/footers and page counts. Other browsers' margin-box support is unverified. PDF creation still uses browser printing, not a server PDF service. Run the C# app to use this designer; the standalone application preview remains v0.2.


## Data-bound layout blocks (v0.8)

Open **Design report → Position text blocks**, add/select a block, and choose its **Content**: Static text, Record count or Field total. For Field total choose a selected numeric column. **Text / prefix** is prepended to the computed value, for example `Budget: $` produces `Budget: $425.00`. These blocks share the existing drag and numeric positioning controls and persist with the layout.

Counts and sums cover the whole filtered report, not a page or group. Null values are excluded from sums; an empty sum is zero. Totals use decimal arithmetic and refuse overflow. Numeric totals currently use invariant formatting with at least two decimal places; currency symbols are literal prefixes, with no currency conversion. Bindings reference permanent field IDs. Removing a bound column prevents preview/save until the column is restored or the block binding is changed; the designer does not silently discard the binding.

`examples/WorkOrder_Bound_Report.html` is output from the live tested C# host. Its two work orders produce a positioned `$425.00` budget total and record count of 2. Existing v0.7 multipage samples remain included. This adds report-level aggregates, not individual-record fields, repeating detail cards or image blocks. Those features, multiple layouts and server PDF generation remain pending.


## Required-field backfills (v0.9)

In **Edit model**, click **Add field**, enter its name/caption/type, check **Required**, and enter the initial value for existing records. Preview displays the value, affected record counts and SQL. Apply fills existing records and installs the required constraint in one SQLite transaction. For example, add required `site` with backfill `Main laboratory`: existing equipment gets that value, while future equipment must explicitly supply its own site. A backfill is not a default for future records.

The browser supports Text, Integer, Decimal, Date (`yyyy-MM-dd`) and Boolean (`true`/`false`). The provider also supports valid Select values when supplied by a source model. Unique, computed and Link additions remain blocked. This release does not convert an existing optional field to required, perform type conversions, copy another field's values or execute custom transformation code.

For C#-owned models, add the decorated required property with a permanent field ID, increase the model version, then create a JSON backfill file:

```json
[{"fieldId":"0123456789abcdef0123456789abcdef","value":"Main laboratory"}]
```

Replace the example ID with that property's actual permanent ID. From the project root, with the host stopped and the source model updated:

```sh
dotnet run --project src/Loom.Demo -- --plan-model --backfills=backfills.json
dotnet run --project src/Loom.Demo -- --apply-model=REVIEWED_PLAN_ID --backfills=backfills.json
```

The same commands work for JSON-owned source models. CLI apply regenerates the plan and requires the reviewed ID. Changing the backfill value changes that ID. The provider API is `store.Plan(targetCatalog, backfills)` followed by `store.Apply(plan.Id, admin)`; HTTP clients only apply a server-owned plan ID.

SQLite rebuilds each affected table without introducing permanent SQL defaults, retaining IDs, versions, timestamps, lifecycle state, existing values and framework unique indexes. Existing audit snapshots remain unchanged; the migration ledger records the schema transition and normalized backfill values. Invalid scalar values are rejected before writes. Apply rechecks the schema and includes records created after preview; row counts in the preview are informational, not a frozen record set.

Rebuilds currently require framework-managed tables: custom indexes/triggers, any database views or physical foreign keys, custom defaults/generated columns and detected custom constraints cause refusal. This is a conservative limitation to avoid silently losing external database behavior. The migration holds a transaction while copying rows; batched/online migrations and progress/cancellation remain future work. Existing reports retain their field IDs.


## Existing optional fields can become required (v0.10)

In **Edit model**, eligible optional scalar fields now have a **Required** checkbox and backfill input. Preview counts SQL nulls. If none are present, a backfill is unnecessary; otherwise supply a value. Apply rechecks data inside the migration transaction. It fills only nulls, preserving existing values including false and zero, and installs NOT NULL without a future-record default. An existing field can be renamed in the same operation using its permanent ID.

The provider and CLI use the same `MigrationBackfill(fieldId, value)` format as required additions. If a no-backfill plan encounters newly introduced nulls at apply time, it refuses. Invalid non-null values are not silently replaced. The existing input API normalizes blank optional values to null; malformed values written externally must be corrected before tightening the field. Unique, computed and Link fields, required-to-optional changes and type conversions remain unsupported.

The complete example program lives in `examples/Loom.GettingStarted`. [Getting started](docs/GETTING_STARTED.md) explains its two schema versions, how to write typed records, model ownership, CLI migrations and the UI. Validation for this release: 237 core checks, 16 HTTP checks, two optional-to-required browser workflows, the saved-report API check and an executed console example.


## Safe numeric type conversions (v0.11)

The model designer and provider now support Integer → Decimal and exact Decimal → Integer. Preview checks populated values and reports conversion steps; apply rechecks the current data in its transaction. Fractional, malformed and out-of-range values block narrowing. No rounding or truncation is performed. Nulls remain null unless a simultaneous required-field change provides a backfill. Renaming and conversion can share one migration while retaining field identity.

Eligible existing numeric fields have a type selector in Edit model. Browser changes to fields used by a C# hook are refused until the hook is updated. Source-model authors change the field/property type, retain the ID, increment the model version and use the existing plan/apply CLI. No additional conversion file is needed. Unique/computed fields and all other type pairs remain blocked. Recreate typed services/repositories from the accepted catalog and updated classes after migration.

Exact conversion uses provider-registered SQLite functions and never passes values through floating-point REAL/double. The SQL preview is diagnostic and includes these functions; apply through Loom, not by copying that SQL into an unrelated SQLite connection. Text and date parsing, precision/scale policies, intentional lossy transforms and conversions involving relationships remain future work. Browser display/editing still uses JavaScript numbers and is not suitable for editing integers beyond its exact range; the C# API and migration engine preserve 64-bit integers.

The getting-started program now runs three schema versions: optional Site, required Site, then Integer-to-Decimal Measurement. It demonstrates refusal to narrow 42.5 and verifies that the value remains unchanged. See `docs/GETTING_STARTED.md`.

## v0.12: money, conversions, binary data and in-memory demo

Run the complete disposable demo with no database setup:

```sh
dotnet run --project src/Loom.Demo -- --memory-demo
```

Alternatively run `run-memory-demo.cmd` on Windows or `./run-memory-demo.sh` on Linux/macOS. Open **http://127.0.0.1:5080**, then choose **Test results**. Two seeded runs demonstrate a large sample count, exact USD costs, downloadable binary waveforms and external time-series references. Edit records, upload a replacement waveform, download it and inspect audit history. Restarting discards all changes and restores the examples. The model/report layout editors require SQLite; lists, forms, permissions, history, CSV and printable reports work in memory. The memory provider stores logical values; persisted blob compression is exercised by SQLite.

New field types: `Money` (`decimal` plus fixed currency), `Binary` (`byte[]`), and `Uri` (`Uri` or decorated string). SQLite transparently compresses binary fields using a versioned Brotli/raw envelope; callers always receive the original bytes. The API and browser transport document numeric values as decimal strings to preserve all digits. C# repositories retain `long`/`decimal` properties.

See [data types and storage](docs/DATA_TYPES.md) for exact conversion, rounding, size and compression contracts. The model designer's **Type settings** accepts these policies as JSON; normal record entry uses money inputs, file pickers and reference fields. The demo's attributed model is `src/Loom.Demo/Models/TestResultModel.cs`.

## Scientific foundation and task tracker

Remaining work is tracked in [GitHub issues](https://github.com/jeffadickson/Loom/issues), with a requirements-to-issue map in [BACKLOG.md](docs/BACKLOG.md).

The first scientific C# layer adds immutable quantities with a bounded unit registry, dimension checks, absolute-temperature versus interval semantics, standard uncertainty and explicit coverage factors. Run it with:

```sh
dotnet run --project examples/Loom.Science
```

See [SCIENTIFIC_DATA.md](docs/SCIENTIFIC_DATA.md). This is an in-process API; native quantity fields, persistence and generated UI integration remain issue #2.

## Server demo hosts

The demo host accepts `Database__Provider=PostgreSql` or `SqlServer` plus `Database__ConnectionString`. Use `--science-demo` to include scientific test results on a persistent provider; `--memory-demo` always selects disposable memory storage. Adding the scientific model to an existing server catalog is rejected until explicit schema migrations are implemented. Use a fresh database for this walkthrough. Provider changes do not move data.

Build the standalone introduction from its served source with `python3 tools/build_introduction.py`. The served page uses external scripts to respect the host content-security policy; the standalone bundle embeds them and explicitly simulates storage.

## Laboratory register

The scientific demo now links instruments, specimens, runs and results, with a printable run report. Open `/laboratory.html` on the running demo. See [Laboratory setup, accounts, migrations and limitations](docs/LABORATORY.md).

## Account lifecycle

Use `Authentication__Mode=Identity` for persistent accounts, registration and email verification, password change/reset, authenticator MFA and administrator-managed roles/disablement. See [Identity setup and security boundaries](docs/IDENTITY.md). The older `Accounts` mode remains a limited compatibility demo.
