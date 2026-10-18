#!/usr/bin/env python3
"""Generate a C# protocol library from the registries.

**This is a DRY RUN by default.** It writes to a temp folder, not into SRC, so the
question it answers is "is the registry rich enough to produce the library we
already hand-wrote, comments and all?" - before anything depends on the answer.

Generating C# first is deliberate. There is a hand-written C# library to compare
against, so a shortfall shows up as a difference from working code rather than as
a bug discovered later in a language nobody has tested yet. Whatever is missing
here would be missing for C and TypeScript too.

Usage:
    python generate_csharp.py                 write to the temp folder and report
    python generate_csharp.py --out DIR       write somewhere specific
"""

import io
import json
import re as _re
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))

CS_TYPE = {8: "byte", 16: "ushort", 32: "uint", 64: "ulong"}


def check_xml_safe(text, where, problems):
    """Registry prose becomes an XML doc comment, so it must not contain XML metacharacters.

    NOT escaped - escaping is banned in comments here, and a comment full of ampersand
    entities is unreadable in the editor where it matters. The prose is rewritten
    instead: the ND manuals themselves write AND rather than the ampersand.
    """
    if text is None:
        return
    body = str(text)
    if "&" in body:
        problems.append("%s: contains an ampersand - write AND, as the ND manuals do" % where)
    if _re.search(r"<[a-zA-Z/]", body):
        problems.append("%s: contains a '<' that reads as a tag - rephrase" % where)


PROBLEMS = []


def wrap(text, width, indent):
    """Wraps prose for an XML doc comment."""
    words = str(text).split()
    lines, current = [], indent
    for word in words:
        if len(current) + len(word) + 1 > width and current != indent:
            lines.append(current)
            current = indent + word
        else:
            current = (current + " " + word) if current != indent else indent + word
    if current != indent:
        lines.append(current)
    return lines


def doc_block(summary, remarks, indent="    "):
    """An XML doc comment: summary, then any remarks as separate paragraphs."""
    check_xml_safe(summary, "summary", PROBLEMS)
    for remark in (remarks or []):
        check_xml_safe(remark, "remark", PROBLEMS)
    out = [indent + "/// <summary>"]
    for line in wrap(summary, 100, indent + "/// "):
        out.append(line)
    out.append(indent + "/// </summary>")
    if remarks:
        out.append(indent + "/// <remarks>")
        for para in remarks:
            if not para:
                continue
            out.append(indent + "/// <para>")
            for line in wrap(para, 100, indent + "/// "):
                out.append(line)
            out.append(indent + "/// </para>")
        out.append(indent + "/// </remarks>")
    return out


def provenance(entry):
    """The status and evidence, as a remark. This is the point of generating from the registry."""
    status = entry.get("status")
    if not status:
        return None
    evidence = entry.get("evidence")
    if evidence:
        return "%s: %s" % (status, evidence)
    return status


def gen_enum(block, name_hint, namespace, header):
    """One enum, from a values/bits block."""
    binding = (block.get("bindings") or {}).get("c") or {}
    enum_name = to_pascal(binding.get("enum") or name_hint)
    underlying = binding.get("underlying_type", "uint16_t")
    cs = {"uint8_t": "byte", "uint16_t": "ushort", "uint32_t": "uint",
          "int16_t": "short"}.get(underlying, "ushort")

    entries = block.get("values") or block.get("bits") or []
    is_flags = bool(block.get("bits"))

    out = list(header)
    out.append("namespace %s" % namespace)
    out.append("{")
    out += doc_block(
        block.get("note") or ("Generated from the protocol registry: %s." % enum_name),
        ["GENERATED from DOC/protocols - do not edit. Every value carries the status and evidence "
         "the registry records for it."])
    if is_flags:
        out.append("    [System.Flags]")
    out.append("    public enum %s : %s" % (enum_name, cs))
    out.append("    {")

    for entry in entries:
        names = entry.get("names") or [entry.get("name")]
        value = entry.get("value")
        if value is None and entry.get("mask") is not None:
            value = entry["mask"]
        for member in names:
            if member is None:
                continue
            remarks = []
            p = provenance(entry)
            if p:
                remarks.append(p)
            if entry.get("trap"):
                remarks.append("TRAP: " + entry["trap"])
            if entry.get("alias_of"):
                remarks.append("Same value as %s - one service under two names." % entry["alias_of"])
            if entry.get("shared"):
                remarks.append("ONE BIT, SHARED between %s. Disambiguated by %s."
                               % (" and ".join(names), entry.get("disambiguated_by", "?")))
            out += doc_block(entry.get("meaning") or member, remarks, "        ")
            literal = value if isinstance(value, str) else str(value)
            out.append("        %s = %s," % (member, literal))
            out.append("")

    out.append("    }")
    out.append("}")
    return enum_name, "\n".join(out) + "\n"


def gen_struct(name, struct, namespace, header):
    """A reader/writer for a wire structure, as accessors rather than a packed layout."""
    binding = (struct.get("bindings") or {}).get("c") or {}
    type_name = to_pascal(binding.get("struct") or name)

    out = list(header)
    out.append("using System;")
    out.append("")
    out.append("namespace %s" % namespace)
    out.append("{")
    out += doc_block(
        "The %s, %s bytes, %s." % (name, struct.get("size_bytes"), struct.get("byte_order")),
        ["GENERATED from DOC/protocols - do not edit.",
         binding.get("note") or ""])
    out.append("    public static class %s" % type_name)
    out.append("    {")
    out += doc_block("The size on the wire, in bytes.", None, "        ")
    out.append("        public const int Size = %d;" % struct.get("size_bytes", 0))
    out.append("")

    for field in struct.get("fields", []):
        bits = field.get("width_bits", 16)
        cs = CS_TYPE.get(bits, "ushort")
        member = to_pascal(field["name"])
        offset = field.get("byte_offset", 0)

        remarks = []
        p = provenance(field)
        if p:
            remarks.append(p)
        if field.get("rule"):
            remarks.append("Rule: " + field["rule"])
        for sup in field.get("supersedes", []):
            remarks.append("SUPERSEDED READING, do not re-derive: " + sup.get("reading", ""))

        out += doc_block(field.get("meaning") or member, remarks, "        ")
        out.append("        public static %s Read%s(ReadOnlySpan<byte> frame)" % (cs, member))
        out.append("        {")
        if bits == 16:
            out.append("            return (ushort)((frame[%d] << 8) | frame[%d]);" % (offset, offset + 1))
        elif bits == 8:
            out.append("            return frame[%d];" % offset)
        else:
            out.append("            throw new NotSupportedException(\"width %d\");" % bits)
        out.append("        }")
        out.append("")

    out.append("    }")
    out.append("}")
    return type_name, "\n".join(out) + "\n"


def to_pascal(name):
    return "".join(part.capitalize() for part in str(name).replace("-", "_").split("_"))


def main():
    out_dir = None
    if "--out" in sys.argv:
        out_dir = sys.argv[sys.argv.index("--out") + 1]
    if not out_dir:
        out_dir = os.path.join(tempfile.gettempdir(), "xmsg-generated-csharp")

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)

    catalog = json.load(io.open(os.path.join(HERE, "catalog.json"), encoding="utf-8"))
    header = [
        "// <auto-generated>",
        "//     Generated from DOC/protocols by generate_csharp.py. DO NOT EDIT.",
        "//     Every summary, status and evidence line below comes from the registry.",
        "// </auto-generated>",
        "",
    ]

    written = []
    for entry in sorted(catalog["registries"], key=lambda e: e["read_order"]):
        doc = json.load(io.open(os.path.join(HERE, entry["file"]), encoding="utf-8"))
        namespace = "NDInsight.Sintran.Xmsg.Generated"

        for key in ("operations", "services", "errors", "connection_types",
                    "status_codes", "control_services"):
            block = doc.get(key)
            if not block or not (block.get("values")):
                continue
            name, text = gen_enum(block, key, namespace, header)
            path = os.path.join(out_dir, name + ".cs")
            io.open(path, "w", encoding="utf-8", newline="").write(text)
            written.append((entry["file"], name + ".cs", len(text.splitlines())))

        for bf_name, bf in (doc.get("bitfields") or {}).items():
            name, text = gen_enum(bf, bf_name, namespace, header)
            path = os.path.join(out_dir, name + ".cs")
            io.open(path, "w", encoding="utf-8", newline="").write(text)
            written.append((entry["file"], name + ".cs", len(text.splitlines())))

        for st_name, st in (doc.get("structures") or {}).items():
            name, text = gen_struct(st_name, st, namespace, header)
            path = os.path.join(out_dir, name + ".cs")
            io.open(path, "w", encoding="utf-8", newline="").write(text)
            written.append((entry["file"], name + ".cs", len(text.splitlines())))

    print("generated into %s" % out_dir)
    for source, file, lines in written:
        print("  %-22s -> %-28s %4d lines" % (source, file, lines))
    print("\n%d file(s). This is a DRY RUN - nothing under SRC was touched." % len(written))
    if PROBLEMS:
        print("")
        print("PROSE THAT WOULD BREAK THE GENERATED COMMENTS:")
        for problem in sorted(set(PROBLEMS)):
            print("  " + problem)
        print("Fix the registry TEXT - do not escape it. See DOC/protocols/README.md.")
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
