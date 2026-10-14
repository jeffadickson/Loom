# Validation record — v0.6

Updated 2026-10-14 in a Linux x64 environment with .NET SDK 8.0.426 / runtime 8.0.32 and Chromium 133 via Playwright. Generated types and the application compile with warnings treated as errors.

## Results

- **170 core/provider checks passed.** The same document-service behavior runs against Memory and a real SQLite database. Coverage includes required/select values, permission checks, linked records, unique fields, computed values, read-only tampering, stale saves, lifecycle states, audit, parameter binding, rollback, concurrent writers, SQLite reopen and migration refusal. Returned audit snapshots cannot mutate stored history.
- **16 live HTTP checks passed** against a launched ASP.NET Core application and temporary SQLite database. Coverage includes actual HTTP status codes, denied requests, concurrent updates, CSV formula escaping, pagination bounds, deletion and retained audit history.
- **11 browser workflow checks passed against the C# host.** Create equipment; create linked work order; compute estimate; edit with audit; submit/lock; Viewer UI and direct API rejection; report columns; CSV download; model inspector; mobile page overflow; no JavaScript runtime errors. Several actions are grouped into one check.
- **The same 11 browser checks passed against the standalone offline preview.** This confirms UI interactions, not C# or database equivalence. The preview uses JavaScript numeric calculations and browser storage, with explicit labeling.
- **7 designer browser checks passed on the real C# host and separately on the offline preview.** Rename and optional addition preview/apply, preserved values, date persistence, ledger, export and role visibility were exercised.
- **3 restart/export checks passed.** A full host restart retained the accepted model and populated date, linked work orders and migration ledger; exported JSON retained permanent IDs.
- The additional 28 core checks exercise stable identity, nonmutating plans, stale plans, forbidden edits, known-hook dependencies, drift detection, legacy adoption, idempotency and atomic rollback after an injected post-DDL ledger failure.
- Desktop (1440 px) and mobile (390 px) screenshots were inspected. On narrow screens the table scrolls within its panel; the page itself does not overflow.

## Reproduction

From the source root:

```sh
dotnet build src/Loom.Demo
dotnet run --project tests/Loom.Tests -- .
python3 tools/test_live.py
```

For browser tests, install Playwright in an environment where Node can resolve it, install its Chromium browser, then:

```sh
python3 tools/test_live.py dotnet --browser --designer
node tools/test_ui.cjs file:///ABSOLUTE/PATH/Loom_Browser_Demo.html
```

`CHROMIUM_EXECUTABLE` optionally chooses an installed browser. `tools/test_live.py` creates an isolated temporary database and terminates its host afterward. It expects a Debug build at the standard output path and a free loopback port 5080.

The execution environment's normal `dotnet build` launcher encountered a process-information lookup limitation. Build verification used the same SDK's `MSBuild.dll` entry point directly with restore; tests and the app ran as normal compiled .NET assemblies. This is an environment workaround, not a source change. User-facing instructions use the ordinary SDK commands.

Browser test instrumentation bypassed CSP to permit Playwright's evaluation helpers in this environment; production CSP effectiveness is not claimed as tested. The actual application retains its CSP. Browser installation used a packaged Chromium executable after the default Playwright browser-download route failed.

## What has not been demonstrated

No Windows/macOS execution, server-database adapter, multi-process load benchmark, real identity provider, tenant boundary, field/row permissions, cross-provider migration, backup restore, advanced report builder, PDF generation, durable workflow execution, accessibility certification or security review. No production-readiness or Frappe compatibility claim follows from these tests.

Raw text logs are in `docs/test-results/`. Tests are behavioral source tests, not a substitute for those outstanding production gates.

## v0.3 validation

Ten new core checks cover attribute/JSON equivalence, permanent identity through C# property rename, unsupported CLR types, saved report values/filter/sort after rename, updated/fixed headings, permissions, missing references and database reopen. Live HTTP checks pass in both JSON and CSharp modes. The 11 existing browser workflows and 7 designer workflows pass on the JSON C# host. The saved report endpoint preserves values after a designer rename and full host restart. CSharp mode hides the designer and rejects its endpoint. Local CLI preview, mismatched-plan refusal and reviewed application were exercised against the populated database. No offline preview code changed in v0.3.

## v0.4 validation

46 new checks cover typed create/get/save/delete, required values, stale saves, permissions, filtering/sort/paging, invalid selectors, linked deletion, decimal/date values, server-calculated read-only properties, submit/cancel/edit locks, audit, generated init-property materialization, field rename and stale field coverage. Common checks run on both Memory and real SQLite; migration binding checks run on SQLite. The generated model sources were regenerated and compiled with warnings as errors. The standalone typed example runs and prints estimates of 300 and 360 before submission. The existing 16 HTTP and 11 browser workflows pass against the real host; the saved report API also passes. No report-builder or offline-preview work was added.

## v0.5 report writer validation

11 additional core checks pass (160 total). The existing 16 HTTP and 11 browser workflows pass. Three writer browser/API checks pass after enabling the same CSP bypass used for existing Playwright instrumentation: HTML download/open, print media styling, and authorized/denied requests. Screen and print-media screenshots of the sample report were visually inspected. This verifies Chromium rendering/styles, not native PDF generation or cross-browser multi-page pagination. A real sample HTML report is included under examples/.

## v0.6 visual-layout checks

Ten new core checks pass: initial/default state, save authorization/version, stale overwrite, invalid font/width/empty columns, stable identity through field rename, typography/footer/orientation rendering, and SQLite reopen. The visual browser workflow covers palette drag-and-drop, arrow reorder, width adjustment, heading/alignment/font/page edits, server preview, save/reload, saved download and Viewer restriction with no runtime errors. The test uses the real CSP with no bypass and verifies the preview's computed font. A full host restart retains the saved definition and report output. Existing 16 HTTP checks and 11 browser workflows also pass. A timing issue in the reload test was fixed by waiting for the asynchronously loaded dialog before asserting its contents. Desktop screenshots were inspected; a low-contrast sidebar was corrected. Actual multipage print-band behavior remains outside this release.


## v0.7 validation — 2026-10-14

Build succeeded and 176 core checks passed. Six new checks cover positioned text escaping, CSS page-text escaping/counters, out-of-bounds boxes, duplicate block IDs and oversized page captions. The live host passed 16 HTTP checks and the saved-report API check. Three new browser workflows verify pointer placement and saving, generating a 98-record PDF from actual server HTML, and reloading saved page/text settings under the real CSP. Page text extraction verifies all eight headers, footers and page numbers, each of 95 generated instruments exactly once, and report-start/end notes exactly once. All eight rendered pages were visually inspected.

Reproduce: run `tools/test_live.py /path/to/dotnet --bands` with Playwright available through NODE_PATH and optional CHROMIUM_EXECUTABLE. Then run `python3 tools/check_print.py /tmp/loom-multipage-report.pdf` (requires PyMuPDF). Browser print evidence is Chromium 133 only; no cross-browser pagination claim is made.


## v0.8 validation — 2026-10-14

Application and tests built successfully. 186 core checks passed (10 new checks for bound aggregates, filtering, empty sums, escaped prefixes, stable field identity and invalid sources/bindings). The SQLite layout reopen check also verifies persistence of a count binding. The live host passed 16 HTTP checks and the saved-report API check. Three browser workflows under the real CSP verify sum/count selection and C# preview, saved binding reload plus missing-column refusal/recovery, and saved output recalculation after adding data; no browser runtime errors were observed.

Run `tools/test_live.py /path/to/dotnet --bindings` with Playwright available through NODE_PATH and optional CHROMIUM_EXECUTABLE. Build the application before running the live harness. Core tests and browser output are recorded in `docs/test-results/v08-*.txt`. The eight-page PDF fixture documents v0.7 print verification; no new PDF was generated in v0.8.


## v0.9 validation — 2026-10-14

Application and tests built successfully; 218 core checks passed. The 32 new backfill checks cover required values, duplicate/unknown or existing field bindings, invalid scalar inputs, preview immutability, value-sensitive and order-independent plan IDs, non-admin refusal, rollback after injected ledger failure, exact scalar preservation (including Int64 maximum and false), SQL-looking literal text, caller mutation isolation, combined rename/addition, records inserted after preview, retained audit history, receipts, idempotence, missing future values, framework unique indexes, custom index/trigger and external foreign-key refusal, successive rebuilds, absence of a permanent SQL default and database reopen.

The live host passed 16 HTTP checks and the saved-report API check. Two browser workflows verify required-field controls, missing-value refusal, read-only preview, apply to three populated records, schema reload, future-record validation and ledger values. A CLI workflow exports the accepted model, plans a second required addition, rejects a changed backfill under the reviewed ID, applies the correct value in a new process, and verifies all four records plus both migration receipts.

Reproduce with `tools/test_live.py /path/to/dotnet --backfill` after building the demo (Playwright via NODE_PATH; optional CHROMIUM_EXECUTABLE). Logs: `docs/test-results/v09-core.txt` and `v09-backfills.txt`. These tests cover constant required-field additions; type conversions and optional-to-required migrations remain unsupported.


## v0.10 validation — 2026-10-14

Application, test project and standalone getting-started program built successfully. 237 core checks passed. Nineteen added checks cover missing-value requirements, invalid backfills, preview counts/read-only behavior, invalid data introduced after preview, no-write failure behavior, simultaneous rename/required change, preservation of populated values and document versions, unchanged historical snapshots, required future writes, false/zero preservation, Boolean/Integer null fills, database reopen, complete data without backfill, late null refusal and successful retry after correction.

The live host passed 16 HTTP checks and the saved-report API check. Two browser workflows created an optional field, populated one of three records, previewed a required transition with two nulls, filled only those nulls, and verified schema/history after reload with no runtime errors. Run `tools/test_live.py /path/to/dotnet --required` after building the demo, with Playwright available through NODE_PATH and optional CHROMIUM_EXECUTABLE.

The complete `examples/Loom.GettingStarted` program was executed against its temporary SQLite database. It verified Multimeter's missing site became Main laboratory, Oscilloscope retained Annex, a future missing site was rejected, and the final total was three valid instruments. Logs are in `docs/test-results/v010-*.txt`. No type-conversion or production migration claim is made.


## v0.11 validation — 2026-10-14

Application, core tests and the updated getting-started program built successfully. 263 core checks passed. Twenty-five new conversion checks cover preview counts and nonmutation, signed 64-bit endpoints, values above double's exact integer range, null preservation, receipts, fractional/overflow/malformed/exponent refusal, tiny fractions that decimal parsing could round, changed data at apply, rollback after an injected ledger failure, exact reverse conversion, record version/history retention, combined conversion/rename/required backfill, idempotence, unsupported pairs and reopen. An additional designer check rejects type changes to C# hook dependencies.

The live host passed 16 HTTP checks and the saved-report API check. Three browser workflows verify widening, refusal to narrow 42.5 without modifying it, and exact narrowing combined with rename and required backfill, followed by schema reload. Browser automation explicitly waits for each newly opened designer dialog before manipulating its controls. No browser runtime errors were observed. Run `tools/test_live.py /path/to/dotnet --conversion` after building the app, with Playwright available through NODE_PATH and optional CHROMIUM_EXECUTABLE.

The standalone getting-started program executed all three model versions, verified 42 widened exactly, stored 42.5, refused narrowing and checked that 42.5 remained unchanged. Logs: `docs/test-results/v011-core.txt`, `v011-conversions.txt`, and `v011-example.txt`. These checks establish the implemented Integer/Decimal conversion behavior, not general type conversion or production readiness.

## v0.12 validation

- Core/provider suite: 324 checks passed, including exact Money/decimal parsing and sums, all new conversion paths, SQLite compressed BLOB storage and audit recovery, binary byte limits, checksum/truncation/trailing-data rejection, empty payloads, fixed currency and required-to-optional migrations.
- Live SQLite host: 16 HTTP checks, saved-report resolution, Money/Binary/Uri designer additions, exact transport and transparent binary download, plus three numeric-conversion browser scenarios.
- In-memory demo: real browser money/large-integer editing, binary upload/download, scale rejection; process restart verifies seed reset.
- Existing getting-started C# sample builds/runs and verifies migrations and refusal behavior.

Commands: `dotnet run --project tests/Loom.Tests -- .`; `python3 tools/test_live.py dotnet --conversion --datatypes`; `python3 tools/test_memory_demo.py dotnet`. Browser checks require Playwright and Chromium; set NODE_PATH and CHROMIUM_EXECUTABLE if using external installations.

Raw results are in docs/test-results/v012-*.txt. These are prototype functional checks, not a production security, load or database-provider certification.
