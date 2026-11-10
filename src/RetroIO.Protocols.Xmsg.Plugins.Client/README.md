# RetroIO.Protocols.Xmsg.Plugins.Client

## What it is

What a C# plug-in server program uses to connect to an XMSG node. `PluginConnection` opens the gRPC stream and
gives two objects:

- `Kernel` (`IXmsgKernel`): the XMSG calls, with ports living on the node;
- `Names` (`IXroutNames`): the XROUT name calls, with names claimed on the node.

A server written against those two interfaces runs the same inside a node or as a plug-in.

## Why you need it

The chat server (`RetroIO.Protocols.Xmsg.Chat`) is written as a SINTRAN task: it opens a port, claims `*CHAT`,
receives and sends. With this package it runs as its own program, and RetroServer does not have to contain it.

## What you can use it for

```csharp
using GrpcChannel channel = GrpcChannel.ForAddress("http://127.0.0.1:5011");
await using PluginConnection node = await PluginConnection.ConnectAsync(channel, "retro-chat-server", "1.0");
ChatServer chat = new ChatServer(node.Kernel, node.Names);
chat.Open("*CHAT", 20, "Welcome");
while (!node.IsClosed) { chat.Poll(); Thread.Sleep(10); }
```

## Behaviour worth knowing

- **Calls block until the node answers.** XMSG calls are synchronous on a SINTRAN machine, and so are these. A call
  that needs the node waits at most `CallTimeout` (10 seconds). A timeout comes back as an error status, never as
  success.
- **Message buffers stay in the plug-in.** Reserving, writing, reading and releasing are done by a real
  `XmsgKernel` inside the plug-in. Only opening and closing ports, sending, and what arrives cross the stream.
- **Calls the contract does not carry throw `NotSupportedException`.** Examples: general status, friend systems,
  privileges, returning a message, listing names. Nothing is answered with a made-up value.
- **When the stream ends the node releases everything.** Reconnecting means a new connection and a new server
  object.

- **A token, when RetroServer has one.** Pass it to `ConnectAsync`; a wrong token is refused with XEPVR (-24).
- **Log lines go to RetroServer.** `PluginConnection.Log(...)`, or `PluginLoggerProvider` for `ILogger`. Lines below
  the level RetroServer asked for are dropped in the program. See `docfx/xmsg/logging.md`.
- **Check `NodeFeatures` before using a newer request.** See `docfx/xmsg/versioning.md`.

## Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| A node running `RetroIO.Protocols.Xmsg.Plugins` | Everything | RetroServer with its XMSG node switched on (`xmsgnode-config.json`) |

## Related

- `RetroIO.Protocols.Xmsg.Plugins`: the node's side.
- `RetroIO.Protocols.Xmsg.Plugins.Contracts`: the `.proto` file, for plug-ins in other languages.
