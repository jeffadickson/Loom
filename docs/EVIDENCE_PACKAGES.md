# Portable scientific evidence

Facility Operations can now hand a completed test to another person without requiring access
to the running application. Complete the existing reservation → measurement → independent review
→ release workflow. As a Manager or Reviewer, open **Reports & queue** and choose
**Download evidence package**. The existing HTML report download remains available.

The ZIP contains:

| File | Contents |
| --- | --- |
| `report.html` | Original immutable released HTML, including project costs |
| `evidence.bin` | Original raw bytes from the approved attempt, regardless of the uploaded file format |
| `provenance.json` | Version 1 Facility handoff metadata and nine record snapshots |
| `readme.txt` | Contents, verification command and sharing limitations |
| `manifest.json` | Format version, exact uncompressed file sizes and SHA-256 digests |

Provenance contains the tenant and release ID, Request, Result, Review, Release, Campaign,
Instrument, Calibration, Specimen and Procedure. Every document keeps its identity, version,
timestamps and authorized values. Decimal and money values remain exact JSON strings. Binary
fields are omitted from these snapshots because the files already carry their bytes. External
dataset URIs remain references: Loom does not fetch them, guarantee their availability or freeze
the remote dataset. Prior rejected attempts remain in Loom; the package contains only the selected
approved attempt. It is not a database backup or a full audit export.

Export opens one authorized transactional snapshot, checks the linked approval and stored payload
hashes, then builds the package. Operators cannot export because reports and provenance contain
costs. Cross-tenant access is denied. The download response uses `Cache-Control: no-store`; copies
saved by recipients still need their own access and retention policy. No new database schema or
migration is required. Existing releases can be exported.

## Offline verification

From an extracted Loom release, with the .NET runtime installed:

```sh
dotnet Loom.Demo.dll --verify-evidence Test_Evidence.zip
```

From a built checkout:

```sh
dotnet src/Loom.Demo/bin/Release/net8.0/Loom.Demo.dll --verify-evidence Test_Evidence.zip
```

Exit code **0** means the inventory, lengths and digests agree; **1** means verification failed.
Verification runs before authentication, database configuration or background workers. It neither
extracts files nor executes the HTML or fetches dataset URIs. After successful verification, open
`report.html` with a browser and inspect the raw data using the tool appropriate to its format.
The filename `evidence.bin` does not assert a particular media type.

**Integrity is not authenticity.** Someone who replaces both data and its manifest can produce
another internally consistent ZIP. Obtain the package through a trusted channel or compare its
whole-file digest supplied through an independently trusted channel. This format does not provide
a digital signature, regulatory certification or evidence of scientific correctness.

## Reusable Core API

```csharp
using Loom.Core.Science;

byte[] archive = EvidencePackage.Create(new Dictionary<string, byte[]>
{
    ["measurement.csv"] = System.Text.Encoding.UTF8.GetBytes("time,value\n0,20.125\n")
});
EvidenceManifest inventory = EvidencePackage.Verify(archive);
// EvidencePackage.VerifyFile(path) bounds the file read before verification.
```

The generic Core verifier validates the archive format and its digests. It does not interpret the
application-specific contents of `provenance.json` or establish its approval authority.

Version 1 uses exact, case-sensitive JSON properties:

```json
{"version":1,"files":[{"path":"measurement.csv","length":25,"sha256":"64 UPPERCASE HEX DIGITS"}]}
```

The example is schematic: real lengths and hashes are generated from the bytes. Unknown or duplicate
properties, null records, unsupported versions, unlisted/missing/duplicate ZIP entries and invalid
hashes are rejected. Names are flat lowercase ASCII letters/digits plus `.`, `_` and `-`; traversal,
leading/trailing dots, consecutive dots, separators and Windows device names are rejected. Names
are limited to 100 characters; `manifest.json` is reserved.

Limits: 32 payload files, 4 MiB per uncompressed payload, 16 MiB combined payload, 16 MiB input/output
ZIP, 64 KiB manifest. Verification checks actual decompressed sizes as well as ZIP metadata. This
is a bounded in-memory format, not a streaming large-dataset archive. The Facility upload limit
remains 1 MiB. Sorted entries and fixed ZIP timestamps make repeated exports reproducible under
the same runtime/compressor; cross-version compressed-byte identity is not promised.

## Qualification

`EvidencePackageTests` covers repeatability, tampering, inventory mismatch, malformed manifests,
unsafe names and resource bounds. `FacilityApplicationTests` checks authorization, restart,
provenance isolation, stored corruption and bounded worker behavior. The real browser/recovery
runner independently checks ZIP hashes with Python and invokes the actual offline command:

```sh
python tools/test_facility.py dotnet
```

The same runner is part of `build/Loom.proj` BrowserTest on Windows, macOS and Linux. Browser checks
also cover server-side search beyond page one and stale responses after an account change.
