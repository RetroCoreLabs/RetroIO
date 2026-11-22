# RetroIO.Network.Router

A small IPv4 router for emulated dial-up providers, used by RetroServer and RetroCore. Each dial-up
call (SLIP now, PPP later) is one point-to-point link with its own address on one subnet. The
router passes IP packets between the links, the owner's own services, and (later) NAT to the
internet.

It owns no socket, no thread and no timer. The owner calls it from its own thread, and every event
is raised from inside the call that caused it.

## Parts

| Type | What it does |
|---|---|
| `IpRouter` | The router. `AddLink` and `RemoveLink`; `SendFromLocal` for packets from the owner's services; `SendFromUpstream` for packets coming back from the internet; events `OnLocal`, `OnUpstream` and `OnDrop`; `DropCount` per reason. |
| `RouterLink` | One link. `Receive` takes a packet from the far end; packets for it go to the delegate given to `AddLink`. Counters: packets and bytes, in and out. |
| `RouterSettings` | Subnet (default 10.42.0.0/24), router address (default 10.42.0.1), address pool. The prefix length can be 8 to 30. |
| `Ipv4` | Address text, header checks, checksum, TTL decrement. |
| `DropReason` | Why a packet was dropped: bad header, spoofed source, TTL expired, no route, too big. |

## Where a packet goes

By its destination address:

- **The router address**: to the local services (`OnLocal`).
- **A broadcast** (the subnet broadcast, or 255.255.255.255): to the local services **only**, never to
  the other links. RFC 1812 section 5.3.5.2 lets a router forward a directed broadcast, says it must be
  possible to turn that off, and makes off the default. This router never forwards one, so a caller
  cannot reach every other caller with one packet.
- **A link's address**: to that link.
- **Another address in the subnet**: dropped, no route.
- **Outside the subnet**: to `OnUpstream` (the NAT in `RetroIO.Network.Nat`); dropped, no route, when nothing is attached.

## Rules a packet has to pass

- A good IPv4 header: version 4, header length at least 20 bytes, total length within the packet,
  correct checksum (RFC 791, RFC 1812 section 5.2.2). Bytes past the total length are cut off.
- The source is the sender's own address: the link's address, or the router address for
  `SendFromLocal`. Anything else is dropped as spoofed.
- A forwarded packet has its TTL lowered by one and its checksum corrected by RFC 1624; a packet whose
  TTL would reach 0 is dropped (RFC 1812 section 5.3.1).
- A packet from upstream (`SendFromUpstream`) must have a source outside the subnet, or it is dropped
  as spoofed. It is delivered only to an address inside the subnet: one outside, or the subnet
  broadcast, is dropped as no route, so nothing goes back the way it came. A packet for the router itself is delivered,
  not forwarded, and keeps its TTL.
- A packet larger than the link it is for carries (the link's MTU) is dropped. The router does not
  fragment.

## Not done yet

- ICMP replies (time exceeded, destination unreachable, echo for the router address).
- A bridge to the real LAN (step 3).
- An Ethernet link kind with ARP and DHCP, and a tick in emulated time, which RetroCore's NAT needs.
