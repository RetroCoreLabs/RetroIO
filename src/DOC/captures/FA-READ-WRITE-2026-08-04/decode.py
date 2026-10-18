"""Decode a cap-*.txt hub capture into readable COSMOS/XMSG/FA lines.

Layers, outermost first:
  802.3 : dst(6) src(6) len(2) then LLC1 AA AA 03
  ND    : 11 bytes  0B 02 | kind | 00 | seq | dest-ref(2) | own-ref(2) | trailing(2)
  SINTRAN datagram : 7 words = 14 bytes, word 6 is the ones-complement checksum
  XMSG sub-header  : 14 bytes
  FA body          : msgtype(2) conversation(2) sessionhdr(4) then QFORM tag fields
"""
import sys

STATION = {"080026640000": "d100", "080026660000": "d102", "0800261F4E00": "d19999"}

FAOP = {
    1: "FileEntryDisconnect", 2: "ReserveFileEntry", 3: "ReleaseFileEntry",
    4: "ChangeFileEntryId", 5: "OpenFile", 6: "CloseFile", 7: "SetBlockSize",
    8: "ReadFile", 9: "WriteFile", 10: "CreateFile", 11: "DeleteFile",
    12: "SiiiSpecial", 13: "DeviceFunction",
}


def be16(b, i):
    return (b[i] << 8) | b[i + 1]


def checksum(words):
    """Ones-complement sum with end-around carry, then complement."""
    s = 0
    for w in words:
        s += w
        s = (s & 0xFFFF) + (s >> 16)
    return (~s) & 0xFFFF


def qform(body, start):
    """Walk the tagged fields of a QFORM body. Returns a list of (tag, value-hex)."""
    out = []
    i = start
    while i < len(body):
        tag = body[i]
        if tag in (0x92, 0xF2):          # class-1 two-byte integer
            if i + 3 > len(body):
                break
            out.append((tag, "%04X" % be16(body, i + 1)))
            i += 3
        elif tag == 0x8C:                # counted byte string
            if i + 2 > len(body):
                break
            n = body[i + 1]
            out.append((0x8C, body[i + 2:i + 2 + n].hex().upper()))
            i += 2 + n
        else:
            out.append((tag, "?" + body[i:].hex().upper()))
            break
    return out


def sevenbit(b):
    return "".join(chr(c & 0x7F) if 32 <= (c & 0x7F) < 127 else "." for c in b)


def decode(line):
    parts = line.split()
    if len(parts) < 3:
        return None
    ts, ln, hexs = parts[0], int(parts[1]), parts[2]
    f = bytes.fromhex(hexs)
    dst = STATION.get(f[0:6].hex().upper(), f[0:6].hex().upper())
    src = STATION.get(f[6:12].hex().upper(), f[6:12].hex().upper())
    # ND's COSMOS LLC1 uses SAP 0xA8, not the 0xAA SNAP pair.
    if f[14:17] != b"\xa8\xa8\x03":
        return "%s %s->%s NOT-LLC1 %s" % (ts, src, dst, hexs)
    p = f[17:]                            # ND link header starts here
    kind = p[2]
    seq = p[4]
    plen = be16(p, 9)
    head = "%s %-6s->%-6s kind=%02X seq=%3d plen=%4d" % (ts, src, dst, kind, seq, plen)
    if kind & 0xF0 != 0x20 or plen == 0:
        return head
    dg = p[11:11 + plen]
    if len(dg) < 14:
        return head + "  short-datagram " + dg.hex().upper()
    words = [be16(dg, i) for i in range(0, 14, 2)]
    ok = "ok" if checksum(words[0:6]) == words[6] else "BAD(%04X)" % checksum(words[0:6])
    head += "  dg[sub=%02X dst=%d src=%d f1=%04X f2=%04X ck=%s]" % (
        dg[3], words[2], words[3], words[4], words[5], ok)
    rest = dg[14:]
    if len(rest) < 14:
        return head + " tail=" + rest.hex().upper()
    # XMSG sub-header (14 bytes) then the FA body
    sub = rest[0:14]
    body = rest[14:]
    head += " sub=" + sub.hex().upper()
    if len(body) < 8:
        return head + " body=" + body.hex().upper()
    mt, conv = be16(body, 0), be16(body, 2)
    sh = body[4:8].hex().upper()
    head += "\n    FA mt=%04X conv=%04X sess=%s" % (mt, conv, sh)
    flds = qform(body, 8)
    if flds and flds[0][0] == 0x92:
        opn = int(flds[0][1], 16)
        head += " OP=%s(%d)" % (FAOP.get(opn, "?"), opn)
    head += "\n    fields: " + " ".join("%02X:%s" % (t, v) for t, v in flds[:24])
    txt = sevenbit(body[8:])
    if sum(c.isalnum() for c in txt) > 6:
        head += "\n    text  : " + txt[:200]
    return head


for raw in open(sys.argv[1], encoding="utf-8", errors="replace"):
    raw = raw.strip()
    if not raw or raw.startswith("#"):
        continue
    d = decode(raw)
    if d:
        print(d)
