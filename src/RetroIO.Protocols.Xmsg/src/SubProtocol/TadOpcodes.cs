//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.SubProtocol
{
    /// <summary>
    /// Mnemonic names for TAD (Terminal Access and Directory, protocol <c>0xDD</c>)
    /// message opcodes.
    /// </summary>
    /// <remarks>
    /// The table is taken verbatim from the Wireshark dissector
    /// (<c>hdlc_tcp.lua</c>, <c>vs_tad_type</c>), itself verified against the SINTRAN III
    /// K03/L07/M06 symbol tables and TAD-Message-Formats.md. Opcodes not present in the
    /// table are formatted as <c>0xNN</c>.
    /// </remarks>
    public static class TadOpcodes
    {
        private static readonly Dictionary<byte, string> Names = BuildTable();

        /// <summary>
        /// Returns the mnemonic name for a TAD opcode.
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode byte (message type at chain offset 0).
        /// </param>
        /// <returns>
        /// The mnemonic (for example <c>BDAT</c>) when known, otherwise <c>0xNN</c>.
        /// </returns>
        public static string Name(byte opcode)
        {
            if (Names.TryGetValue(opcode, out string? name))
            {
                return name;
            }

            return "0x" + opcode.ToString("X2");
        }

        /// <summary>
        /// Builds the opcode-to-name table.
        /// </summary>
        /// <returns>
        /// A dictionary mapping each known TAD opcode to its mnemonic.
        /// </returns>
        private static Dictionary<byte, string> BuildTable()
        {
            Dictionary<byte, string> table = new Dictionary<byte, string>();
            table.Add(0x01, "BDAT");
            table.Add(0x02, "RFI");
            table.Add(0x03, "ECKM");
            table.Add(0x04, "BMMX");
            table.Add(0x08, "ESCA");
            table.Add(0x09, "DCON");
            table.Add(0x0C, "TMOD");
            table.Add(0x0D, "TTYP");
            table.Add(0x0E, "CESC");
            table.Add(0x0F, "DESC");
            table.Add(0x13, "SYCN");
            table.Add(0x14, "USCN");
            table.Add(0x16, "RESE");
            table.Add(0x17, "RECO");
            table.Add(0x18, "DUMM");
            table.Add(0x1F, "OPSV");

            // 0x20 ESRS and 0x29 EDRS were BOTH missing from the dissector table this was copied from,
            // so a captured escape response printed as a bare "0x20". Added 2026-08-18: 7ESRS=000040
            // and 7EDRS=000051 octal, present and identical in all four symbol tables under
            // SINTRAN/NPL-SOURCE/SYMBOLS (J, K03, L07, M06). EDRS is the answer sent instead of ESRS
            // when the escape function is disabled - see TadOp.Edrs.
            table.Add(0x20, "ESRS");
            table.Add(0x21, "CERS");
            table.Add(0x22, "ISRQ");
            table.Add(0x23, "ISRS");
            table.Add(0x24, "NOWT");
            table.Add(0x25, "TNOW");
            table.Add(0x26, "NWRE");
            table.Add(0x27, "RLOC");
            table.Add(0x29, "EDRS");
            table.Add(0x2A, "TREP");
            table.Add(0x2B, "UMOD");
            table.Add(0x2C, "78MOD");
            table.Add(0xFA, "CPCO");
            table.Add(0xFB, "ERRS");
            table.Add(0xFE, "REJE");
            return table;
        }
    }
}
