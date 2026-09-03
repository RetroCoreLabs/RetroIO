//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Commands;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Dispatch;

/// <summary>
/// The composition root for OSCLI commands: builds the ordered registry.
/// </summary>
/// <remarks>
/// <para><b>The list is ordered, and the order is behaviour</b></para>
/// Resolution is first-match-wins, so a command registered earlier beats a later one whenever both
/// accept the same typed text. Add new commands at the position matching the server's historical
/// try-order, not merely at the end.
/// <para><b>Migration in progress</b></para>
/// Commands are being moved out of the
/// <see cref="RetroIO.Protocols.Econet.FileServer.Handlers.OsCliCommandHandler"/> if/else chain one
/// at a time, strictly top-down in that chain's original order. That discipline is what makes the
/// half-migrated state safe: the dispatcher consults this registry first and the remaining chain
/// second, so a registry holding a prefix of the original order, followed by the chain holding the
/// rest in order, reconstructs the original order exactly at every intermediate step. Migrating out
/// of order would break that invariant and could silently change which command a text selects.
/// <para><b>Minimum abbreviations are not free choices</b></para>
/// They decide which command an abbreviation picks, and must match the established values:
///  - <c>I AM</c> 2, <c>IAM</c> 3, <c>LOGIN</c> 4, <c>LOGON</c> 5.
///  - <c>CAT</c> 2, <c>.</c> 1.
///  - <c>LOAD</c> 2, <c>SAVE</c> 2, <c>BYE</c> 3.
///  - <c>ACCESS</c> 3, <c>INFO</c> 1, <c>CDIR</c> 2, <c>DELETE</c> 3, <c>RENAME</c> 3.
///  - <c>SDISC</c> 2, <c>COPY</c> 2, <c>LIB</c> 3, <c>DIR</c> 3, <c>DIR^</c> 4.
/// INFO being 1 is why <c>*I.</c> selects INFO rather than login. See
/// <see cref="OsCliCommandMatcher"/> for how that resolves without special-casing.
/// </remarks>
public static class OsCliCommandTable
{
    /// <summary>
    /// Builds the registry in dispatch order.
    /// </summary>
    /// <param name="context">
    /// Server context handed to every command that needs it.
    /// </param>
    /// <returns>
    /// A registry populated in the server's historical command try-order.
    /// </returns>
    public static OsCliCommandRegistry Build(FileServerContext context)
    {
        OsCliCommandRegistry registry = new OsCliCommandRegistry();

        // --- Order below mirrors the original dispatch chain, top-down. ---

        // Login — the only commands that run without a session. Tried first, as the chain did.
        // One registration per spelling, each with its own minimum abbreviation.
        registry.Register(new LoginCommand("I AM", 2));
        registry.Register(new LoginCommand("IAM", 3));
        registry.Register(new LoginCommand("LOGIN", 4));
        registry.Register(new LoginCommand("LOGON", 5));

        // Logout. Full name only (minimum == length), exactly as the chain accepted it.
        registry.Register(new ByeCommand(context));

        // Catalogue, and its "*." synonym. "CAT" first, matching the historical try-order.
        registry.Register(new CatCommand("CAT", 2));
        registry.Register(new CatCommand(".", 1));

        // File transfer. LOAD/SAVE resolve the target then return fetch/push metadata.
        registry.Register(new LoadCommand());
        registry.Register(new SaveCommand());

        // Make directory. Previously missing from OSCLI though the CreateDir operation existed.
        registry.Register(new CdirCommand());

        // Redirects: the reply carries a file-server return code so the ROM re-issues the request.
        registry.Register(new OsCliRedirectCommand("DELETE", 3, FileServerOpcodes.Delete));
        registry.Register(new OsCliRedirectCommand("INFO", 1, FileServerOpcodes.GetObjectInfo));
        registry.Register(new OsCliRedirectCommand("EX", 2, FileServerOpcodes.Examine));
        registry.Register(new OsCliRedirectCommand("CREATE", 6, FileServerOpcodes.CreateFile));
        registry.Register(new OsCliRedirectCommand("BUILD", 5, FileServerOpcodes.CreateFile));

        // Access permissions (currently a success-only stub — see AccessCommand).
        registry.Register(new AccessCommand());

        // Change own password (previously missing). Its argument is redacted from all logs.
        registry.Register(new PassCommand());

        // Rename / move.
        registry.Register(new RenameCommand());

        // Server version list (RetroFS extra).
        registry.Register(new FsListCommand(context));

        // Directory and library selection.
        registry.Register(new LibCommand());
        registry.Register(new DirCommand("DIR", 3, forcedArgument: null));
        registry.Register(new DirCommand("DIR^", 4, forcedArgument: "^"));

        // Disc selection.
        registry.Register(new SdiscCommand());

        // Server-side single-file copy (previously missing).
        registry.Register(new CopyCommand());

        // Persist a user's home/library preference (takes effect at next login). Both need >=4 chars
        // ("SETL"/"SETH"), so they neither collide with each other nor with any earlier command's
        // abbreviation — order here is not behaviour. SETLIB is self-service (System only to set another
        // user); SETHOME is System-only (RequiresSystem, gated centrally).
        registry.Register(new SetLibCommand());
        registry.Register(new SetHomeCommand());

        // SYST resets another user's password (no old-password check). Min 4 ("SETP") — distinct from
        // SETLIB ("SETL") / SETHOME ("SETH"). Its password argument is redacted by EconetLogRedaction.
        registry.Register(new SetPassCommand());

        // SYST force-log-off, by station (net.stn / bare stn) or username. Two spellings: FLOG (min 3),
        // LOGOFF (min 5). System-gated centrally by the dispatcher (RequiresSystem).
        registry.Register(new FlogCommand("FLOG", 3));
        registry.Register(new FlogCommand("LOGOFF", 5));

        // SYST set a user's visible-disc bitmap (persist-only, effect at next login). Both spellings min 5.
        registry.Register(new SetDiscMaskCommand("DISCMASK"));
        registry.Register(new SetDiscMaskCommand("DISKMASK"));

        // SYST set a user's privilege (System/Normal). Min 4. Locked/no-pw-change/chroot/delete deferred.
        registry.Register(new PrivCommand());

        // Login ("I AM") stays ahead of the registry in OsCliCommandHandler for now because it is
        // the only command that runs without a session; it moves once RequiresLogin is wired through.

        return registry;
    }
}
