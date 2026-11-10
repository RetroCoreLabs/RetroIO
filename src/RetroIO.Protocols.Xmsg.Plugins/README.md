# RetroIO.Protocols.Xmsg.Plugins

## What it is

The XMSG node's side of plug-in servers. A plug-in server is a separate program, in any language, that runs only
server code (for example chat). It dials the node over gRPC and keeps one two-way stream open. This package turns
its requests into XMSG kernel and XROUT calls on the node, and sends back what arrives on its ports.

- `PluginServerHost`: the work. It queues requests from gRPC threads and does them in `Pump()`, on the node's pump
  thread.
- `PluginConnectionService`: the gRPC service, one `Connect` stream per plug-in.
- `PluginSession`: one connected plug-in, with its ports, names and the events waiting to go back to it.

## Why you need it

A server compiled into the node takes the node down with it when it crashes or hangs, and it has to be C#. A
plug-in runs in its own process, in any language. The plug-in never sees an XMSG frame. It makes the calls a
SINTRAN task makes: open a port, claim a name, send, receive. Acknowledgements, sequence numbers, resends and
recovery stay in the node, written once.

## What you can use it for

- Running the C# chat server outside the node. See `RetroIO.Protocols.Xmsg.Plugins.Client`.
- Writing a server in C or another language from `xmsg_plugin.proto`. That file is in
  `RetroIO.Protocols.Xmsg.Plugins.Contracts`.

Wiring it into a node:

```csharp
XmsgKernelServer bridge = XmsgKernelServer.Create(systemNumber, portSeed, "*PLUGINS", 4);
PluginServerHost host = new PluginServerHost(bridge, new XroutDirectory());
node.Register(bridge);                       // the node's server list
services.AddGrpc(); services.AddSingleton(host);
app.MapGrpcService<PluginConnectionService>();
// and on the node's pump thread, every tick:
host.Pump();
```

## Behaviour worth knowing

- **One kernel for all plug-ins.** Every plug-in's ports are opened in the bridge's kernel, so two plug-ins never
  get the same port. Their names go in one XROUT name table.
- **Letters arrive whole.** A letter from a SINTRAN machine to a plug-in's name is handed over with its XSLET
  header first, exactly as XROUT gives it to a SINTRAN task. The data starts at `4 + the length word`
  (`XroutLetter`).
- **`XmsgNodeService` (namespace `Server`) runs the whole node** for a server program such as RetroServer: it
  joins an Ethernet segment, runs the TAD terminal server, opens the gRPC port, and pumps it all on one loop.
  `services.AddXmsgNode(o => ...)`. One peer machine only for now.
- **A token, when set, is always checked** (`PluginServerHost.Token`). The program sends it in its Hello; a wrong
  or missing one is refused with XEPVR. Empty means no check.
- **A plug-in that goes away loses everything.** When its stream ends, its ports are closed and its names cleared.
- **Not done yet:**
  - A letter to a name nobody holds gets no XROUT error back.
  - Plug-in names do not show in LIST-NAMES.

  Both are in `src/BUGS.md`.

## Tests

`src/RetroIO.Protocols.Xmsg.Plugins/tests`. They run the C# chat server as a plug-in over a real gRPC stream, held
in memory, and check that it answers a join that reached the node off the wire. They also check that a plug-in that
disconnects loses its name.

## Related

- `RetroIO.Protocols.Xmsg.Plugins.Contracts`: the `.proto` file.
- `RetroIO.Protocols.Xmsg.Plugins.Client`: the C# plug-in side.
- The design: `docs/XMSG-PLUGIN-SERVERS.md` in the RetroServer repository.
