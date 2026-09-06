# RetroIO.Hdlc

One HDLC framing implementation, shared by the X.25 and the SINTRAN/XMSG stacks.

It replaced five separate framers that had drifted apart: two of them lost a frame after a shared
flag, two had no maximum frame length at all, one treated end of buffer as a frame terminator, and
the two FCS helpers had methods of the same name whose results differed by a final complement.

## What is in it

| Type | Role |
|---|---|
| `HdlcFcs` | FCS-16 arithmetic: poly x16+x12+x5+1 reflected, init all ones, complemented on transmit, residue `0xF0B8` |
| `HdlcFramerOptions` | Per-link policy: transparency, transmit ACCM, min and max frame length, whether the framer owns the FCS |
| `HdlcEncoder` | Pure, stateless encode |
| `HdlcDecoder` | Streaming decode, one instance per link direction, zero allocation per frame |

## The one thing to get right

`HdlcFramerOptions.SendAccm` decides how much is escaped on transmit, and the two stacks genuinely
differ:

- `HdlcFramerOptions.X25` escapes every octet below `0x20`, the RFC 1662 section 7.1 default.
- `HdlcFramerOptions.Xmsg` escapes only the flag and the control escape, the ITU-T X.25
  clause 2.2.2.2 minimum. This is what the real ND machines do - across the whole capture corpus
  the only octets ever following `0x7D` are `0x5D` and `0x5E`.

Receive needs no such option: unescaping is a blind XOR `0x20`, so a decoder reads either.

## References

ITU-T X.25 (10/96) clause 2, ITU-T Q.921 (09/97), RFC 1662. Clause numbers are cited in the XML
docs at each decision they justify.
