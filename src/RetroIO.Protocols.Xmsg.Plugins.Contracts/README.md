# RetroIO.Protocols.Xmsg.Plugins.Contracts

## What it is

The gRPC contract between an XMSG node and a plug-in server program: `src/xmsg_plugin.proto`, and the C# code
generated from it. Both sides use it. The `.proto` file is also shipped in the package under `proto/`.

## Why you need it

A plug-in can be written in any language gRPC supports. The `.proto` file is the one definition they all share.
C++ has an official gRPC library. Plain C has none, so a C plug-in uses a C++ shim or a C library such as the
community ones. That choice is the plug-in author's.

## The contract in short

One call, `Connect`, with a stream each way. The plug-in sends requests, each with an id it chooses. The node
answers each with a `Result` carrying the same id, and sends an `Arrived` for every message that lands on one of the
plug-in's ports.

| Request | XMSG or XROUT call it stands for | Result value |
|---|---|---|
| `Hello` | (first request, required; carries the token when the node has one) | the node's system number |
| `OpenPort` | XFOPN | the new port's magic number |
| `ClosePort` | XFCLS | |
| `RegisterName` | XSNAM, or XSCRS for a connection port | |
| `AdjustSeats` | XSNSP | |
| `ClearName` | clearing a name | |
| `Send` | XFSND | |
| `SendLetter` | XSLET | |

Statuses are the COSMOS Programmer Guide's own numbers:

- an XMSG status (1 = completed, negative = an XMSG error) for port and send calls;
- an XROUT status (0 = XRSOK) for name calls.

A letter arrives with its XSLET header first, as XROUT hands it to a SINTRAN task.

## Related

- `RetroIO.Protocols.Xmsg.Plugins`: the node's side.
- `RetroIO.Protocols.Xmsg.Plugins.Client`: the C# plug-in side.
- The design: `docs/XMSG-PLUGIN-SERVERS.md` in the RetroServer repository.
