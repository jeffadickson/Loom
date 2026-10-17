# Scientific evidence handoff: 20 local iterations

Scope: turn the existing Facility request-to-release workflow into a portable, verifiable
handoff. All increments remain local until the batch is finished. This ledger records
actual changes and checks; final qualification is recorded below after execution.

| Iteration | Change | Check |
| --- | --- | --- |
| 1 | Define portable scientific evidence acceptance criteria | Reviewed existing workflow and issue 35; no database schema change required. |
| 2 | Add deterministic bounded evidence archive writer | Built and passed 8 focused checks, including reproducible inventory and existing Facility workflow. |
| 3 | Verify complete evidence inventory and file digests | 9 focused checks pass, including altered, missing, duplicate, extra and truncated ZIP entries. |
| 4 | Reject nonportable paths and oversized evidence inputs | 10 focused checks pass; path traversal, reserved device names, count and byte limits exercised. |
| 5 | Require strict manifest properties and version semantics | 11 focused checks pass, including malformed, null, unknown and duplicate JSON properties. |
| 6 | Add standalone offline evidence verification command | Reviewed pre-host dispatch and bounded file reads; added file-verifier regression for next cumulative run. |
| 7 | Export approved release evidence through tenant authorization | 13 focused checks pass, including offline file verification, role denial, cross-tenant denial and identical exports after restart. |
| 8 | Include bounded provenance snapshots and handoff instructions | Reviewed nine selected records, removed payload duplication, added exact-value and unrelated-record regression. |
| 9 | Expose authenticated evidence ZIP download endpoint | Reviewed route identity resolution, fixed filename, no-store response and role enforcement at application boundary. |
| 10 | Add evidence package download to released reports | JavaScript syntax checked; reused existing download handling and Manager/Reviewer report visibility. |
| 11 | Verify stored digests before release and evidence download | 15 focused checks pass; corrected summary-versus-payload read and proved corrupted stored hashes fail closed. |
| 12 | Replace whole-table evidence scans with scoped provider queries | Reviewed stable field IDs, two-row bounds, exact cardinality and preserved blob-free summaries. |
| 13 | Bound release processing and honor cancellation between jobs | 16 focused checks pass, including two pending releases drained one per batch and cancellation before writes. |
| 14 | Reject missing and oversized encoded evidence before allocation | Reviewed decode bound and rollback path; added null, empty, malformed and oversized input regression. |
| 15 | Search all facility records through paged provider queries | JavaScript syntax checked; search resets page offsets and removes misleading current-page-only filtering. |
| 16 | Discard stale facility responses across account and tenant changes | Reviewed request generations for load, paging, detail, download and delayed file submission; JavaScript syntax passes. |
| 17 | Exercise package downloads search and delayed account responses in Chromium | Real browser workflow passes, including off-page search, ZIP download, mobile layout and delayed North response after South login. |
| 18 | Independently verify exported archives and offline CLI after restart | Python ZIP/hash checks and real CLI success, corruption, missing-file and usage failures pass after persisted worker restart. |
| 19 | Document portable evidence format and update HTML introduction | Regenerated standalone introduction; documented API, commands, limits, permissions and integrity limitations. |
| 20 | Complete adversarial review and qualify the scientific handoff batch | Full locked build, 491 framework checks, 19 focused checks, real Facility recovery browser/CLI and live/offline introduction checks pass. |

## Local qualification

- Locked full solution build: passed with .NET SDK 8.0.426.
- Existing executable framework suite: **491 checks passed**.
- Evidence/Facility Fact methods: **19 passed**, executed by an isolated local reflection runner;
  the ordinary xUnit runner remains the CI gate. No local Testcontainers claim is made.
- Real Facility browser flow, queued-work restart, ZIP download, independent Python hashes,
  offline CLI success and rejection cases: passed.
- Live/offline HTML introduction and laboratory browser flows: passed.
- Final review added strict verification of malicious inventory paths and oversized manifests,
  aggregate-size enforcement before copying payloads, friendly CLI corruption errors, and
  suppression of an audit-history response after its dialog closes.

No production deployment, load qualification, signing, remote dataset capture, large-file streaming,
or expansion of the existing 1 MiB Facility evidence limit is claimed by this batch.
