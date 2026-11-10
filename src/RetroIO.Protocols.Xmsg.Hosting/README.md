# RetroIO.Protocols.Xmsg.Hosting

## What it is

Everything a program needs to **run** an XMSG node, as opposed to the protocol itself: the topology file,
a node builder, the state files, the clean-shutdown steps, the restart announcer, the folder sync daemon and
the one-shot file push and pull drivers.

## Why you need it

The protocol packages give you a node host, a TAD server and a file server. Putting them together so they
work against a real ND machine takes more than that, and every piece of it was learned the hard way:

- **The state files must survive a restart.** The ND keeps the next datagram sequence it expects from us for
  each remote system. Start again at zero and it drops our frames without an error, which looks exactly like a
  dead link. `XmsgStateFiles` names the files; the caller says which folder they live in.
- **Shutdown has to say goodbye.** Stopping with TAD sessions open killed D100's XMSG once. Closing the socket
  without a LAPB DISC left the next connection repeating SABM until someone reset the link by hand.
  `XmsgNodeShutdown` does both.
- **The two transports must be built the same way.** The runner built the node twice, once for HDLC and once
  for Ethernet, and the copies drifted: logs, the seed store and the restart announcement were each missing on
  one path. `XmsgNodeBuilder` is the single copy.

All of this lived as internal code inside `xmsgrunner` in the RetroXmsg repository until 2026-11-09. It moved
here so the runner and RetroServer share one implementation.

## What you can use it for

- Building a node on any link: `XmsgNodeBuilder.Build` with an `XmsgNodeOptions`.
- Serving a folder over COSMOS file access: `XmsgNodeBuilder.CreateFileServer`.
- Running several links on one set of servers: `XmsgNodeBuilder.BuildRelayHosts`.
- Reading and checking a topology file, or the same JSON from a setting: `TopologyConfig.Load` and `Parse`.
- Stopping cleanly: `XmsgNodeShutdown.LogOutTadSessions`, then `DisconnectLapb` on HDLC.
- Mirroring a folder onto a machine: `SyncDaemon`, gated by `XmsgSyncReadiness.Build`.
- Pushing or pulling one file: `FaPushRun`, `FaPullRun`.

## Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET SDK as pinned in `global.json` | Everything | |
| A writable state folder | Anything that runs | One per node. Two processes must never share one |
| A link | Anything live | `LapbLayerAdapter` over an HDLC bridge, or `EthernetLink` |
| A pump every 20 ms | Anything live | Call `XmsgHostedNode.Host.Pump()` |
| A system number of our own | Anything live | Only one process may use it on a network at a time |

## Threading

The node keeps its state with no lock, so only one thread may touch it. The HDLC adapter delivers payloads
on the same thread that runs its loop, so that is already true there. An Ethernet backend delivers on its own
receive thread: set `XmsgNodeOptions.DeliverInboundOnPump` and the node queues those payloads and handles them
inside `Pump()` instead.

## Contents

| Type | Purpose |
|---|---|
| `XmsgNodeBuilder`, `XmsgNodeOptions`, `XmsgHostedNode` | Build a node and keep its parts |
| `XmsgStateFiles` | The names of the state files, and where they go |
| `FaConnectionNumbers` | The file server's connection numbers, in persisted blocks |
| `XmsgNodeShutdown` | TAD logouts and the LAPB DISC on the way down |
| `TopologyConfig`, `TopologyNode`, `TopologyEndpoint`, `EthernetConfig`, `FileServerConfig`, `TadUserConfig`, `LogConfig`, `NodeReach` | The topology file |
| `LinkAnnouncer` | Tell a peer our XMSG restarted, on the loop tick and not inside a status callback |
| `SyncDaemon`, `LiveSyncTransferAgent`, `SyncCredentials`, `XmsgSyncReadiness` | Folder sync over the node's link |
| `FaPushRun`, `FaPullRun` | One file to or from a machine |

## Tests

`src/RetroIO.Protocols.Xmsg.Hosting/tests`.

## Related

- `RetroIO.Protocols.Xmsg.Node` - the node host, the routing table and the sequence stores.
- `RetroIO.Protocols.Xmsg.Servers` - the TAD server and the COSMOS file server.
- `RetroIO.Protocols.Xmsg.Sync` - the folder sync engine the daemon drives.
- `RetroIO.Protocols.Xmsg.Live` - the HDLC adapter and the Ethernet link.
