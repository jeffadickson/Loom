# Issues #2–#11: implementation progress

This batch advances all ten issues. It does **not** complete all ten. #5 meets its listed timing/quality acceptance criteria; other issues remain open for the work below.

| Issue | Implemented and exercised | Remaining acceptance work |
|---|---|---|
| #2 | Measurement attributes/catalog, typed JSON, Memory/SQLite round trips and audit, migration unit validation, forms, reports | Link measurement provenance to the persisted run graph in #4; broaden migration fixtures |
| #3 | Typed array descriptors; shape/length/allocation validation; explicit finite-value policy; compressed checksummed chunks; bounded partial reads | Durable chunk persistence, stable interchange format, missing-value/quality bindings, corruption fixtures across persisted chunks |
| #4 | Immutable DocTypes, authorized revision capture with content hashes, provenance/algorithm parameter validation | Persist and verify provenance links atomically; retain immutable input revisions; amendment workflow and full authorization coverage |
| #5 | Explicit clocks, uncertainty, uniform/irregular coordinates, gaps, quality flags, nonmutating filters and offsets | Listed acceptance criteria complete; persistence integration belongs to array/provenance follow-up |
| #6 | Versioned opaque dataset locators, separate credential configuration, authorization-first adapter gateway, budgets/cancellation | Actual HDF5/NeXus and time-series adapters; round trips including quality/provenance |
| #7 | ModelTool CLI, MSBuild target, stable generated IDs, unchanged-output preservation, handwritten-file protection, generated-model compile fixture and CI step | Precise source diagnostics for hooks/stale bindings; removed-model cleanup and fuller incremental build tracking |
| #8 | Descriptions, text lengths, numeric bounds, create-only defaults, attribute/generator/settings support; deterministic ZIP timestamps/order | Conditional display; dedicated typed designer controls; transactional constraint-change preflight |
| #9 | Stable-ID scalar query AST, authorized API, typed equality selectors, parameterized SQLite pushdown with exact decimal ordering, Memory parity | Index planning/EXPLAIN coverage, paged link search, migrate legacy List/report paths to provider queries |
| #10 | PostgreSQL dialect in shared server provider, serializable CRUD/audit/optimistic concurrency, exact JSON numerics; live CI definition | Migrations/drift/concurrency coverage, server-side queries/indexes, dedicated blob representation and version matrix |
| #11 | SQL Server dialect using the same provider and conformance executable; live CI definition | Migration suite and full collation/concurrency coverage |

## Verification

Local verification: **488 framework checks passed**; browser measurement editing/reset checks passed; generator/model compilation passed twice; server-provider/conformance projects restored and compiled. Logs: [framework](test-results/next-ten-framework.txt), [browser](test-results/next-ten-browser.txt).

The framework test executable covers the existing suite plus measurement round trips, migration refusals, array ranges and bounds, timing masks, dataset authorization, raw-record immutability, constraints, and Memory/SQLite query parity. Browser automation edits and reloads the new measurement controls, preserves exact money/integers, uploads/downloads bytes, and verifies memory reset. The MSBuild target generates and compiles an immutable attributed scientific model.

Server-provider and conformance projects restore and compile locally. Hosted CI for implementation commit `a7c0829` also passed: [framework/build/generated models](https://github.com/jeffadickson/Loom/actions/runs/38603350931) and [live PostgreSQL 16 plus SQL Server 2022](https://github.com/jeffadickson/Loom/actions/runs/38603350918). Both database jobs executed successfully, including their test commands. The shared live suite checks exact money, transactional audit, rollback, authorization, stale writes, uniqueness, reopen durability and audit after deletion. This is initial conformance coverage; #10/#11 remain open for migrations, drift, broader concurrency and other gaps above.

## Query and constraint contracts

`DocumentService.Query` and `POST /api/documents/{type}/query` accept stable field IDs, an AND list of scalar comparisons (`Equal`, `GreaterThan`, `LessThan`, `IsNull`), ordering and bounded paging. Enum operators use their normal JSON numeric representation (0–3). Values are normalized with field rules. SQLite binds values as parameters, compares exact decimals using a registered decimal collation and uses ordinal text comparison. Nulls sort first ascending; ties use document ID. Binary/measurement comparison is refused pending explicit scientific semantics. `TypedRepository.Where` now uses this provider query path. Legacy `List` and the link picker still have their old limits.

Defaults apply only to omitted fields on creation. Explicit null is not replaced. Updates do not reapply defaults, and defaults never imply a backfill. Bounds and text length are enforced server-side; changing existing constraints is conservatively refused by SQLite until migration preflight is implemented. Descriptions are presentation metadata, not validation. Conditional display is not implemented and must never become an authorization mechanism.

## Server-provider limitations

`Loom.Server.ServerDocumentStore` accepts an owned `DbConnection` and explicit `ServerEngine`. Applications provide `NpgsqlConnection` or `Microsoft.Data.SqlClient.SqlConnection`; driver configuration and credentials remain outside the model. Both dialects store complete JSON documents and audit entries in dedicated tables, preserving exact decimal text. This initial implementation does not expose native numeric columns or compressed blob columns. It supports only initial schema creation and an unchanged schema thereafter; schema changes fail explicitly.

Transactions use serializable isolation. Known serialization/deadlock/constraint errors become retryable/conflict responses; callers must retry the complete operation. Unique field checks currently run through the existing service scan inside the transaction, with a 10,000-record scan ceiling. There is no claim of native unique-index enforcement or optimized server queries. Application comparisons currently follow the existing service's ordinal value equality; server key columns still depend on database collation and require a documented deployment policy. Raw database access bypasses service validation and immutable-record protections.

The CI database passwords are disposable test-only values. SQL Server trust-server-certificate configuration is scoped to the isolated CI database and is not a production configuration recommendation.
