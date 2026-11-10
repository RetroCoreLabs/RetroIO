# RetroIO.Protocols.Xmsg.Hub

## What it is

The COSMOS Ethernet hub: a virtual Ethernet segment that ND machines join over TCP. Every frame one member sends
reaches every other member and never goes back to the sender, as on a real hub. Hubs can join each other to make one
segment out of several.

The package has the hub itself (`HubServer`), a pcap writer, a buffer of recent frames, a buffer of log lines, and a
hosted service (`XmsgHubService`) for a program built on the .NET generic host.

## Why you need it

A TCP link joins exactly two machines. COSMOS over Ethernet needs a shared wire with several machines on it, plus a
place to watch the traffic from. On Windows, host-local traffic cannot be captured with Wireshark unless a loopback
adapter is installed. The hub sees every frame exactly once, so it can keep them.

The hub was a console program only (`xmsghub`, in the RetroXmsg repository) until 2026-11-10. It moved here so
RetroServer can host it, switch it on and off from its settings, and show its log and frames in the admin UI.

## What you can use it for

- Running a segment for RetroCore machines: `device add ETH 0 --net=tcp:HOST:5010`.
- Hosting it in a service: `services.AddXmsgHub(o => o.Port = 5010)`.
- Seeing what crossed the wire: `HubServer.FrameLog` keeps each frame once, as it went from its sender to the hub.
  `HubFrameRing.WritePcap` turns any slice of them into a pcap file.
- Keeping a complete record of a run: `HubServer.StartCapture(path)` writes every frame to a pcap file.
- Joining segments: give a hub an uplink to another hub.

## Wire format

This is what every member speaks, and what `TcpEthernetBackend` already sends:

- A 5-byte hello: `R E T H` and a role byte, `1` for a machine and `2` for a hub. The hub always answers as a hub.
- Each frame: a 2-byte big-endian length, then the raw Ethernet frame. An ND frame is 802.3 with LLC `A8 A8 03`, the
  11-byte ND link header, then the SINTRAN datagram.
- On hub-to-hub links only, each frame is preceded by a hop count and a 4-byte origin id. These stop a frame that has
  gone round a loop of hubs.

## Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET SDK as pinned in `global.json` | Everything | |
| A free TCP port | Running | 5010 by default. On some Windows machines ASUS Armoury Crate takes 5010 after a reboot |

## Behaviour worth knowing

- A slow member cannot stall the segment. Each member has its own bounded queue and sending thread. A member that stops
  reading loses frames, and only that member loses them.
- The service does not stop its host when the port is taken. It reports `Failed` with the reason, and the other
  protocols keep running.
- The frame buffer is a view for people, not a capture. When it is full the oldest frame goes. The numbers never repeat,
  so a reader can see that frames were lost. Use the capture file for a complete record.

## Tests

`src/RetroIO.Protocols.Xmsg.Hub/tests`. They include the 152 frames of a real capture: LIST-NAMES from D102 to D100,
taken on 2026-11-10. Every test hub listens on port 0.

## Related

- `RetroIO.Protocols.Xmsg.Ethernet` - the ND link layer and the TCP backend a machine uses to join a hub.
- RetroServer hosts this hub. Its settings are in `xmsghub-config.json`, and its admin page is "XMSG Hub".
