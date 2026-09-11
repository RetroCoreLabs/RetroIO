//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*ACCESS &lt;object&gt; [&lt;perms&gt;]</c> — sets a file or directory's access permissions.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors PiEconetBridge <c>fs_access</c> (fs.c:7270-7310). The permission string is
/// <c>&lt;owner-perms&gt;[/&lt;other-perms&gt;]</c>: before the slash the letters <c>W R H/P L</c> set the
/// owner bits plus hidden/locked; after the slash only <c>W</c> and <c>R</c> are legal and set the
/// public bits. An empty string clears every bit (<c>perm = 0</c>). An unrecognised letter is
/// <c>0xCF "Bad attribute"</c>; a malformed argument is <c>0xFF</c>; an object that will not resolve is
/// <c>0xD6 "Not found"</c>.
/// </para>
/// <para><b>Ownership divergence (deliberate).</b></para>
/// The reference gates this on "caller owns the object, or owns the parent with write, or is system".
/// RetroFS has no per-object MDFS owner model (see the <c>isOwner = true</c> note in
/// <c>HandleGetObjectInfo</c>), so the gate collapses to "a logged-in session may set access on any
/// object it can resolve" — the dispatcher already enforces the login via <see cref="RequiresLogin"/>.
/// The new access byte is persisted through <see cref="AcornInfStore"/> (the <c>,inf</c> sidecar is the
/// Acorn attribute system of record) and round-trips back to <c>*INFO</c>/<c>*EX</c>/GetObjectInfo.
/// </remarks>
public sealed class AccessCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "ACCESS";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 3;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<object> [<access-string>]"; the access string is optional (empty => clear all).
        string[] parts = call.Argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return FileServerReply.Error(FileServerErrors.Unsupported, "Bad parms");

        string fsp = parts[0];
        string accessSpec = parts.Length > 1 ? parts[1] : string.Empty;

        // Parse before resolving the object, exactly as fs.c: a bad permission letter is reported
        // even for an object that does not exist.
        byte parseError = TryParseAccess(accessSpec, out byte newAccess);
        if (parseError != 0)
        {
            string message = parseError == FileServerErrors.BadAttribute ? "Bad attribute" : "Bad parms";
            return FileServerReply.Error(parseError, message);
        }

        // No per-object owner model: resolving the object IS the access check (see remarks).
        if (!call.Context.Paths.TryResolve(call.Session!, fsp, out string unixPath, out _, out _))
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");

        // Read-modify-write the ,inf sidecar: keep load/exec/length, replace only the access byte.
        AcornInfStore.TryReadAttributes(call.Context.FileSystem, unixPath, out uint load, out uint exec, out int length, out _);
        AcornInfStore.WriteAttributes(call.Context.FileSystem, unixPath, load, exec, length, newAccess);

        // Success is a plain completion (return code 0) — the same payload the stub always returned.
        return FileServerReply.Success();
    }

    /// <summary>
    /// Parses an Acorn permission string into the access byte.
    /// </summary>
    /// <param name="spec">
    /// The permission text, e.g. <c>"WR/R"</c>, <c>"L"</c>, <c>"H"</c>, or empty.
    /// </param>
    /// <param name="access">
    /// Receives the parsed access byte; 0 when <paramref name="spec"/> is empty.
    /// </param>
    /// <returns>
    /// 0 on success, <see cref="FileServerErrors.BadAttribute"/> (0xCF) for an unrecognised letter, or
    /// <see cref="FileServerErrors.Unsupported"/> (0xFF) for a malformed string (more than one slash).
    /// </returns>
    private static byte TryParseAccess(string spec, out byte access)
    {
        access = 0;

        string trimmed = spec.Trim();
        if (trimmed.Length == 0)
            return 0; // empty string clears every bit — fs.c treats this as perm 0.

        // Split owner side from public side on the single legal '/'. More than one slash is malformed.
        string[] sides = trimmed.Split('/');
        if (sides.Length > 2)
            return FileServerErrors.Unsupported;

        // Owner side: W R H/P L are all legal.
        string owner = sides[0];
        for (int i = 0; i < owner.Length; i++)
        {
            switch (char.ToUpperInvariant(owner[i]))
            {
                case 'R': access |= (byte)EconetAccess.OwnerRead; break;
                case 'W': access |= (byte)EconetAccess.OwnerWrite; break;
                case 'L': access |= (byte)EconetAccess.Locked; break;
                case 'H':
                case 'P': access |= (byte)EconetAccess.Hidden; break;
                default: return FileServerErrors.BadAttribute;
            }
        }

        // Public side (after the slash): ONLY W and R are legal here.
        if (sides.Length == 2)
        {
            string other = sides[1];
            for (int i = 0; i < other.Length; i++)
            {
                switch (char.ToUpperInvariant(other[i]))
                {
                    case 'R': access |= (byte)EconetAccess.PublicRead; break;
                    case 'W': access |= (byte)EconetAccess.PublicWrite; break;
                    default: return FileServerErrors.BadAttribute;
                }
            }
        }

        return 0;
    }
}
