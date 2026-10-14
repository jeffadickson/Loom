# Active backlog and GitHub task index

GitHub issues are the current task tracker. This index replaces stale release-by-release checkboxes; historical requirements remain the acceptance baseline, with later implemented features taking precedence. P0 denotes a production gate, not a claim that this prototype is production-ready.

Implemented baseline: scalar models, typed repositories, local role checks, Memory/SQLite, audit, exact money, bounded compressed binary fields, URI references, supported migrations, report writer/designer and demos. The basic GitHub build workflow is present; broader CI remains open.

| Issue | Requirements | Work |
|---|---|---|
| [#1](https://github.com/jeffadickson/Loom/issues/1) | SCI-01 | Scientific quantity and uncertainty primitives — completed |
| [#2](https://github.com/jeffadickson/Loom/issues/2) | SCI-02 | Integrate scientific quantities into model fields and persistence |
| [#3](https://github.com/jeffadickson/Loom/issues/3) | SCI-03 | Scientific typed arrays and chunked binary storage |
| [#4](https://github.com/jeffadickson/Loom/issues/4) | SCI-04 | Scientific run provenance and immutable raw results |
| [#5](https://github.com/jeffadickson/Loom/issues/5) | SCI-05 | Scientific timing and sample quality masks — completed |
| [#6](https://github.com/jeffadickson/Loom/issues/6) | SCI-06 | External dataset references and scientific interchange adapters |
| [#7](https://github.com/jeffadickson/Loom/issues/7) | META-07/08/11 | Build-time model generation and diagnostics |
| [#8](https://github.com/jeffadickson/Loom/issues/8) | META-06/09 | Complete metadata constraints and model designer |
| [#9](https://github.com/jeffadickson/Loom/issues/9) | DB-14/META-08 | Provider-side query AST, indexes and link pagination |
| [#10](https://github.com/jeffadickson/Loom/issues/10) | DB-05/15/MIG-18 | PostgreSQL provider and live conformance CI |
| [#11](https://github.com/jeffadickson/Loom/issues/11) | DB-06 | SQL Server provider and conformance |
| [#12](https://github.com/jeffadickson/Loom/issues/12) | DB-07 | MariaDB/MySQL provider and version matrix |
| [#13](https://github.com/jeffadickson/Loom/issues/13) | MIG-02/05 | Legacy identity mapping and DocType renames |
| [#14](https://github.com/jeffadickson/Loom/issues/14) | MIG-07/08/09/10/17/20 | Remaining backfills, transformations and constraint migrations |
| [#15](https://github.com/jeffadickson/Loom/issues/15) | MIG-11/20 | Safe field retirement and separately reviewed purge |
| [#16](https://github.com/jeffadickson/Loom/issues/16) | DB-11/MIG-12-15/19/OPS-05 | Production migration CLI, coordination and recovery |
| [#17](https://github.com/jeffadickson/Loom/issues/17) | MIG-16/18 | Coordinate schema changes with generated code and external contracts |
| [#18](https://github.com/jeffadickson/Loom/issues/18) | SEC-02/07 | Real authentication and secure deployment |
| [#19](https://github.com/jeffadickson/Loom/issues/19) | SEC-03/04 | Row/field authorization and tenant isolation |
| [#20](https://github.com/jeffadickson/Loom/issues/20) | SEC-06 | Audit retention, redaction and tamper evidence |
| [#21](https://github.com/jeffadickson/Loom/issues/21) | DB-16 | Backup, restore and database transfer |
| [#22](https://github.com/jeffadickson/Loom/issues/22) | META-04/DOC-05 | Child tables and full transaction-aware hook lifecycle |
| [#23](https://github.com/jeffadickson/Loom/issues/23) | DOC-07 | Declarative approval workflows and amendments |
| [#24](https://github.com/jeffadickson/Loom/issues/24) | DOC-08/09 | Durable jobs, scheduler and transactional outbox |
| [#25](https://github.com/jeffadickson/Loom/issues/25) | UI-06 | Saved views, bulk actions, comments and managed attachments |
| [#26](https://github.com/jeffadickson/Loom/issues/26) | UI-05 | Accessibility and responsive UI acceptance |
| [#27](https://github.com/jeffadickson/Loom/issues/27) | RPT-02/03/04 | Advanced reports, provider queries and server PDF |
| [#28](https://github.com/jeffadickson/Loom/issues/28) | RPT-05 | Charts, dashboards, XLSX and scheduled report delivery |
| [#29](https://github.com/jeffadickson/Loom/issues/29) | API-02/03 | Versioned API contracts, SDKs, idempotency and bulk import |
| [#30](https://github.com/jeffadickson/Loom/issues/30) | OPS-03/04 | OpenTelemetry, structured errors and operational diagnostics |
| [#31](https://github.com/jeffadickson/Loom/issues/31) | OPS-02/05/06 | Expand CI, packaging, dependency review and performance gates |
| [#32](https://github.com/jeffadickson/Loom/issues/32) | META-05/10 | Application packaging and singleton/tree/virtual models |
| [#33](https://github.com/jeffadickson/Loom/issues/33) | GOV-01 | Resolve license and remaining architecture decisions |

The #2–#11 batch has implementation and verification progress recorded in [NEXT_TEN_PROGRESS.md](NEXT_TEN_PROGRESS.md). #5 timing/quality masks meets its listed acceptance criteria; all other issues in this batch retain explicit remaining work. The new server providers compile and the initial PostgreSQL 16 / SQL Server 2022 live CI suite passed; broader conformance and migrations remain open.
