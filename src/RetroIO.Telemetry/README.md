# RetroIO.Telemetry

## What it is

One shape for the metrics and the log of every RetroServer part (a protocol server, a service):
`RetroTelemetry`.

- **Metrics** are named counters with key-value labels, like a Windows perfmon counter list:
  `retroio.dialup.calls.answered`, `retroio.dialup.io{direction=in}`. Each is an observable
  instrument on a .NET `Meter`, the type the OpenTelemetry .NET SDK reads.
- **The log** sits next to the metrics: each record has a time, a level, the part, the text and
  key-value attributes. It goes to the part's `ILogger` (where OpenTelemetry takes logs from) and the
  newest records are kept for the management API.

## Why you need it

So every part of RetroServer reports in the same way, the management API (`api/v1/telemetry`) and
RetroAdmin show them all with one page, and exporting metrics and logs with OpenTelemetry later is a
line in the host (`AddMeter("RetroIO.*")` and the OpenTelemetry logger provider), with nothing
changed in the parts. Rule from Ronny, 2026-11-23.

## How to use it

```csharp
RetroTelemetry telemetry = new RetroTelemetry("RetroIO.DialUp", logger);
telemetry.AddCounter("retroio.dialup.calls.answered", "{call}", "Calls answered since start.", () => CallsAnswered);
telemetry.AddCounter("retroio.dialup.io", "By", "Bytes on the lines.", () => BytesIn, new("direction", "in"));
telemetry.Log(LogLevel.Warning, "login refused", new("caller", "127.0.0.1:30715"));
```

Register the part's `ITelemetrySource` with the host so the management API lists it.

A part that already logs through `ILogger` in many classes does not need each call changed: add
`new TelemetryLoggerProvider("<the part's namespace>", telemetry)` as an `ILoggerProvider`, and every
line from a class in that namespace is kept in the part's log too (the host's level filter applies).

Naming: `retroio.<part>.<thing>` in lower case, OpenTelemetry units (`By` bytes, `s` seconds,
`{call}` a count), and variants as labels rather than as separate names.

The package depends only on `Microsoft.Extensions.Logging.Abstractions`, so any protocol package can
use it.
