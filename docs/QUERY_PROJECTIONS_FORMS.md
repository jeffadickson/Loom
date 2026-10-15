# Projected queries and form descriptions

## Read only the fields needed

`DocumentQuery.SelectFieldIds` contains stable field IDs. Null preserves the existing full-readable-
document behavior; an empty array returns document identity, version, state and timestamps with
an empty values object. Unknown or duplicate IDs fail validation. An explicitly requested
restricted field returns 403. Row scopes, filter/sort permissions and exact value handling still apply.

```csharp
var schema = catalog.Get("Reading");
var name = schema.Fields.Single(f => f.Name == "Name").Id!;
var page = service.Query("Reading", actor,
    new DocumentQuery(name, Skip: 25, Take: 25, SelectFieldIds: [name]));
// page.Total describes all matching records, not just this page.
// page.Items contain Name; omitted fields are absent rather than fake nulls.
```

Use a full Get before editing a document. Projected values are a read model and must not be
submitted as a complete replacement document: omitted optional values could otherwise be cleared.

SQLite generates an explicit SELECT column list and only decodes selected values. Memory selects
fields before copying results. PostgreSQL and SQL Server remove excluded JSON properties on the
server before EF materializes a document body. Those server stores still retain complete JSON bodies
on disk, so projection reduces returned data/materialization, not necessarily physical disk reads.
Server reference-search fallback retains scalar fields needed for comparisons but excludes
unrequested binary/measurement payloads. Its existing 10,000-row scan limit remains.

Saved reports request their selected columns; a name-only report no longer reads evidence blobs.
A report that explicitly selects a binary column still loads it to calculate its displayed length.
Uniqueness and incoming-link existence checks request identity only.

## Describe a form once

`service.Describe(type, actor)` and `tenantSession.Describe(type)` return a `DocumentForm`:

- Stable type and field IDs, current names and labels.
- Visible field definitions: types, required flags, defaults, bounds, precision, scale, currency,
  link targets, options and descriptions.
- CanCreate and CanEdit flags. Immutable types never advertise editing.
- ReadOnly field flags incorporating field write policy. Other roles' grants are not exposed.

Metadata informs the UI; it never replaces server-side validation or authorization. Custom C#
application actions still govern reservations, execution, review and release.

The reusable `wwwroot/loom-forms.js` renderer consumes this contract. Facility register forms no
longer repeat field definitions in JavaScript. Exact numeric input stays text until server
normalization. Link fields use debounced server searches, so a referenced record need not be on
the first page. Required boolean fields permit false. File inputs enforce the schema's byte limit
before encoding; complex measurements currently use JSON text, not a specialized scientific editor.

## Facility paging

The initial overview returns at most 25 records per type and separate complete totals/status counts.
Request, register, release and job views have Previous/Next controls. The overview and browse
queries exclude all binary fields, including compressed release HTML. Request detail loads the
selected request's attempt/review summaries and related register records. Evidence/report downloads
are explicit.

`GET /api/facility/{tenant}/records/{type}?skip=25&take=25` returns a projected Page.
Optional `status` filters requests across the full dataset. Optional `search` uses existing query
permissions; searches touching hidden fields fail closed. The visible toolbar deliberately says
**Search this page**; link lookup searches the target register on the server.
`GET /api/facility/{tenant}/requests/{id}` returns role-filtered detail and references.

Reservation conflicts now use database-side resource/status/time filters with identity-only
results, rather than loading every request. They remain in the same transaction as insertion.

## Verification and boundaries

Shared contracts run against Memory, SQLite, PostgreSQL and SQL Server: selected-field paging,
filtering by omitted fields, exact money, search fallback, empty projections, explicit binary
selection, and invalid IDs. A SQLite test replaces a stored evidence envelope with invalid bytes:
projected summary and scalar report succeed while a full read fails, demonstrating that the blob
is not decoded then discarded. Further tests cover permission-filtered form metadata, immutable
edit flags, global counts with bounded pages and non-overlapping pages.

Browser verification creates 32 instruments, navigates to the second page, finds the last instrument
through a remote link lookup, then completes the real execution/review/restart/release cycle.

This does not complete the proposed large-dataset benchmark, streaming storage, generalized
workflow service or distributed job scheduler. Attempt/audit detail can still grow with a single
request's history. Query indexing and server search translation remain separate scalability work.
