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
