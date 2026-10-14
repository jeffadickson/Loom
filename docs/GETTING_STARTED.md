# Getting started with Loom

Loom is a C#/.NET framework prototype for applications built around records, forms and reports. C# attributes or JSON metadata describe a model. The framework uses that description for validation, SQLite schema, browser forms, permissions and reports. It is inspired by Frappe; it does not run Frappe/Python applications.

This guide provides two runnable paths: a small C# console program to understand the framework, then the equipment/work-order browser application to explore its UI. Neither requires writing JavaScript.

## 1. Prepare the project

Install the .NET 8 SDK, extract the source archive and open a terminal in `LoomDemo`, the directory containing `Directory.Build.props` and `src`. Internet access is needed for the first NuGet restore. These commands work in a normal terminal or PowerShell. The archive contains source; builds create their own binaries.

```sh
dotnet --version
dotnet run --project examples/Loom.GettingStarted
```

The first command should report an SDK capable of building .NET 8. The second builds and runs the complete example in `examples/Loom.GettingStarted/Program.cs`. It creates a temporary SQLite database and removes it on exit. It does not modify the browser application's data.

## 2. Follow the example program

The program creates two instruments:

| Instrument | Site before migration | Site after migration |
|---|---|---|
| Multimeter | Missing | Main laboratory |
| Oscilloscope | Annex | Annex |

It then makes Site required, backfills only the missing value and demonstrates that a new record without Site is rejected. It creates a valid third instrument and prints:

```text
After: Multimeter = Main laboratory, Oscilloscope = Annex
Expected validation: Site is required.
PASS: 3 instruments; existing values preserved; missing site filled; future site required.
```

The example checks these results and throws if they differ. Its three model classes represent three releases of the same model, not three database tables. In an ordinary application you edit the existing class and retain its permanent IDs.

### Define a model in C#

This excerpt is from the runnable program:

```csharp
[Document(ModelIds.Instrument, nameof(Name), Name = "Instrument", Version = 1)]
[Grant("Admin", "read", "create", "write", "audit")]
public sealed class InstrumentV1
{
    [Field(ModelIds.Name, Required = true)]
    public string? Name { get; set; }

    [Field(ModelIds.Site)]
    public string? Site { get; set; }
}
```

`Document` supplies the permanent model ID, storage name and schema version. `Field` supplies a permanent field ID and constraints. The CLR property type determines the scalar type; metadata can also describe select and link fields. `Grant` defines permitted actions for a role.

The sample uses fixed illustrative IDs. For a new model or field, generate a unique 32-digit ID with `Guid.NewGuid().ToString("N")` once and retain it. Renaming a property must not generate a new field ID. Readable names and captions are still available to forms and reports; permanent IDs keep persisted references connected across renames.

### Create records using typed C# objects

```csharp
var catalog = AttributeCatalog.From(typeof(InstrumentV1));
store.Migrate(catalog); // Initializes this example's fresh database.
var service = new DocumentService(catalog, store, []);
var instruments = service.Repository<InstrumentV1>(actor);
var meter = instruments.Create(new InstrumentV1 { Name = "Multimeter" });
```

The example supplies a `SqliteStore` and an explicit demo `Actor`. The repository uses the same validation, authorization, version checks and audit path as the HTTP API. It returns a `TypedDocument<T>` carrying the record ID/version and a typed `Value`. Change `record.Value`, then call `repository.Save(record)` and retain the returned version. The repository is not a LINQ-to-SQL implementation.

### Change the schema without writing a migration program

Version 2 retains all IDs, increments the document version and adds `Required = true` to Site. The sample plans and applies that transition:

```csharp
var target = AttributeCatalog.From(typeof(InstrumentV2));
var plan = store.Plan(target, [
    new MigrationBackfill(ModelIds.Site, JsonValue.Create("Main laboratory"))
]);
foreach (var change in plan.Changes)
    Console.WriteLine(change.Description);
var accepted = store.Apply(plan.Id, actor);
```

The demonstration applies its known plan automatically in its disposable database. In your application, expose the plan for review before calling Apply. Apply accepts the plan ID, not client SQL. Changing a supplied backfill changes the plan ID.

Create a new `DocumentService` and typed repository using the accepted catalog and current model after a migration, as the example does. Do not keep writing through an old model definition. Existing non-null values are preserved; SQL nulls alone receive the backfill. False and zero are populated values. The normal record API converts blank optional input to null. Invalid non-null data introduced outside that API blocks this migration and must be corrected explicitly.

If Site is populated on every record, omit the backfill. Apply checks again and refuses if a null appeared after preview. If a backfill is supplied, it also covers nulls introduced before apply. Counts shown in the preview describe that moment; the migration runs on the rows present at apply time. The backfill is not a default for future records.

### Convert numeric types safely

The program also changes Measurement from `long?` (Integer) to `decimal?` (Decimal), keeping the field ID and increasing the model version. Plan and apply use the same API; no custom conversion script is needed. It verifies the existing measurement remains 42, then saves 42.5. A proposed change back to Integer is refused because 42.5 is not whole; the original value remains stored.

| Change | Allowed when |
|---|---|
| Integer → Decimal | Every populated value is a valid signed 64-bit integer |
| Decimal → Integer | Every populated value is whole and fits signed 64-bit range |

Neither conversion rounds or truncates. Nulls remain null unless a simultaneous required-field change supplies an explicit backfill. Preview and apply both check the data. A failed preflight performs no migration; a failure during apply rolls back conversion, schema and ledger together. These conversions currently exclude unique and computed fields. Additional conversions are available in v0.12; see DATA_TYPES.md.

In the browser designer, eligible numeric fields have an Integer/Decimal selector. C# model authors change `long?` to `decimal?` (or the reverse), retain the permanent ID, increment the document version, rebuild and use the source-model planning CLI. Refresh typed services/repositories after applying, as with other schema changes.

Conversion arithmetic and storage avoid SQLite REAL/double, preserving exact 64-bit values. The current browser application still uses JavaScript numbers for record display/editing; use the typed C# API for values requiring more than JavaScript's exact integer range. This limitation does not change the provider's migration arithmetic.

## 3. Run the browser application

From `LoomDemo`:

```sh
dotnet run --project src/Loom.Demo
```

Open `http://127.0.0.1:5080`. The first run seeds three equipment records and one work order. SQLite settings are in `src/Loom.Demo/appsettings.json`; the default database is under the demo's `data` directory. Stop the host with Ctrl+C. Restarting it retains the database.

1. Open Equipment and create or edit a record. Try leaving a required field blank.
2. Open Work orders. Enter estimated hours and hourly rate; the C# hook computes the estimate.
3. Select Admin and open Edit model. Add an optional Text field named `site`, preview and apply. Give one equipment record a site.
4. Reopen Edit model, check Required beside the existing site field and preview. The error identifies missing values. Supply `Main laboratory` as the backfill, preview again and apply. Only the missing sites change.
5. Open Design report to choose columns, grouping and totals. Add text or summary blocks, save the layout, then Download report. Open the HTML and use browser Print to create a PDF.

The role selector simulates actors; it is not sign-in. This host binds to loopback and remains a local development demo. The separate `Loom_Browser_Demo.html` is an older offline preview and does not run the current C# migration engine. Use the real host for this guide.

## 4. Choose how to author models

| Mode | Where you edit | How changes reach an existing database |
|---|---|---|
| JSON (demo default) | Metadata files or browser Edit model | Browser preview/apply, or source-model CLI |
| CSharp | Decorated model classes | Rebuild, then source-model CLI preview/apply |

Set `Models:Source` to `CSharp` to use the demo's handwritten classes. Browser model mutation is disabled in that mode because the compiled classes own the definition. Forms and reports still work. A mismatch between compiled and accepted schemas refuses startup until migrated.

For source-model changes, stop the host, update the model/version, and use:

```sh
dotnet run --project src/Loom.Demo -- --plan-model --backfills=backfills.json
dotnet run --project src/Loom.Demo -- --apply-model=REVIEWED_PLAN_ID --backfills=backfills.json
```

See the README's backfill example for the JSON file format. Omit `--backfills` when the change needs none. Browser-created JSON model changes should be exported back into source; do not discard their permanent IDs. These commands target the configured database and are different from the disposable console example.

## 5. Where to look next

| File or directory | Purpose |
|---|---|
| `examples/Loom.GettingStarted/Program.cs` | Complete small program described here |
| `src/Loom.Demo/Models/EquipmentModels.cs` | Decorated equipment and work-order models |
| `src/Loom.Demo/WorkOrderHook.cs` | Calculated estimate behavior |
| `src/Loom.Core` | Metadata, services, typed repositories and reports |
| `src/Loom.Sqlite` | Persistent storage and transactional migration planning |
| `docs/API.md` | HTTP API and model-designer payloads |
| `docs/MIGRATION_REQUIREMENTS.md` | Migration requirements and implementation limits |
| `docs/REPORT_WRITER.md` | Report layout and output |
| `docs/BACKLOG.md` | Remaining framework work |

Run the framework checks with `dotnet run --project tests/Loom.Tests -- .` from `LoomDemo`. Browser automation is optional and documented in `docs/VALIDATION.md`.

## Current limits

SQLite is the implemented persistent provider; Memory is for disposable use. Changing the provider configuration does not transfer data. Required transitions currently support nonunique, noncomputed scalar fields. Conversions other than Integer/Decimal, required-to-optional changes, field retirement, link/unique constraint changes, child tables and cross-record units of work remain future work. Rebuilds conservatively refuse unsupported custom database objects. Authentication, record/field permissions and durable jobs are also unfinished. See the requirements for scope rather than treating the demo as production-ready.

## In-memory test-results demo

Run `dotnet run --project src/Loom.Demo -- --memory-demo` from the repository root, then open http://127.0.0.1:5080 and choose **Test results**. The app demonstrates money, a large integer, binary upload/download and a time-series reference. Restart to reset. See [data types](DATA_TYPES.md) for the attributed model and policies.
