# RetroIO.Network.Nat

Address sharing (NAT) for `RetroIO.Network.Router`. Every packet a caller sends to an address
outside the router's subnet goes to the NAT. The NAT sends the data on with an ordinary socket of the
host, and turns what comes back into packets for that caller. No raw sockets, so no administrator
rights are needed.

It owns no thread and no timer. The owner calls `Service` from the thread that drives the router
and passes the time.

## Parts

| Type | What it does |
|---|---|
| `IpNat` | Attaches to a router's upstream side. `Service(now, wait)` reads the host sockets and closes idle mappings; `UdpMappings`, `DropCount` per reason, `HostPortUnreachable`; event `OnDrop`. `Dispose` leaves the router and closes every socket. |
| `NatDropReason` | Why a packet was dropped: unsupported protocol, fragment, not unicast, bad UDP, too many mappings, too big for IP, host send failed. |

## UDP

- One mapping per caller address and UDP port, used for every destination (RFC 4787 REQ-1). The
  host picks the outside port.
- Any outside host may answer on a mapping (endpoint independent filtering, allowed by RFC 4787
  REQ-8).
- A mapping closes two minutes after its last datagram in either direction (RFC 4787 REQ-5).
- At most 256 mappings at once.
- The caller's UDP checksum is checked when it is not 0 (RFC 768). Replies carry a correct checksum.
- Replies go to the router's `SendFromUpstream`, which lowers the TTL by one and delivers them to
  the caller's link.

## Not built yet

Each is dropped and counted, never answered with something made up:

- TCP.
- ICMP: ping to outside addresses, and the "port unreachable" message back to the caller when the
  host reports one (counted in `HostPortUnreachable`).
- IP fragments from a caller.
