//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The 7ECKM echo-strategy value (spec TAD-Message-Formats.md 5.2 / 22.12). Echo is
    /// host-controlled but client-executed: the host sends this value and the asker applies it to its
    /// own local echo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the wire an ECKM message is <c>00 03 01 value</c>. The leading <c>0x00</c> is NOT a prefix
    /// belonging to the opcode - it is the odd-start alignment pad the ND builder writes; see
    /// <see cref="TadMessageBuilder"/> for the mechanism.
    /// </para>
    /// <para>
    /// Only these three values appear in the captured logins. The custom table that strategy 7 selects
    /// is 16 bytes, not 20 - corrected 2026-08-18 against the version J NPL source, where the
    /// earlier "20-byte" reading turns out to be an OCTAL literal read as decimal.
    /// <c>CBRECTA</c> in <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/20-COS-TAD-POF-CODE.NPL</c> copies exactly
    /// 8 words (<c>FOR X:=0 TO 7</c>) and then advances the byte pointer by <c>20</c> octal = 16
    /// decimal. The message sizes agree: ECKM strategy 7 reserves 0o21 = 17 bytes (1 + 16) and BMMX
    /// strategy 7 reserves 0o23 = 19 bytes (1 + 2 + 16).
    /// </para>
    /// <para>
    /// 16 bytes is 128 bits, one per ASCII character, so the table is very likely a per-character
    /// bitmap - INFERRED from the size, not stated in the source. We have never seen strategy 7 on
    /// these links.
    /// </para>
    /// </remarks>
    public enum EchoStrategy : byte
    {
        /// <summary>
        /// Echo off / line-discipline teardown (<c>0x00</c>) - sent in the logout ladder alongside
        /// <c>BMMX 000000</c> and <c>CESC 00</c>.
        /// </summary>
        Teardown = 0x00,

        /// <summary>
        /// Echo ON (<c>0x01</c>) - the asker echoes keystrokes locally. Sent in the banner burst and to
        /// restore echo in the password verdict (both the OK and the wrong-password reset).
        /// </summary>
        LocalEcho = 0x01,

        /// <summary>
        /// Echo OFF (<c>0xFF</c>) - the asker displays nothing while typing. Sent right before the
        /// "PASSWORD- " RFI so the password is entered blind.
        /// </summary>
        NoEcho = 0xFF,
    }
}
