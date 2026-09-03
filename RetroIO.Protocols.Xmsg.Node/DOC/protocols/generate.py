#!/usr/bin/env python3
"""Turn the protocol registries into readable markdown with mermaid diagrams.

The JSON is the source of truth. The markdown is generated FROM it and must never
be hand-edited - `generate.py --check` fails if the committed markdown does not
match what the JSON would produce, so a registry change without a regenerate is
caught rather than silently shipping a stale document.

It also CROSS-CHECKS the flows: every operation or message a flow names must
exist in that protocol's registry. A ladder that cites an operation we do not
have is a ladder describing a protocol we no longer speak, and that is precisely
the drift this whole arrangement exists to stop.

Usage:
    python generate.py           regenerate the markdown
    python generate.py --check   fail if the markdown is out of date (for CI)
"""

import hashlib
import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

def catalogued():
    """The registries, in the order catalog.json says to meet them.

    Read from the catalog rather than listed here, so adding a protocol means
    editing ONE file. A generator with its own list quietly stops emitting a page
    somebody added, and nothing says so.
    """
    cat = json.load(io.open(os.path.join(HERE, "catalog.json"), encoding="utf-8"))
    entries = sorted(cat["registries"], key=lambda e: e["read_order"])
    return [(e["file"], e["markdown"], e) for e in entries]

BADGE = {
    "MEASURED": "**MEASURED**",
    "INFERRED": "*inferred*",
    "UNKNOWN": "**UNKNOWN**",
    "SUPERSEDED": "~~superseded~~",
    "KNOWN_GAP": "*known gap*",
}


def badge(status):
    """Renders a status so the eye can sort a page at a glance."""
    return BADGE.get(status, status or "")


def esc(text):
    """Escapes a value for a markdown table cell."""
    if text is None:
        return ""
    return str(text).replace("|", "\\|").replace("\n", " ")


# ---------------------------------------------------------------------------
# structures and bitfields
# ---------------------------------------------------------------------------

def render_structure(name, struct, out):
    out.append("### `%s`" % name)
    out.append("")
    size = struct.get("size_bytes")
    words = struct.get("words")
    if size:
        # A structure that is not a whole number of words carries no "words" count - the ND link
        # header is 11 bytes. Printing "None words" there is worse than saying nothing, so the
        # word clause only appears when the registry actually has one.
        if words:
            out.append("%s bytes, %s words, %s."
                       % (size, words, struct.get("byte_order", "big-endian")))
        else:
            out.append("%s bytes, %s."
                       % (size, struct.get("byte_order", "big-endian")))
        out.append("")

    fields = struct.get("fields", [])

    # A packet diagram beats a table for seeing the SHAPE. Bit ranges are word
    # numbers times 16 so the picture matches the words the manuals talk in.
    if fields and all("word" in f for f in fields):
        out.append("```mermaid")
        out.append("packet-beta")
        for f in fields:
            start = f["word"] * 16
            end = start + 15
            out.append("%d-%d: \"%s\"" % (start, end, f["name"]))
        out.append("```")
        out.append("")

    out.append("| Word | Byte | Field | What it is | Status | Evidence |")
    out.append("|---|---|---|---|---|---|")
    for f in fields:
        meaning = f.get("meaning") or f.get("rule") or ""
        out.append("| %s | %s | `%s` | %s | %s | %s |" % (
            f.get("word", ""), f.get("byte_offset", ""), f["name"],
            esc(meaning), badge(f.get("status")), esc(f.get("evidence"))))
    out.append("")

    for f in fields:
        for sup in f.get("supersedes", []):
            out.append("> **A superseded reading of `%s`.** %s"
                       % (f["name"], esc(sup.get("reading"))))
            out.append(">")
            if sup.get("why_it_survived"):
                out.append("> *Why it survived:* %s" % esc(sup["why_it_survived"]))
            if sup.get("cost"):
                out.append("> *What it cost:* %s" % esc(sup["cost"]))
            out.append("")


def render_bitfield(name, bf, out):
    out.append("### `%s`" % name)
    out.append("")
    if bf.get("location"):
        out.append("%s, %s bits." % (bf["location"], bf.get("width_bits", "?")))
        out.append("")
    if bf.get("rule"):
        out.append("**%s**" % esc(bf["rule"]))
        out.append("")

    bits = bf.get("bits", [])
    if bits:
        out.append("| Bit | Mask | Name | What it means | Status | Evidence |")
        out.append("|---|---|---|---|---|---|")
        for b in bits:
            names = b.get("names") or [b.get("name")]
            shown = " / ".join("`%s`" % n for n in names)
            if b.get("shared"):
                shown += " **(one bit, two names)**"
            out.append("| %s | `%s` | %s | %s | %s | %s |" % (
                b.get("bit", ""), b.get("mask", ""), shown,
                esc(b.get("meaning")), badge(b.get("status")), esc(b.get("evidence"))))
        out.append("")

    for b in bits:
        if b.get("trap"):
            out.append("> **Trap.** %s" % esc(b["trap"]))
            out.append("")

    combos = bf.get("combos", [])
    if combos:
        out.append("Named combinations:")
        out.append("")
        out.append("| Combination | Value | Bits | Status |")
        out.append("|---|---|---|---|")
        for c in combos:
            out.append("| `%s` | `%s` | %s | %s |" % (
                c["name"], c.get("value", ""),
                ", ".join("`%s`" % x for x in c.get("bits", [])),
                badge(c.get("status"))))
        out.append("")

    values = bf.get("values", [])
    if values:
        out.append("| Value | Name | Status | Evidence |")
        out.append("|---|---|---|---|")
        for v in values:
            out.append("| `%s` | `%s` | %s | %s |" % (
                v.get("value", ""), v.get("name", ""),
                badge(v.get("status")), esc(v.get("evidence"))))
        out.append("")


def render_value_table(title, block, out, note_key="note"):
    """Renders any {note, values:[{name,value,meaning,status,evidence}]} block."""
    out.append("## %s" % title)
    out.append("")
    if block.get(note_key):
        out.append("> %s" % esc(block[note_key]))
        out.append("")
    out.append("| Name | Value | What it does | Status | Evidence |")
    out.append("|---|---|---|---|---|")
    for v in block.get("values", []):
        name = "`%s`" % v["name"]
        if v.get("alias_of"):
            name += " *(same as `%s`)*" % v["alias_of"]
        out.append("| %s | `%s` | %s | %s | %s |" % (
            name, v.get("value", ""), esc(v.get("meaning")),
            badge(v.get("status")), esc(v.get("evidence"))))
    out.append("")


# ---------------------------------------------------------------------------
# flows
# ---------------------------------------------------------------------------

def collect_known_names(doc):
    """Every operation and message name this registry defines."""
    known = set()

    def add_values(block):
        for v in (block or {}).get("values", []):
            if v.get("name"):
                known.add(v["name"])

    add_values(doc.get("operations"))
    # The chat registry calls its vocabulary message_kinds - they are messages, not operations on
    # a file, and naming them "operations" to satisfy this list would have been the tail wagging.
    add_values(doc.get("message_kinds"))
    add_values(doc.get("services"))
    add_values(doc.get("errors"))
    add_values(doc.get("connection_types"))
    add_values(doc.get("control_services"))
    add_values(doc.get("status_codes"))
    for m in doc.get("message_types", []):
        if m.get("name"):
            known.add(m["name"])
    for name in doc.get("bitfields", {}):
        for v in doc["bitfields"][name].get("values", []):
            if v.get("name"):
                known.add(v["name"])
    return known


def check_flow_names(source, doc, problems, known):
    """Every op/msg a flow names must exist in SOME registry.

    Across all of them rather than just this one, because the protocols stack: a
    TAD connect legitimately names XSLET, which XROUT defines. Narrowing it to
    the file being checked rejected honest ladders on the first run.
    """
    for flow_name, flow in (doc.get("flows") or {}).items():
        for cited in walk_cited(flow.get("steps", [])):
            if cited not in known:
                problems.append(
                    "%s: flow '%s' names '%s', which the registry does not define"
                    % (source, flow_name, cited))


def walk_cited(steps):
    for step in steps:
        for key in ("op", "msg"):
            if step.get(key):
                yield step[key]
        for inner in ("steps",):
            if step.get(inner):
                for c in walk_cited(step[inner]):
                    yield c


def render_flow_steps(steps, out, depth=0):
    pad = "    " * depth
    for step in steps:
        if step.get("loop"):
            out.append("%s    loop %s" % (pad, step["loop"]))
            render_flow_steps(step.get("steps", []), out, depth + 1)
            out.append("%s    end" % pad)
            continue
        if step.get("alt"):
            out.append("%s    alt %s" % (pad, step["alt"]))
            render_flow_steps(step.get("steps", []), out, depth + 1)
            if step.get("else"):
                out.append("%s    else %s" % (pad, step["else"]))
                render_flow_steps(step.get("else_steps", []), out, depth + 1)
            out.append("%s    end" % pad)
            continue
        if step.get("raw"):
            out.append("%s    %s" % (pad, step["raw"]))
            continue

        label = step.get("op") or step.get("msg") or step.get("label") or ""
        arrow = "-->>" if step.get("reply") else "->>"
        out.append("%s    %s%s%s: %s" % (
            pad, step["from"], arrow, step["to"], label))
        if step.get("note"):
            out.append("%s    Note over %s,%s: %s" % (
                pad, step["from"], step["to"], step["note"]))


def render_flows(flows, out):
    if not flows:
        return
    out.append("## Flows")
    out.append("")
    out.append("Generated from the registry, so a ladder cannot name an operation "
               "that does not exist.")
    out.append("")
    for name, flow in flows.items():
        out.append("### %s" % flow.get("title", name))
        out.append("")
        if flow.get("summary"):
            out.append(flow["summary"])
            out.append("")
        if flow.get("proved"):
            out.append("> **Proved:** %s" % esc(flow["proved"]))
            out.append("")
        out.append("```mermaid")
        out.append("sequenceDiagram")
        out.append("    autonumber")
        for actor in flow.get("actors", []):
            out.append("    participant %s" % actor)
        render_flow_steps(flow.get("steps", []), out)
        out.append("```")
        out.append("")
        for gotcha in flow.get("gotchas", []):
            out.append("> **%s** %s" % (gotcha.get("name", "Note."), esc(gotcha.get("detail"))))
            out.append("")


# ---------------------------------------------------------------------------
# a whole page
# ---------------------------------------------------------------------------

def source_stamp(source):
    """A hash of the registry this page was generated from.

    Written into the page so STALENESS IS CHECKABLE WITHOUT THIS SCRIPT. A C# test
    hashes the JSON and compares, which means `dotnet test` catches a registry
    edited without regenerating - rather than it depending on somebody remembering
    to run --check, or on python being installed wherever the tests run.
    """
    raw = io.open(os.path.join(HERE, source), "rb").read()
    return hashlib.sha256(raw).hexdigest()


def render_page(source, doc):
    out = []
    meta = doc.get("meta", {})
    out.append("# %s" % meta.get("title", source))
    out.append("")
    out.append("> **Generated from [`%s`](%s) - do not edit this file.** "
               "Run `python generate.py` after changing the registry." % (source, source))
    out.append("")
    out.append("<!-- source-sha256: %s -->" % source_stamp(source))
    out.append("")
    if meta.get("purpose"):
        out.append(meta["purpose"])
        out.append("")
    if meta.get("layering"):
        out.append("**Where it sits:** %s" % meta["layering"])
        out.append("")

    out.append("| Status | Means |")
    out.append("|---|---|")
    for k, v in (doc.get("status_values") or {}).items():
        out.append("| %s | %s |" % (badge(k), esc(v)))
    out.append("")

    if doc.get("the_seat_law"):
        law = doc["the_seat_law"]
        out.append("## The seat law")
        out.append("")
        out.append("**%s**" % esc(law.get("summary")))
        out.append("")
        out.append("%s - %s" % (badge(law.get("status")), esc(law.get("evidence"))))
        out.append("")
        for c in law.get("consequences", []):
            out.append("- **%s** %s" % (esc(c.get("rule")), esc(c.get("why"))))
        out.append("")
        marker = law.get("marker", {})
        if marker:
            out.append("> **The only marker:** %s - %s"
                       % (esc(marker.get("field")), esc(marker.get("meaning"))))
            out.append(">")
            out.append("> %s" % esc(marker.get("evidence")))
            out.append("")

    if doc.get("structures"):
        out.append("## Message formats")
        out.append("")
        for name, struct in doc["structures"].items():
            render_structure(name, struct, out)

    if doc.get("qform"):
        q = doc["qform"]
        out.append("## The QFORM encoding")
        out.append("")
        out.append(esc(q.get("summary")))
        out.append("")
        out.append("| Rule | How | What it means | Status |")
        out.append("|---|---|---|---|")
        for r in q.get("tag_byte", {}).get("rules", []):
            how = r.get("extract") or r.get("test") or r.get("value") or ""
            out.append("| `%s` | `%s` | %s | %s |" % (
                r["name"], how, esc(r.get("meaning")), badge(r.get("status"))))
        out.append("")
        out.append("| Class | Name | What it holds | Status |")
        out.append("|---|---|---|---|")
        for c in q.get("classes", []):
            out.append("| %s | `%s` | %s | %s |" % (
                c["value"], c["name"], esc(c.get("meaning")), badge(c.get("status"))))
        out.append("")
        for t in q.get("traps", []):
            out.append("> **Trap: %s.** %s" % (esc(t.get("name")), esc(t.get("detail"))))
            out.append("")

    if doc.get("message_prefix"):
        render_structure("message_prefix", doc["message_prefix"], out)

    if doc.get("message_types"):
        out.append("## Message types")
        out.append("")
        out.append("| Name | Value | Status | Evidence |")
        out.append("|---|---|---|---|")
        for m in doc["message_types"]:
            out.append("| `%s` | `%s` | %s | %s |" % (
                m["name"], m.get("value", ""), badge(m.get("status")), esc(m.get("evidence"))))
        out.append("")

    if doc.get("bitfields"):
        out.append("## Bitfields")
        out.append("")
        for name, bf in doc["bitfields"].items():
            render_bitfield(name, bf, out)

    for key, title in [("control_services", "Control services"),
                       ("services", "Services"),
                       ("operations", "Operations"),
                       ("status_codes", "Status codes"),
                       ("errors", "Errors we act on"),
                       # TAD's driver-internal error numbers: they never cross the wire, so they are
                       # a block of their own rather than mixed in with the wire errors above.
                       ("error_codes", "Driver error codes"),
                       ("connection_types", "Connection types")]:
        if doc.get(key):
            render_value_table(title, doc[key], out)

    if doc.get("exchanges"):
        out.append("## Exchanges")
        out.append("")
        for name, ex in doc["exchanges"].items():
            out.append("### %s" % name)
            out.append("")
            if ex.get("summary"):
                out.append(esc(ex["summary"]))
                out.append("")
            for part in ("request", "accept", "reply"):
                if part not in ex:
                    continue
                out.append("**%s** (%s)" % (part, ex[part].get("direction", "")))
                out.append("")
                out.append("| Field | Value | What it is | Status | Evidence |")
                out.append("|---|---|---|---|---|")
                for f in ex[part].get("body_fields", []):
                    out.append("| `%s` | %s | %s | %s | %s |" % (
                        f["name"],
                        "`%s`" % f["value"] if f.get("value") else "",
                        esc(f.get("meaning")), badge(f.get("status")), esc(f.get("evidence"))))
                out.append("")

    if doc.get("sequencing"):
        out.append("## Sequencing rules")
        out.append("")
        for name, block in doc["sequencing"].items():
            out.append("### `%s`" % name)
            out.append("")
            out.append("**%s** %s" % (badge(block.get("status")), esc(block.get("rule"))))
            out.append("")
            out.append("*Evidence:* %s" % esc(block.get("evidence")))
            out.append("")
            for sub in ("retransmissions", "persistence"):
                if block.get(sub):
                    b = block[sub]
                    out.append("**%s.** %s %s" % (
                        sub, esc(b.get("rule") or b.get("note")), badge(b.get("status"))))
                    out.append("")
                    out.append("*Evidence:* %s" % esc(b.get("evidence")))
                    out.append("")

    if doc.get("data_transfer"):
        dt = doc["data_transfer"]
        out.append("## Moving the bytes")
        out.append("")
        out.append("Block %s bytes, %s blocks per read, a data message %s bytes."
                   % (dt.get("block_length"), dt.get("blocks_per_read"),
                      dt.get("data_message_length")))
        out.append("")
        out.append("> %s" % esc(dt.get("note")))
        out.append("")
        for r in dt.get("rules", []):
            out.append("- **%s** - %s" % (esc(r.get("name")), esc(r.get("detail"))))
        out.append("")

    render_flows(doc.get("flows"), out)

    if doc.get("open_questions"):
        out.append("## Still open")
        out.append("")
        out.append("| # | Question | Status | What would settle it |")
        out.append("|---|---|---|---|")
        for q in doc["open_questions"]:
            out.append("| %s | %s | %s | %s |" % (
                q.get("id", ""), esc(q.get("question")),
                badge(q.get("status")), esc(q.get("would_settle_it") or q.get("see") or "")))
        out.append("")

    return "\n".join(out).rstrip() + "\n"


def render_index(pages):
    out = []
    out.append("# The XMSG protocols")
    out.append("")
    out.append("> **Generated by `generate.py` - do not edit.**")
    out.append("")
    out.append("<!-- source-sha256: %s -->" % source_stamp("catalog.json"))
    out.append("")
    out.append("Everything here rides on XMSG. A service is a task holding a **port**; "
               "a port is addressed by a **magic number**; and XROUT is the phone book "
               "that turns a registered *name* into one.")
    out.append("")
    out.append("```mermaid")
    out.append("flowchart TD")
    out.append("    TAD[TAD - terminal sessions] --> XMSG")
    out.append("    FA[FA - remote files] --> XMSG")
    out.append("    CHAT[chat, and anything we build] --> XMSG")
    out.append("    XMSG[XMSG messages, addressed by magic number] --> HDR")
    out.append("    XROUT[XROUT - names, routes, seats] -.->|turns a name into an address| XMSG")
    out.append("    HDR[SINTRAN datagram: 7-word header + body] --> LINK")
    out.append("    LINK[ND link over Ethernet, or LAPB over HDLC]")
    out.append("```")
    out.append("")
    out.append("| Protocol | What it is | Registry |")
    out.append("|---|---|---|")
    for source, md, title, purpose in pages:
        out.append("| [%s](%s) | %s | [`%s`](%s) |"
                   % (title, md, esc(purpose), source, source))
    out.append("")
    out.append("Every claim carries a status and an evidence pointer. "
               "**MEASURED** means somebody watched it happen and the evidence names where; "
               "*inferred* follows from something measured; **UNKNOWN** means we copy it and "
               "never compute it.")
    out.append("")
    return "\n".join(out)


def main():
    check = "--check" in sys.argv
    problems = []
    stale = []
    pages = []

    # Every name any registry defines. Collected first so a flow in one file may
    # cite a service another file owns - which is what a stacked protocol does.
    known_everywhere = set()
    for source, _md, _entry in catalogued():
        path = os.path.join(HERE, source)
        if os.path.exists(path):
            known_everywhere |= collect_known_names(
                json.load(io.open(path, encoding="utf-8")))

    for source, md, entry in catalogued():
        path = os.path.join(HERE, source)
        if not os.path.exists(path):
            problems.append("missing registry: " + source)
            continue

        doc = json.load(io.open(path, encoding="utf-8"))
        check_flow_names(source, doc, problems, known_everywhere)

        text = render_page(source, doc)
        target = os.path.join(HERE, md)
        existing = io.open(target, encoding="utf-8").read() if os.path.exists(target) else None
        if existing != text:
            if check:
                stale.append(md)
            else:
                io.open(target, "w", encoding="utf-8", newline="").write(text)

        # Title and one-liner come from the CATALOG, so the index reads consistently
        # even if a registry's own meta drifts.
        pages.append((source, md, entry.get("title", source), entry.get("one_line", "")))

    index = render_index(pages)
    target = os.path.join(HERE, "PROTOCOLS.md")
    existing = io.open(target, encoding="utf-8").read() if os.path.exists(target) else None
    if existing != index:
        if check:
            stale.append("PROTOCOLS.md")
        else:
            io.open(target, "w", encoding="utf-8", newline="").write(index)

    for p in problems:
        print("PROBLEM: " + p)

    if check and stale:
        print("STALE (regenerate with `python generate.py`): " + ", ".join(stale))

    if problems or (check and stale):
        return 1

    print("generated %d protocol pages and the index" % len(pages))
    return 0


if __name__ == "__main__":
    sys.exit(main())
