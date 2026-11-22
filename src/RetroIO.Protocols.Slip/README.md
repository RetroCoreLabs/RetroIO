# RetroIO.Protocols.Slip

SLIP, the Serial Line Internet Protocol of RFC 1055, for dial-up lines. It turns line bytes into
whole IP packets and IP packets into line bytes.

It owns no serial port, no thread and no timer. The owner feeds it line bytes and gets packets
back through handlers, all on the owner's thread. That makes it usable behind an emulated modem in
RetroCore, in RetroServer's dial-up provider, or inside a test.

## Parts

| Type | What it does |
|---|---|
| `SlipLink` | `ReceiveFromLine` takes line bytes in any split and raises `OnPacket` per packet. `SendPacket` raises `OnLineOutput` with the framed bytes. Bad packets raise `OnError` with a reason and a running count. |
| `SlipError` | Why a packet was dropped: a bad escape, or longer than the maximum. |

## Framing (RFC 1055)

- END (C0h) ends a packet. A packet is also sent with END before it, which flushes line noise;
  the empty packet this makes is ignored.
- A C0h data byte goes as DBh DCh, a DBh data byte as DBh DDh.
- The largest packet defaults to 1006 bytes, the size RFC 1055 gives for Berkeley UNIX SLIP.

## Different from RFC 1055 on purpose

RFC 1055's sample receiver keeps a byte after ESC that is not DCh or DDh, and drops bytes past its
buffer, then passes the packet on. Here such a packet is dropped and reported through `OnError`:
a packet the line has changed is never passed on as good.

## Planned

- CSLIP, the TCP/IP header compression of RFC 1144, in this package.
- PPP (RFC 1661, 1662) is a separate package.
