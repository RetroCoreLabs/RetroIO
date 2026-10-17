//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Names the SINTRAN III error numbers a file server sends back in a refusal.
    /// </summary>
    /// <remarks>
    /// <para><b>The numbers on the wire are DECIMAL; the manual prints them in OCTAL</b></para>
    /// This catches people out, so it is worth stating plainly. The SINTRAN III Users Guide
    /// (ND-60.050.06) lists error 056 meaning "No such file name" - and the wire carries
    /// <c>A2 002E</c>, which is 46 decimal, which IS 056 octal. Both readings below were confirmed
    /// against a live D100 on 2026-08-18:
    ///  - a read of a file that does not exist answered 46 = 056 octal, "No such file name".
    ///  - a push to <c>(NOUSR)</c> answered 39 = 047 octal, "No such user name in main directory",
    ///    which is exactly what was wrong with the request.
    /// <para><b>Only measured numbers are named</b></para>
    /// Everything else comes back as a bare number. A wrong name sends the reader off to fix
    /// something that is not broken, which is worse than no name at all - the same trap that made
    /// three invented <c>FaServerStatus</c> values point at the wrong SINTRAN errors until 2026-08-07.
    /// Add a name here when a capture proves one, not before.
    /// </remarks>
    public static class FaSintranError
    {
        /// <summary>
        /// Appends the error's meaning to a message, when the number is one we have measured.
        /// </summary>
        /// <param name="status">
        /// The SINTRAN III error number, as carried on the wire in decimal.
        /// </param>
        /// <returns>
        /// A parenthesised description, or an empty string when the number has no confirmed meaning.
        /// </returns>
        public static string Describe(ushort status)
        {
            switch (status)
            {
                // 047 octal. VERIFIED: a push to "(NOUSR)A:T" against D100.
                case 39:
                    return " (No such user name in main directory)";

                // 056 octal. VERIFIED: a read of a file that does not exist, and the same number
                // FaServerStatus has recorded from the 2026-08-04 captures.
                case 46:
                    return " (No such file name)";

                // 060 octal. From the 2026-07-29 access capture.
                case 48:
                    return " (Wrong password)";

                // 076 octal. VERIFIED 2026-08-18: the sync daemon issued a CREATE for a file that
                // was already on D100 - it creates rather than overwrites when its ledger has never
                // carried the file - and this came back. The name is the whole explanation: a
                // create refuses an existing file, it is not about access. A read-only file was in
                // the same test, which is why the access was suspected first; re-pushing an
                // existing file with NORMAL access as an overwrite succeeded, which cleared it.
                case 62:
                    return " (File already exists)";

                // Ends a directory walk rather than reporting a fault.
                case 197:
                    return " (End of directory)";

                default:
                    return " (not carved yet - do not guess at it)";
            }
        }
    }
}
