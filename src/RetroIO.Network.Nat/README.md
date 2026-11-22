# RetroIO.Network.Nat

Address sharing (NAT) for `RetroIO.Network.Router`, for TCP, UDP and ping. Every packet a caller sends to an address
outside the router's subnet goes to the NAT. The NAT sends the data on with an ordinary socket of the
host, and turns what comes back into packets for that caller. No raw sockets, so no administrator
rights are needed.

It owns no thread and no timer. The owner calls `Service` from the thread that drives the router
and passes the time.

## Parts

| Type | What it does |
|---|---|
| `IpNat` | Attaches to a router's upstream side. `Service(now, wait)` reads and writes the host sockets, runs the TCP timers and closes idle mappings; `UdpMappings`, `TcpConnections`, `HostConnectsRefused`, `TcpRetransmits`, `PingsWaiting`, `PingsUnanswered`, `Reassemblies`, `DropCount` per reason, `HostPortUnreachable`; event `OnDrop`. `Dispose` leaves the router and closes every socket. |
| `NatDropReason` | Why a packet was dropped: unsupported protocol, fragment, not unicast, bad UDP, too many mappings, too big for IP, host send failed, bad TCP, too many connections, host connect failed, bad ICMP, too many pings, too many reassemblies, reassembly timeout. |

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

## TCP

The caller's TCP connection ends in the NAT, which is the far end of it as RFC 9293 describes one.
The bytes go on over an ordinary host connection to the address and port the caller asked for.

- The caller's SYN starts a host connect. The SYN-ACK goes back only once the host connection is
  up, so a refused connect is a reset to the caller's SYN. A connect that fails another way (a
  timeout) gets no answer; the caller gives up by itself.
- Our MSS is 1460; the caller's MSS option is used, 536 when it sends none (RFC 9293 section
  3.7.1). No window scaling, selective acknowledgment or timestamps are offered, so none is used.
- Data from the caller is taken in order only; anything else is answered with an ACK for the next
  byte. The window offered is the free space of a 64 KB buffer toward the host.
- Toward the caller: at most the caller's window is in flight. After 1 second without an
  acknowledgment everything unacknowledged is sent again, and the wait doubles up to 60 seconds
  (RFC 6298). After 12 tries the connection is reset. A zero window is probed with one byte.
- The host is not read while 64 KB wait for the caller, so the host's own TCP slows the far end.
- A FIN from the caller ends the host's sending side once its data is written; the end of the host
  stream becomes our FIN. The side that closed first waits 4 minutes in TIME-WAIT (RFC 9293 section
  3.4.2). Resets go both ways. A segment for no connection gets a reset.
- At most 256 connections at once; a SYN beyond that gets a reset.

## ICMP

- A caller's echo request goes on with the host's own ping, which needs no administrator rights. The
  answer is rebuilt with the caller's identifier, sequence number and the data that came back
  (RFC 5508 section 3.1). At most 64 wait at once; the host waits 4 seconds for each.
- What the host's ping reports becomes the matching message (RFC 792): echo reply; time exceeded
  when the TTL ran out; destination unreachable (network, host, protocol or port); nothing on a
  timeout. Any other result is counted in `PingsUnanswered`.
- A UDP "port unreachable" the host reports becomes destination unreachable, code 3, quoting the
  caller's last datagram on that mapping. Only Windows reports it to an unconnected socket; on other
  systems the caller hears nothing.
- Other ICMP from a caller is dropped as an unsupported protocol.

## Fragments

A host socket takes whole datagrams only, so a caller's fragmented packet is put back together first
(RFC 791 section 3.2). Fragments may come in any order, repeat or overlap. At most 64 packets are
put together at once. One not complete after 60 seconds (RFC 1122 section 3.3.2) is dropped, and
when its first fragment had arrived the caller gets ICMP time exceeded, code 1. A fragment that
would end past 65535 bytes, or a fragment other than the last whose data is not a multiple of 8
bytes, is dropped.

## Not built yet

Each is dropped and counted, never answered with something made up:

- TCP urgent data (the URG flag and pointer are not carried).
- ICMP messages from a caller other than echo request.
- An ICMP message to the caller when a TCP host connect fails for a reason other than a refusal.
