# Proposed proving application: Test Facility Operations

Build a substantial application on Loom for managing a shared engineering test facility.
This is a proposal, not an implemented application or a claim of flight qualification.

## One complete operating cycle

A project requests a thermal/vibration/electrical test. A facility manager assigns a lab,
instrument set and operators. The application checks calibration validity, specimen identity
and procedure revision. Operators record the run and attach raw results or external dataset
references. A different reviewer approves the result, records discrepancies and requests
amendments when needed. The project receives a versioned report with provenance and costs.

Two independent laboratories act as tenants. A person may be an operator in one and a reviewer
in another. Neither laboratory can see the other's specimens, costs, documents or reports.

## Main data model

- Project and test campaign: owner, authorization, budget and requested procedure.
- Instrument and calibration revision: serial number, location, validity and certificates.
- Specimen and configuration revision: immutable identity, assembly state and genealogy.
- Procedure revision and test request: requirements, parameters and acceptance limits.
- Reservation and run: assigned equipment/operators, timestamps and execution state.
- Result and dataset: exact values, units, uncertainty, quality flags, raw blobs and external references.
- Discrepancy, review and amendment: disposition, independent sign-off and retained prior versions.
- Cost entry and release package: exact money, report layout and released evidence references.

Authors implement scientific validation and business rules in ordinary C#. Metadata supplies
forms, relationships and reports; generated plumbing must not prevent handwritten logic.

## Boundaries with the other projects

Loom owns application data, permissions, forms, reports, recovery and durable business work.
TestForge can execute procedures and upload run evidence. The instrument-control library can
operate/mock instruments. This application should integrate with those systems through explicit
versioned contracts, not absorb their execution engines or instrument drivers.

## Proposed milestones and acceptance gates

1. **Facility register:** two tenants, instruments/calibrations, specimens and campaigns;
   authenticated membership, useful forms/search and field-level cost restrictions.
2. **Request to run:** reservations, procedure revisions, calibration checks and controlled
   state transitions; conflicting assignments and unauthorized transitions are rejected.
3. **Evidence and review:** immutable raw inputs, derived-result provenance, independent
   approval, amendments and a multipage release report.
4. **Reliable operation:** durable export/report jobs, failure/retry inspection, backup/restore
   drills and interruption/restart scenarios. TestForge integration can begin here.

Use synthetic engineering data for the initial exercise: two laboratories, three roles per lab,
50 instruments, 200 specimens, 1,000 historical runs and several large waveform attachments.
Scale testing is a measured acceptance exercise, not an existing performance claim.

The release gate is one complete request-to-approved-report cycle, with cross-tenant leakage
tests, a deliberately interrupted job, and recovery of an independently restored tenant that
reproduces the same records, binary hashes, audit and report. This will expose framework gaps
more effectively than adding another isolated CRUD demonstration.

## Expected framework gaps to address through the application

Scheduling/conflict rules, general durable jobs, richer approval workflows, immutable provenance
graphs, attachment streaming, report pagination and production observability remain work.
Track those as explicit Loom tasks; keep application-specific procedures and acceptance limits
inside the application repository. Choose the application repository/name only when this proposal
is accepted.
