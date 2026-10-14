# Demo HTTP API

Base: `http://127.0.0.1:5080`. Every `/api` call requires `X-Loom-Demo: local` and `X-Demo-Role: Admin|Editor|Viewer`. Those are demonstration selectors, NOT credentials. JSON uses camelCase.

| Method | Route | Meaning |
|---|---|---|
| GET | /health | Basic health and provider name |
| GET | /api/meta | Models visible to the current role; granted actions and provider capabilities |
| GET | /api/documents/{type} | List with search, sort, descending, skip, take, field and equals parameters |
| GET | /api/documents/{type}/{id} | Read document |
| POST | /api/documents/{type} | Create; body is field-value object |
| PUT | /api/documents/{type}/{id} | Full replacement of editable values; body `{ "version": 1, "values": {...} }` |
| DELETE | /api/documents/{type}/{id}?version=1 | Delete a draft, subject to references and permission |
| POST | /api/documents/{type}/{id}/actions/submit | Submit draft; body `{ "version": 1 }` |
| POST | /api/documents/{type}/{id}/actions/cancel | Cancel submitted document; version required |
| GET | /api/documents/{type}/{id}/audit | Before/after history, including deleted documents, with audit permission |
| GET | /api/reports/{type} | Bounded generated report; optional field/equals filter |
| GET | /api/reports/{type}/csv | Bounded CSV export; optional field/equals, search, sort and descending |

List response: `{total, items}`. A document has `id`, `type`, `version`, `state`, `createdAt`, `modifiedAt`, and `values`. PUT is not PATCH: omitted optional fields become null; omitted required fields are errors. Clients must omit server-calculated fields.

Errors: 400 malformed/unsupported fields or query; 403 denied; 404 missing; 409 concurrency/uniqueness/lifecycle conflict; 422 invalid values. JSON body is `{error: message}`. Structured field-specific error codes and OpenAPI are planned.

Paging is 1–200 rows. CSV refuses more than 200 matching rows. Prototype searches are case-insensitive substring searches; equality filters are exact serialized values; strings sort with the host's comparison rules. Provider-side query semantics and stable cross-provider collation are future requirements.

## SQLite model designer (Admin)

| Method | Route | Meaning |
|---|---|---|
| POST | /api/designer/plan | Body `{sourceHash, type, fields:[{id,name,label}], additions:[{id,name,label,type}]}`; all existing fields must be included. Returns generated plan, SQL and row counts. |
| POST | /api/designer/apply | Body `{planId}`; applies the server-owned plan transactionally and refreshes the running catalog. |
| GET | /api/designer/history | Successful migrations with before/after catalog snapshots. |
| GET | /api/designer/export | ZIP containing accepted model JSON and source integration instructions. |

`/api/meta` includes `schemaHash`, permanent IDs and the role/provider-specific `designer` flag. Browser writes send `X-Loom-Schema`; a supplied outdated hash produces 409. This header is currently optional for other API clients. Unsupported edits return 422; expired/stale previews return 409. The designer is unavailable on Memory. New field IDs should be generated once per draft and retained between previews; the server can generate an ID when omitted.

## v0.3 implementation update

- `AttributeCatalog` reads Document/Field/Grant attributes into the canonical catalog. Explicit permanent IDs survive property renames. `Models:Source=CSharp` selects the supplied equivalent C# example; browser model mutation is blocked in this mode. Source/database mismatch refuses startup.
- `--plan-model`, `--apply-model=<reviewed-id>` and `--export-model` expose local source-model planning/application and inspection. Production deployment/recovery coordination remains open.
- `SavedReports.Run` resolves persisted type/field identities for columns, equality filter and sort. `/api/saved-reports/EquipmentRegister` runs the source-controlled example, enforcing read permission and a 200-row cap. Results return readable column names/captions and row arrays.
- External SQL compatibility views, arbitrary code dependency analysis, full report designer, aggregates/joins and backfills remain open. See README for exact authoring and CLI examples.

## v0.5 report download

`GET /api/reports/{type}/print` returns a self-contained HTML attachment using the caller's read permissions. It includes every declared column, groups by the first Select field when present, and sums read-only Decimal fields. The demo action includes all records independently of list search and refuses results over 200 rows. No arbitrary template or SQL input is accepted.

## Visual report layout API (v0.6)

- `GET /api/report-designs/{type}`: Admin; returns saved layout or generated default (version 0).
- `POST /api/report-designs/{type}/preview`: Admin; accepts ReportDesign and returns `{html}` rendered with current read permissions. Does not save.
- `PUT /api/report-designs/{type}`: Admin; accepts `{version,definition,layout}`, validates field identities and formatting, returns saved layout with incremented version. Stale version is 409; invalid formatting is 422.

`/api/meta.reportDesigner` indicates availability. `GET /api/reports/{type}/print` uses the saved layout when present, with the downloading actor's read permissions. Designer persistence requires SQLite. The role headers remain demo selectors, not production authentication.


## v0.9 required-field addition

`POST /api/designer/plan` accepts `required: true` and `backfill` on each addition. Existing additions without these keys remain optional. Example addition: `{"id":"0123456789abcdef0123456789abcdef","name":"site","label":"Site","type":"Text","required":true,"backfill":"Main laboratory"}`. The normal sourceHash, type and existing fields are still required. Backfills on optional additions are rejected. Plans and migration history include normalized `backfills` with permanent fieldId/value pairs. Apply still accepts only `{"planId":"..."}` and remains Admin-only. C# model authority still disables browser model edits.


## v0.10 existing-field required transition

Existing entries in `DesignRequest.fields` may now include `required: true` and an optional scalar `backfill`. Omitting `required` preserves its current setting for compatibility. Example: `{"id":"EXISTING_PERMANENT_ID","name":"site","label":"Site","required":true,"backfill":"Main laboratory"}`. Supply the actual field ID, retain every existing field exactly once, and include the usual sourceHash/type/additions. The plan reports `RequireField` changes with missing-value counts. When populated values already cover all rows, omit backfill. It is rejected on fields that are already required or are not becoming required. Required-to-optional changes remain blocked by the provider. The apply endpoint is unchanged.


## v0.11 numeric type changes

Existing `DesignRequest.fields` entries accept optional `type` alongside id/name/label/required/backfill. Omission preserves the existing type. Integer/Decimal changes are checked by the provider; all other conversion pairs remain unsupported. Numeric fields used by declared C# hook dependencies cannot be changed through the browser endpoint. Plans and receipts identify conversions with `ConvertField`. The apply payload is still the reviewed server-owned planId only.

## v0.12 implementation update

Money, explicit decimal precision/rounding, text/date/select conversions, required-to-optional migration, Binary with transparent SQLite compression, and external Uri references are implemented. The API transports numeric field values as strings. The new `--memory-demo` mode runs seeded test results without database setup. See [DATA_TYPES.md](DATA_TYPES.md) for authoritative contracts and remaining limits; this supersedes earlier deferrals of these features. Streaming/object storage, time-series querying/adapters, FX, additional durable providers and production authentication remain open.
