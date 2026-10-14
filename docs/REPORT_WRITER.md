# Report writer

Implemented in v0.5. Define a report in C#, render safe standalone HTML, and print from a browser. The existing SavedReport definition selects fields, a sort and an optional equality filter. ReportLayout adds a title, subtitle, one grouping field and a set of sum fields. Grouping and totals must use selected columns; numeric totals accept Decimal and Integer only. Explicit report headings override catalog labels.

```csharp
var model = catalog.Get("WorkOrder");
var definition = new SavedReport(
    "WorkOrderRegister", model.Id!,
    model.Fields.Select(f => new ReportColumn(f.Id!)).ToArray());
var layout = new ReportLayout(
    "Work orders",
    "Maintenance register",
    GroupFieldId: model.Fields.Single(f => f.Name == "priority").Id,
    SumFieldIds: [model.Fields.Single(f => f.Name == "estimate").Id!]);
string html = ReportWriter.Write(definition, layout, catalog, service, actor);
File.WriteAllText("WorkOrders.html", html);
```

Resolve names when authoring a definition, then persist its IDs to retain bindings through renames. Handwritten name lookups in code still need updating if those names change. The output contains no scripts, remote fonts or external resources. It includes HTML-escaped headings and values, print styling, table headers marked for browser repetition, group counts/totals and a grand total. Dates use stored ISO dates; decimals use invariant decimal formatting; currency symbols/units come from headings. Null values display an em dash and are excluded from sums. Groups follow their first occurrence in the sorted input. The PDF page size is requested as landscape A4; browser settings can override it.

The HTTP example is `GET /api/reports/{type}/print` with the usual demo headers. It returns an HTML attachment. The app button downloads it; it does not invoke the print dialog automatically. Downloaded output is a snapshot and may contain any data the actor is permitted to read.

Authorization comes from the document service. Linked title lookups also enforce the target model's read permission; denied links display [Restricted], missing records display [Unavailable]. Repeated link titles are cached within a single rendering. No arbitrary SQL or HTML template execution is accepted. Report rows are capped at 200; larger reports fail explicitly instead of truncating. Group totals use decimal arithmetic with overflow refusal.

Limits: one grouping level, one equality filter and one sort; no cross-model aggregate queries, charts, page numbering, nested bands, interactive layout designer or server PDF generation. Record retrieval and linked-title lookups are separate service transactions, so a report is not guaranteed a single database snapshot under external concurrent writers. Print styles were inspected in Chromium; comprehensive multi-page pagination across browsers remains unverified.

Verification: 11 writer core checks cover empty data, escaping, groups/totals, selected-field validation, numeric-only totals, permissions, label identity resolution and readable/permission-protected links. Three browser/API writer checks cover standalone download/open, print media styling and authorized/denied downloads. Existing HTTP and browser workflows were also run.

## Visual editing in v0.6

The application now exposes a visual column-layout editor with real server rendering. `ReportColumn` adds Width (relative integer weight 1–100) and Alignment (auto/left/center/right). `ReportLayout` adds FontSize (9–20 pixels), Font (sans/serif/mono), Footer and Orientation (portrait/landscape). Fonts and CSS values are allowlisted; text is escaped. Column weights normalize to 100% of table width. Admin edits one layout per model; `_report_designs` stores a versioned JSON definition with actor. SQLite transactions enforce compare-and-save version checks. Permanent field IDs retain bindings across field renames. There is no layout history viewer or multiple-named-layout selector yet.

The iframe preview is sandboxed without script permissions. The host now permits inline styles for validated report typography, while its script policy remains self-only. CSP is not bypassed in the visual-layout browser test. This is a table-layout editor, not arbitrary placement of page objects. Report-end footer text is supported; repeating page header/footer bands and advanced pagination remain unimplemented.


## v0.7 implementation update

The visual designer now supports movable static text blocks in report-start and report-end bands, plus repeating printed page header/footer text and optional Page N of M numbering. Saved layouts retain these settings. Band constraints: 120px height, 12 blocks maximum, 500 characters per block, font 9–40px; page header/footer text is limited to 120 characters. Coordinates are validated server-side, HTML text is escaped and CSS generated-content strings use hexadecimal escaping. Images, arbitrary data-bound objects, nested groups, multiple named layouts and server PDF generation remain open. See README for operation and browser-print limits. This supersedes earlier deferrals of these implemented features.


## v0.8 implementation update

Positioned report-start/end blocks support static text, filtered report record count and selected numeric-field sum. `ReportTextBlock` adds `Source` (`text`, `count`, `sum`; default `text` for compatibility) and optional `FieldId` (sum only). Text is the literal prefix for bound values. Numeric sums resolve permanent IDs, exclude nulls and refuse decimal overflow. Invalid/missing/nonselected/nonnumeric bindings and unknown sources are rejected. Saved blocks are evaluated using the same authorized rows as the report table; no expression, SQL or template execution is accepted. Counts/sums are report-wide, not group/page-specific. Individual-record fields, detail-band layouts, images and configurable numeric formats remain open.

## v0.12 implementation update

Money, explicit decimal precision/rounding, text/date/select conversions, required-to-optional migration, Binary with transparent SQLite compression, and external Uri references are implemented. The API transports numeric field values as strings. The new `--memory-demo` mode runs seeded test results without database setup. See [DATA_TYPES.md](DATA_TYPES.md) for authoritative contracts and remaining limits; this supersedes earlier deferrals of these features. Streaming/object storage, time-series querying/adapters, FX, additional durable providers and production authentication remain open.
