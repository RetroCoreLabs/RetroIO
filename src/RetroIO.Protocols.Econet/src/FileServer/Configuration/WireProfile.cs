//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.Json.Serialization;

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Selects which generation of the Acorn file-server wire format a reply is built for.
/// </summary>
/// <remarks>
/// <para>
/// RetroFS emits the <b>later-FS</b> format by default — the shapes ANFS / DNFS 3.60 and PiFS expect,
/// and what every live BeebEm capture and golden test locks. A handful of frames deliberately diverge
/// from the <b>original 1983 Level-2/3</b> format, so a client hard-wired to the 1983 frames would
/// misread them. This enum lets an operator pin such a client to the classic shapes without changing
/// the default for everyone else. See <c>docs/econet/notes/wire-format-verification.md</c> for the
/// authoritative, source-cross-checked divergence list.
/// </para>
/// <para>
/// Known divergences gated by this profile (server replies only):
/// <list type="bullet">
///   <item><description><c>*DIR</c> not-found error code — later FS <c>&amp;D6</c> vs 1983 <c>&amp;FE</c>. (wired)</description></item>
///   <item><description>Read logged-on users (0x0F) frame shape — later variable-length vs the fixed 13-byte 1983 frame. (wired — 1983 byte-offset unverified, see the operation)</description></item>
///   <item><description>Free space (0x1A) — later free+total-extent (8 bytes) vs the 3-byte 1983 free-block count. (wired)</description></item>
/// </list>
/// </para>
/// </remarks>
// Serialize/deserialize by NAME in econet-config.json ("LaterFS" / "Classic1983"), not the raw ordinal.
// The loader uses System.Text.Json with default options, whose default enum handling is numeric-only —
// without this a human-written "WireProfile": "Classic1983" would fail to bind. JsonStringEnumConverter
// still accepts numbers too, so this is backward compatible.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WireProfile
{
    /// <summary>
    /// The later file-server format expected by modern ANFS / DNFS 3.60 clients (the RetroFS default,
    /// byte-for-byte matching every golden capture). Value 0 so an unconfigured resolver is LaterFS.
    /// </summary>
    LaterFS = 0,

    /// <summary>
    /// The original 1983 Level-2/3 format (Acorn <i>Econet Advanced User Guide</i>, 412,019). Use only
    /// to serve a client hard-wired to the 1983 frames; opt-in per machine, never the global default.
    /// </summary>
    Classic1983 = 1,
}
