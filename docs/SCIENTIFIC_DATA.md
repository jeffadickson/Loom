# Scientific quantities: first implementation

Issue #1 implements an in-process C# foundation in `Loom.Core.Science`. Issue #2 tracks native field/catalog, persistence, migrations, forms and reports. No new database field kind is claimed by this first slice.

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

Unsupported operations fail with `LoomException(422, ...)`; null programmer arguments use `ArgumentNullException`. These plain C# objects are not yet a supported persistence/JSON interchange contract. Do not assume serializing a Unit object establishes a stable schema.

## Validation and execution

`ScienceTests` exercises scale/offset conversions, dimension mismatch, absolute versus interval temperature, unknown units, uncertainty conversion, unknown versus zero uncertainty, invalid factors, nonfinite values, overflow/underflow and compatible-unit round trips. It runs with the existing framework/provider suite:

```sh
dotnet run --project tests/Loom.Tests -- .
dotnet run --project examples/Loom.Science
```

Next: native model fields and persistence (#2), typed array/chunk storage (#3), provenance (#4), timing/quality masks (#5), external adapters (#6). Production security and operational gates remain independently tracked; adding scientific primitives does not make the prototype production-ready.

Reference for uncertainty terminology: [NIST TN 1297, reporting uncertainty](https://www.nist.gov/pml/nist-technical-note-1297/nist-tn-1297-7-reporting-uncertainty). The API deliberately keeps standard uncertainty and an explicitly chosen coverage factor separate.
