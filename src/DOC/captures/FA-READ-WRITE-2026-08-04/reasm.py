"""Reassemble segmented FA messages and dump each complete message body as raw hex.

A datagram whose subtype is 0x0A is the FIRST fragment of a long message; the 0x0C that
follows carries the rest, and its Flags2 is the byte offset at which it resumes. Subtype
0x0E carries a message that fits in one datagram. This tool joins them and prints the FA
body (message type onward) so the layout can be read off the bytes.
"""
import sys

STATION = {"080026640000": "d100", "080026660000": "d102", "0800261F4E00": "d19999"}


def be16(b, i):
    return (b[i] << 8) | b[i + 1]


pending = {}

for raw in open(sys.argv[1], encoding="utf-8", errors="replace"):
    raw = raw.strip()
    if not raw or raw.startswith("#"):
        continue
    ts, _ln, hexs = raw.split()
    f = bytes.fromhex(hexs)
    if f[14:17] != b"\xa8\xa8\x03":
        continue
    src = STATION.get(f[6:12].hex().upper(), "?")
    dst = STATION.get(f[0:6].hex().upper(), "?")
    p = f[17:]
    if p[2] & 0xF0 != 0x20:
        continue
    plen = be16(p, 9)
    dg = p[11:11 + plen]
    if len(dg) < 14:
        continue
    sub = dg[3]
    flags2 = be16(dg, 10)
    key = (src, dst)

    if sub == 0x0A:                     # first fragment; keep it until the rest arrives
        pending[key] = (ts, flags2, dg[14 + 14:])
        continue
    if sub == 0x0C:                     # continuation: everything after the 14-byte header
        if key not in pending:
            continue
        ts0, total, head = pending.pop(key)
        body = head + dg[14:]
        print("%s %s->%s SEGMENTED total=%d assembled=%d resume=%d" %
              (ts0, src, dst, total, len(body), flags2))
        print("  " + body.hex().upper())
        continue
    if sub != 0x0E:
        continue
    body = dg[14 + 14:]
    if len(body) < 8:
        continue
    print("%s %s->%s len=%d" % (ts, src, dst, len(body)))
    print("  " + body.hex().upper())
