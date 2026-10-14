# Scientific data contracts

`Loom.Core.Science` now includes quantities, native measurement fields, compressed array foundations, provenance references, timing/quality masks and an authorized dataset adapter boundary. See [the ten-issue progress report](NEXT_TEN_PROGRESS.md) for completed work and remaining integration gaps.

```csharp
using Loom.Core.Science;

var original = new Measurement(new Quantity(25, Units.Celsius), 0.1);
var kelvin = original.To(Units.Kelvin);       // 298.15 K, standard u = 0.1 K
var fahrenheit = original.To(Units.Fahrenheit); // 77 degF, standard u = 0.18 degF interval
var expanded = original.ExpandedUncertainty(2); // 0.2 degC interval, not a probability
```

Quantities and measurements are immutable. Conversion returns a new value and retains the original. Unit conversion requires identical quantity kinds. Absolute temperature and temperature interval are separate kinds: a 10 degC temperature difference becomes 18 delta_degF, with no origin offset. Absolute temperature is constrained to nonnegative thermodynamic kelvin in this bounded model. Time here means duration, not a UTC/TAI timestamp.

## Built-in registry

| Quantity kind | Case-sensitive symbols |
|---|---|
| Length | m, mm, cm, km |
| Duration | s, ms, us |
| Voltage | V, mV |
| Absolute temperature | K, degC, degF |
| Temperature interval | delta_K, delta_degC, delta_degF |

`Units.Get(symbol)` rejects unknown symbols. Definitions cannot be modified by callers. There is no unit-expression parser, user-defined registry, dimensional multiplication/division or implicit numeric conversion in this slice.

## Numeric and uncertainty contract

Values use finite IEEE binary64 (`double`) for scientific measurements, not financial decimal. Ordinary floating-point rounding is expected; round trips are compared with tolerances. Conversions reject nonfinite results and scaling that underflows a nonzero value to zero. These checks do not promise exact decimal representation, arbitrary precision or preservation of changes below machine resolution. Money retains the existing separate decimal contract.

`Measurement.StandardUncertainty` is a nullable nonnegative finite magnitude expressed in the interval corresponding to the value's unit. Null means unspecified; zero means an explicitly supplied zero. A Celsius-to-kelvin conversion adds the temperature offset to the value only, never to uncertainty. Fahrenheit conversion scales uncertainty by the magnitude of the scale ratio. No uncertainty is inferred from a sample array.

`ExpandedUncertainty(k)` requires specified uncertainty and a finite positive coverage factor. It returns a width in the current unit interval. It does not return a confidence probability; a chosen factor such as 2 does not by itself establish coverage. Covariance, correlated inputs, distribution models and uncertainty propagation through arbitrary algorithms remain outside this API.

Unsupported operations fail with `LoomException(422, ...)`; null programmer arguments use `ArgumentNullException`. `Measurement` has a supported JSON converter; standalone `Unit` serialization is not an interchange contract.

## Validation and execution

`ScienceTests` exercises scale/offset conversions, dimension mismatch, absolute versus interval temperature, unknown units, uncertainty conversion, unknown versus zero uncertainty, invalid factors, nonfinite values, overflow/underflow and compatible-unit round trips. It runs with the existing framework/provider suite:

```sh
dotnet run --project tests/Loom.Tests -- .
dotnet run --project examples/Loom.Science
```

Further work: provenance integration with measurement records (#2/#4), durable array/chunk storage (#3), external adapters (#6). Production security and operational gates remain independently tracked; adding scientific primitives does not make the prototype production-ready.

Reference for uncertainty terminology: [NIST TN 1297, reporting uncertainty](https://www.nist.gov/pml/nist-technical-note-1297/nist-tn-1297-7-reporting-uncertainty). The API deliberately keeps standard uncertainty and an explicitly chosen coverage factor separate.

## Native measurement fields

Decorate a `Measurement?` property with `QuantityKind="Temperature"` and `AllowedUnits=new[]{"degC","K"}`. The field type is inferred. Standard uncertainty always means a nonnegative standard uncertainty in the corresponding unit interval; null is unknown. API values use:

```json
{"value":"25","unit":"degC","standardUncertainty":"0.1"}
```

Numbers are invariant round-trip binary64 strings. Memory and SQLite preserve the original unit and uncertainty, including audit snapshots. SQLite stores this compound value as JSON text. Unit-list changes preflight existing values; removing a populated unit is refused. No automatic canonical-unit rewrite occurs. Generated forms edit value, unit and uncertainty independently; reports display all three. Quantity aggregation and ordering require explicit scientific semantics and are not offered by the new scalar query API.

## Arrays, clocks and references

`ScientificArray` owns compressed, checksummed chunks and copies input metadata/data. `ReadElements` addresses flattened elements and decompresses only overlapping chunks. Dtype, byte order, shape, axes, unit and affine scaling are explicit. Float data defaults to finite-only; allow NaN/infinity explicitly when needed. This is currently an in-memory representation; a durable chunk-store format and persisted quality-mask binding remain open.

`SampleTiming` uses decimal seconds relative to `SampleClock.Epoch`, with an explicit UTC, TAI, GPS or monotonic scale. It never converts between scales or interprets a leap second. Resolution is declared metadata, not implicit quantization. Coordinates must increase strictly; uniform and irregular grids retain gaps. Per-sample flags distinguish missing, saturated, unsynchronized and suspect samples. Selection returns original indexes; clock offsets return a new timing object. Unknown synchronization uncertainty remains null.

`DocumentRevision.Capture` performs an authorized read and hashes the serialized revision. This detects a changed snapshot but is neither a signature nor a replacement for retained revision history. `RunProvenance.Validate` validates references, operator/software and algorithm parameters. Persisting and verifying the complete graph atomically is still open. Immutable DocTypes reject service updates/deletes; direct database administration remains outside this service-level guarantee.

`DatasetGateway` validates versioned opaque references, authorizes before invoking an adapter, and checks returned byte/sample budgets. Adapters must enforce budgets while reading and honor cancellation. Credentials belong to adapter configuration. No HDF5/NeXus or time-series adapter is included yet.
