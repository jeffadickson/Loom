# Data types and storage — v0.12

## Attributed C# fields

The complete runnable model is `src/Loom.Demo/Models/TestResultModel.cs`. Its key declarations are:

```csharp
[Field("cc000000000000000000000000000005",
    Currency="USD", Precision=19, Scale=2)]
public decimal? Cost { get; set; }

[Field("cc000000000000000000000000000006",
    Compression="auto", MaxBytes=1048576,
    MediaType="application/octet-stream")]
public byte[]? Waveform { get; set; }

[Field("cc000000000000000000000000000007")]
public Uri? Series { get; set; }
```

Keep permanent IDs when renaming properties. A byte array is an opaque payload: Loom does not infer sample encoding, dimensions, units, endianness or timestamps. Model those explicitly in other fields or use a defined binary format. The sample payloads are illustrative bytes, not a measurement interchange format.

## Money and exact decimals

Money uses a C# decimal and a fixed three-letter uppercase currency identifier on its field. The identifier is validated syntactically, not against a current ISO currency registry. Default precision/scale is 19/2. Specify both Precision (1–28 digits) and Scale (0–Precision) to change it. Decimal fields may also set both; unrestricted Decimal still rejects values that cannot be represented exactly in .NET decimal.

Input uses plain decimal notation: dot separator, no grouping or exponents. Parsing first examines an integer coefficient, so overlong fractions cannot silently round. `Rounding="reject"` is the default. `even` means nearest with ties to even; `away` means nearest with ties away from zero. Overflow is checked after rounding. These policies apply to ordinary saves and schema conversions. Currency-specific decimal places and accounting policies are selected by the application, not inferred.

Reports label money values and totals with the currency. Each field has its own total; USD and EUR are never combined. Summation rejects results that cannot be represented exactly. This does not provide currency-tagged arithmetic for arbitrary application C# expressions: properties are decimal, and custom business calculations must keep their currency context. There is no exchange-rate service or implicit FX conversion. Currency changes/removal on existing Money fields are refused.

## Supported conversions

| Source | Target | Rule |
| --- | --- | --- |
| Integer | Decimal | Exact widening |
| Decimal | Integer | Whole value within signed 64-bit range |
| Text | Integer / Decimal | Explicit invariant parsing; invalid values refuse migration |
| Text | Date | Explicit DateFormat: yyyy-MM-dd, MM/dd/yyyy or dd/MM/yyyy; storage becomes ISO |
| Text | Select | Every populated value must be in target Options |
| Select | Text | Preserve text |
| Text / Uri | Uri / Text | Text-to-Uri validates the reference |
| Integer / Decimal / Text | Money | Currency and target numeric policy are explicit; this assigns denomination, not exchange |
| Decimal / Money | Same type | Precision/scale and explicit rounding changes; Money currency unchanged |
| Select | Select | Options changes validate all existing selections |
| Binary | Binary | Compression/limit/media policy changes re-encode and validate stored payloads |
| Required | Optional | Remove NOT NULL, preserve values |

Conversions exclude unique and computed fields. Preview validates all populated values; apply validates again inside its transaction. Failed operations preserve the original data, accepted schema and migration ledger. Stable IDs preserve report references during renames. Existing required backfills and simultaneous renames remain supported. Other type pairs, custom mapping programs and FX conversions are not implemented.

## Binary storage and compression

`Binary` uses base64 in document JSON and byte[] in typed C#. SQLite stores an actual BLOB containing: LMB1 magic/version, raw/Brotli codec marker, decoded byte count, SHA-256 of decoded bytes, and payload. This format is internal; use Loom to read/write it.

- `auto`: try Brotli for payloads at least 1,024 bytes; keep raw if compression does not help.
- `none`: store raw bytes inside the same envelope.
- `brotli`: always use Brotli, even if it increases size.
- Reading uses the envelope's codec, independently of the current preferred write policy.
- Decoded length is checked before allocating, decompression must finish exactly, and the checksum must match. Invalid/truncated/trailing/corrupt data produces an error, not partial bytes.
- SHA-256 detects corruption; it is not encryption or authenticated protection against someone able to rewrite the database.

Default/max per-field limit is 8 MiB decoded; normal document writes also limit combined binary data to 8 MiB. Empty bytes and null are distinct. Per-field limits are configurable downward. This implementation buffers data in memory and is intended for modest test-result attachments. It is not a streaming object store or a time-series database. Large datasets should use external references. Whole SQLite audit snapshots are also transparently compressed (32 MiB decoded cap); historical uncompressed JSON audit entries remain readable. Old snapshots are not rewritten by field migrations.

Lists show byte counts, reports/CSV show binary placeholders, forms upload files, and authenticated-by-demo-role downloads return original bytes as an attachment. No binary content is executed or rendered inline. MemoryStore keeps uncompressed logical values and resets on restart.

## External references

`Uri` accepts absolute http, https, tsdb and influxdb references with a host, no user-info credentials, and at most 2,048 characters. It stores the reference only. It never connects, fetches, resolves a query or checks that a remote dataset exists. Put provider-specific dataset/channel/time-window metadata in ordinary fields as needed; keep connection credentials in application configuration. No time-series adapter is included yet.

## HTTP precision contract

Numeric document fields—including values in audit snapshots—and numeric saved-report cells are JSON strings. For example: `"SampleCount":"9007199254740993"`, `"Cost":"1234.56"`. Metadata, document versions, page counts and booleans retain ordinary JSON types. Clients should send exact numeric strings; a client that rounds a number before sending has already lost information. In-process C# values stay strongly typed. This is a deliberate v0.12 wire-contract change.

Binary values are base64 JSON strings. `GET /api/documents/{type}/{id}/binary/{field}` returns decoded bytes with the configured media type and attachment filename after the same document read-permission check. Missing data returns 404. Compression is a persistence detail; no client decompressor is needed.

## Demo

Run `dotnet run --project src/Loom.Demo -- --memory-demo`, then open http://127.0.0.1:5080. Choose Test results. The large sample count intentionally exceeds JavaScript's exact integer range. Upload/download a file and edit a cost; entering 1.005 demonstrates default scale rejection. Restart to reset. No database server or file is needed, but the .NET 8 SDK and initial NuGet restore are required. The demo remains loopback-only with simulated roles, not production authentication.
