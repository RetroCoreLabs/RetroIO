# RetroIO.Network.Ethernet

The host side of an emulated network card. An emulated card (a LANCE, an NE2000, an ND Ethernet
controller) hands every frame it sends to an `IEthernetBackend` and takes every frame it receives
from one. This package holds that interface, the registry that makes a backend from a text setting,
and the backends with no native dependency. Moved from RetroCore on 2026-11-22.

## Parts

| Type | What it does |
|---|---|
| `IEthernetController` | The other side: what an emulated card offers, event `FrameTransmitted` and `DeliverFrame`, so the host can join any card to any backend. |
| `IEthernetBackend` | What a card plugs into: `Send`, `Poll`, `Start`, `Stop`, event `OnFrameReceived`. |
| `IEthernetBackendStatus`, `IEthernetPortForwarding`, `IEthernetGuestAddresses` | Optional extras a backend may offer: a status line, port forwards (the NAT has them), and the card addresses given up front (the pcap backend needs them). |
| `EthernetBackends` | Makes a backend from a text setting such as `nat`, `pcap:Ethernet 2` or `tcp:host:port`. Each backend package registers its own maker. `none` or an empty setting gives no backend. |
| `EthernetFrameRing` | A bounded queue of frames between two threads; a frame that finds it full is dropped and counted, as on real Ethernet when a receive ring is full. |
| `IpChecksumRepair` | Recomputes IPv4, TCP and UDP checksums in frames from host adapters that leave them to the network card's hardware (checksum offload). |
| `NullEthernetBackend` | Drops what is sent and never receives; with loopback on, gives every frame straight back, for tests. |
| `TcpLinkBackend` | One emulated machine to another over a TCP connection. |
| `TcpHubBackend` | Many emulated machines through one TCP hub. |
| `UdpMulticastBackend` | Emulated machines on one host through UDP multicast. |
| `TapEthernetBackend` | A TAP interface on Linux, so the host itself is on the emulated network. |
| `EthernetLinkWire`, `EthernetLinkReceiveQueue`, `EthernetLinks` | The frame format the links share, their receive queue, and the registration of the link makers. |

The NAT to the internet is `RetroIO.Network.Nat`; the backend on a real host adapter is
`RetroIO.Network.Pcap`. The design is RetroCore's host networking design
(host-networking-design.md in the RetroCore repository).
