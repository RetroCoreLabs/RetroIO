# RetroIO.Network.Pcap

Puts an emulated network card on a real network adapter of the host, through SharpPcap: Npcap on
Windows, libpcap elsewhere. The emulated machine then has its own address on the real network.
Moved from RetroCore on 2026-11-22.

It is its own package so the rest of the network code never pulls a native dependency. A host that
wants it calls `PcapEthernetBackend.RegisterFactory()` once at start; after that the setting `pcap`
(first adapter) or `pcap:` followed by an adapter name gives this backend.

## Parts

| Type | What it does |
|---|---|
| `PcapEthernetBackend` | The backend: opens the adapter, sends frames, and passes on received frames meant for the emulated machine. |
| `PcapAdapterChoice`, `PcapAdapter` | Lists the adapters and picks one by name or description; with none named it takes the ND-Loopback adapter and says why. |
| `PcapSetupAdvice` | The text shown when the driver is missing, for each host system. |

Opening a real adapter is a hand test: the two tests that need Npcap and the ND-Loopback adapter
are marked explicit.
