#!/usr/bin/env python3
"""Generate the Wireshark dissector's constant tables from the registries.

WHY THIS EXISTS. The dissector used to carry its own hand-typed copies of the
XROUT service codes, the ND link frame kinds and so on. Hand-typed copies drift:
the registry gains a value, nobody remembers the Lua file, and a capture is then
read with names that are a month out of date. generate_c.py and
generate_csharp.py already solved this for C and C#. This is the same idea for
Lua.

WHY IT WRITES INTO THE DISSECTOR INSTEAD OF ITS OWN FILE. A Wireshark Lua
dissector is loaded with a single "-X lua_script:<file>" and the script's own
directory is not on Lua's search path, so a second file it could `require` would
not be found. The dissector therefore has to stay ONE file, and the generated
tables are written into a marked region inside it:

    -- @@BEGIN GENERATED FROM DOC/protocols BY generate_lua.py - DO NOT EDIT @@
    ...
    -- @@END GENERATED@@

Everything outside the markers is hand-written and is never touched.

WHAT IT EMITS. For every enum-shaped block in the registries, two tables:

    REG.<name>     value -> { name=, status=, meaning= }   the full record
    REG.<name>_vs  value -> "NAME [STATUS]"                a Wireshark value string

The status is appended to the value string for anything that is not MEASURED, so
a guess is labelled AS a guess in the dissection tree rather than being displayed
with the same confidence as an observed fact.

Usage:
    python generate_lua.py            rewrite the block in the dissector
    python generate_lua.py --check    fail if the block is stale (nothing written)
    python generate_lua.py --out F    write the whole dissector somewhere else
"""

import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# The dissector this generator writes into, relative to this folder.
DISSECTOR = os.path.normpath(os.path.join(
    HERE, "..", "..", "..", "Devices", "HDLC", "WireShark", "hdlc_tcp.lua"))

BEGIN = "-- @@BEGIN GENERATED FROM DOC/protocols BY generate_lua.py - DO NOT EDIT @@"
END = "-- @@END GENERATED@@"

PROBLEMS = []


def to_int(value):
    """Registry values are written either as 0x-hex strings or as plain numbers."""
    if isinstance(value, int):
        return value
    text = str(value).strip()
    if text.lower().startswith("0x"):
        return int(text, 16)
    return int(text, 10)


def lua_string(text):
    """A Lua double-quoted string. ASCII only - the dissector is an ASCII file."""
    out = []
    for ch in str(text):
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch in "\r\n\t":
            out.append(" ")
        elif 32 <= ord(ch) < 127:
            out.append(ch)
        else:
            # Anything outside plain ASCII would break the "no unicode" rule the
            # repository holds this file to, so it is dropped rather than escaped.
            out.append("?")
    return '"' + "".join(out) + '"'


def short(text, limit=110):
    """One line, trimmed. The full evidence stays in the registry."""
    if text is None:
        return None
    line = " ".join(str(text).split())
    if len(line) > limit:
        line = line[:limit - 3] + "..."
    return line


def collect(entries):
    """value -> { names[], status, meaning }.

    Two registry entries can share one value on purpose - XSDMC and XSDSY are the
    same service call under two names. A plain dict would silently keep whichever
    came last, so the names are gathered into a list and joined with a slash.
    """
    table = {}
    for entry in entries:
        raw = entry.get("value")
        if raw is None:
            raw = entry.get("mask")
        if raw is None:
            PROBLEMS.append("an entry named %s has no value" % entry.get("name"))
            continue
        try:
            key = to_int(raw)
        except ValueError:
            PROBLEMS.append("value %r is not a number" % raw)
            continue

        names = entry.get("names") or [entry.get("name")]
        record = table.get(key)
        if record is None:
            record = {"names": [], "status": entry.get("status"),
                      "meaning": entry.get("meaning")}
            table[key] = record
        for name in names:
            if name and name not in record["names"]:
                record["names"].append(name)
        if record["meaning"] is None:
            record["meaning"] = entry.get("meaning")
    return table


def emit_enum(lua_name, source, entries, out):
    """One registry value list, as two Lua tables."""
    table = collect(entries)
    out.append("-- %s -- from %s. %d values." % (lua_name, source, len(table)))
    out.append("REG.%s = {" % lua_name)
    for key in sorted(table):
        record = table[key]
        name = "/".join(record["names"])
        parts = ["name = %s" % lua_string(name)]
        if record["status"]:
            parts.append("status = %s" % lua_string(record["status"]))
        if record["meaning"]:
            parts.append("meaning = %s" % lua_string(short(record["meaning"])))
        out.append("    [0x%X] = { %s }," % (key, ", ".join(parts)))
    out.append("}")

    # The value string is what a reader sees on the field itself. A status that
    # is not MEASURED is carried into it, so nothing we are guessing at is shown
    # with the same confidence as something observed on the wire.
    out.append("REG.%s_vs = {" % lua_name)
    for key in sorted(table):
        record = table[key]
        label = "/".join(record["names"])
        status = record["status"]
        if status and status != "MEASURED":
            label = "%s [%s]" % (label, status)
        out.append("    [0x%X] = %s," % (key, lua_string(label)))
    out.append("}")
    out.append("")


def build():
    catalog = json.load(io.open(os.path.join(HERE, "catalog.json"), encoding="utf-8"))

    out = [BEGIN]
    out.append("-- Constant tables read straight out of DOC/protocols. Regenerate with:")
    out.append("--     python DOC/protocols/generate_lua.py")
    out.append("-- and check for staleness with --check. Editing between the markers by")
    out.append("-- hand is pointless - the next run overwrites it.")
    out.append("--")
    out.append("-- Each block gives REG.<name> (the full record, with the status and the")
    out.append("-- plain-English meaning) and REG.<name>_vs (a Wireshark value string, with")
    out.append("-- the status appended whenever it is not MEASURED).")
    out.append("local REG = {}")
    out.append("")

    # Which block in which registry becomes which Lua table. Keeping the map here
    # rather than guessing from the file means a registry that grows a new block
    # is a one-line change, and a block that disappears is a loud failure.
    plan = [
        ("sintran-wire.json", ("bitfields", "nd_link_frame_kind"), "nd_link_frame_kind"),
        ("xrout-services.json", ("services",), "xrout_service"),
        ("xrout-services.json", ("errors",), "xrout_error"),
        ("xrout-services.json", ("connection_types",), "xrout_connection_type"),
        ("fa-qform.json", ("operations",), "fa_operation"),
        ("fa-qform.json", ("status_codes",), "fa_status"),
        ("fa-qform.json", ("message_types",), "fa_message_type"),
        ("fa-qform.json", ("qform", "classes"), "qform_class"),
        ("tad-wire.json", ("control_services",), "xmcsm_service"),
        ("tad-wire.json", ("operations",), "tad_op"),
        ("tad-wire.json", ("error_codes",), "tad_error"),
        ("chat-wire.json", ("message_kinds",), "chat_kind"),
    ]

    known = set()
    for entry in catalog["registries"]:
        known.add(entry["file"])

    docs = {}
    for entry in sorted(catalog["registries"], key=lambda e: e["read_order"]):
        docs[entry["file"]] = json.load(
            io.open(os.path.join(HERE, entry["file"]), encoding="utf-8"))

    for filename, path, lua_name in plan:
        if filename not in known:
            PROBLEMS.append("%s is not in the catalog" % filename)
            continue
        node = docs[filename]
        for step in path:
            node = (node or {}).get(step)
            if node is None:
                break
        if node is None:
            PROBLEMS.append("%s has no %s - the registry moved and this plan is stale"
                            % (filename, "/".join(path)))
            continue
        entries = node if isinstance(node, list) else node.get("values")
        if not entries:
            PROBLEMS.append("%s/%s holds no values" % (filename, "/".join(path)))
            continue
        emit_enum(lua_name, "%s %s" % (filename, "/".join(path)), entries, out)

    # The ND link kind block keeps a few byte values OUTSIDE its enum, because
    # they are constants in the C# code rather than enum members - the connection
    # confirm 0x1F is one. A dissector has to recognise them all the same, so they
    # are folded into the same table here.
    kinds = (docs["sintran-wire.json"]["bitfields"]["nd_link_frame_kind"]
             .get("constants") or {}).get("values") or []
    if kinds:
        out.append("-- Byte values the registry lists as CONSTANTS beside the kind enum")
        out.append("-- (sintran-wire.json nd_link_frame_kind/constants). They are real wire")
        out.append("-- bytes, so a decoder needs them even though the C# enum has no member.")
        for entry in kinds:
            key = to_int(entry["value"])
            label = entry["name"]
            status = entry.get("status")
            if status and status != "MEASURED":
                label = "%s [%s]" % (label, status)
            out.append("REG.nd_link_frame_kind[0x%X] = { name = %s, status = %s }"
                       % (key, lua_string(entry["name"]), lua_string(status or "UNKNOWN")))
            out.append("REG.nd_link_frame_kind_vs[0x%X] = %s" % (key, lua_string(label)))
        out.append("")

    out.append(END)
    return "\n".join(out)


def main():
    generated = build()

    source = io.open(DISSECTOR, encoding="utf-8", newline="").read()
    start = source.find(BEGIN)
    stop = source.find(END)
    if start < 0 or stop < 0:
        print("The dissector has no generated block. Add these two lines where the")
        print("constants belong and run again:")
        print("    " + BEGIN)
        print("    " + END)
        return 2

    newline = "\r\n" if "\r\n" in source else "\n"
    body = generated.replace("\n", newline)
    updated = source[:start] + body + source[stop + len(END):]

    if "--check" in sys.argv:
        if updated != source:
            print("STALE: %s does not match the registries. Run generate_lua.py."
                  % DISSECTOR)
            return 1
        print("up to date: %s" % DISSECTOR)
        return 0

    out_path = DISSECTOR
    if "--out" in sys.argv:
        out_path = sys.argv[sys.argv.index("--out") + 1]
    io.open(out_path, "w", encoding="utf-8", newline="").write(updated)

    lines = generated.count("\n") + 1
    print("wrote %d generated lines into %s" % (lines, out_path))

    if PROBLEMS:
        print("\nWHAT THE REGISTRY STILL LACKS FOR LUA:")
        for problem in sorted(set(PROBLEMS)):
            print("  " + problem)
        return 1
    print("no gaps.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
