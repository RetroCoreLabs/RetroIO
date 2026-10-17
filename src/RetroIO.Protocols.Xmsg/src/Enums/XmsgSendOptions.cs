//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The XMSG sub-header "role" byte, decoded as the high byte (bits 8-15) of the MON 200B send-option
    /// word. Each bit selects a send option.
    /// </summary>
    /// <remarks>
    /// This is the SAME bit set as <see cref="XmsgOption"/>, shifted down by 8: XmsgOption is the
    /// 16-bit MON 200B T-register word (source of truth, generated from the official constants) and
    /// this enum is its high byte as it appears on the wire. Convert with
    /// <see cref="XmsgOptionConversion"/>; the lockstep is enforced by a unit test, so never
    /// re-number one enum without the other.
    /// The option-bit names and values are VERIFIED from XMSG-VALUES-M.SYMB (the XF* symbols); the
    /// placement of that byte into the wire role octet is INFERRED, but it decodes every observed role
    /// value (0xE4/0x84/0x94/0x40/0x00/0x60/0xC4/0x54) with zero exceptions. Note in particular that
    /// <see cref="RoutedLetter"/> (bit <c>0x04</c>) is XFROU "routed", NOT an "asker" marker - the old
    /// <c>(role and 0x0F) == 0x04</c> asker test was wrong, because the host-originated 0xFD notification
    /// rides role <c>0x54</c> which also has that bit set. See XMSG-PROTOCOL.md section 18.4.
    /// <para><b>Bit 5 means two different things - CORRECTED 2026-08-06</b></para>
    /// It is XFHIP (high priority) only when XFROU is CLEAR, and XFRRO (non-local XROUT) when XFROU
    /// is SET - see <see cref="HighPriority"/>. The connect-letter decode below used to read
    /// <c>0xE4</c> as ...|HighPriority|RoutedLetter, which cannot be right: that value HAS the
    /// routed bit set, so bit 5 is XFRRO. And that reading makes sense of the frame - a connect
    /// letter naming a server on another node IS a request to a remote XROUT.
    /// <para>
    /// Observed compositions, with bit 5 read correctly: connect letter <c>0xE4</c> =
    /// WaitForTransfer|WakeOnStatus|RemoteXrout|RoutedLetter; asker data <c>0x84</c> =
    /// WaitForTransfer|RoutedLetter; asker control <c>0x94</c> = WaitForTransfer|Bounce|RoutedLetter;
    /// host letter <c>0x40</c> = WakeOnStatus; host terminal data <c>0x00</c> = None; XSGSY reply
    /// <c>0x60</c> = WakeOnStatus|HighPriority (routed bit clear, so priority); XSGSY request
    /// <c>0xC4</c> = WaitForTransfer|WakeOnStatus|RoutedLetter; 0xFD notify <c>0x54</c> =
    /// WakeOnStatus|Bounce|RoutedLetter.
    /// </para>
    /// </remarks>
    [Flags]
    public enum XmsgSendOptions : byte
    {
        /// <summary>
        /// No options set (<c>0x00</c>) - host terminal-data frames.
        /// </summary>
        None = 0,

        /// <summary>
        /// XFTCM send task current message (bit 0, <c>0x01</c>) - never observed set in the corpus.
        /// </summary>
        /// <remarks>
        /// The meaning was carried as UNKNOWN until 2026-08-06, when it turned out ND names it in a
        /// file that has been in this repository all along:
        /// <code>
        /// XMSG-VALUES-M.SYMB:92   SYMBOL XFTCM=8   % In XFSND/SFM: Send task current message
        /// </code>
        /// The symbol value is a BIT NUMBER in the MON 200B T register, and the role octet is that
        /// register's high byte - so T bit 8 is role bit 0. The same offset checks out for every
        /// other member here: XFROU=10 to role bit 2, XFHIP=13 to role bit 5.
        /// </remarks>
        Tcm = 1 << 0,

        /// <summary>
        /// XFSEC secure (bit 1, <c>0x02</c>) - never observed set in the corpus.
        /// </summary>
        Secure = 1 << 1,

        /// <summary>
        /// XFROU routed letter (bit 2, <c>0x04</c>) - routed via XROUT to a remote port.
        /// </summary>
        RoutedLetter = 1 << 2,

        /// <summary>
        /// XFFWD forward (bit 3, <c>0x08</c>) - never observed set in the corpus.
        /// </summary>
        Forward = 1 << 3,

        /// <summary>
        /// XFBNC bounce / return-on-failure (bit 4, <c>0x10</c>).
        /// </summary>
        Bounce = 1 << 4,

        /// <summary>
        /// XFHIP high priority (bit 5, <c>0x20</c>) - but ONLY when
        /// <see cref="RoutedLetter"/> is clear. See the remarks.
        /// </summary>
        /// <remarks>
        /// <para><b>This bit carries two different meanings, decided by XFROU</b></para>
        /// ND's own symbols say so outright, and both entries use the same bit number:
        /// <code>
        /// XMSG-VALUES-M.SYMB:77   XFHIP=13  % In XFSND/RTN/WRT/SFM: If not XFROU then high-priority message
        /// XMSG-VALUES-M.SYMB:80   XFRRO=13  % In XFSND: If XFROU then non-local XROUT (sysno in A-reg).
        /// </code>
        /// So with <see cref="RoutedLetter"/> (XFROU) SET, bit 5 is not priority at all - it
        /// is <see cref="RemoteXrout"/>, "this XROUT request is for a system other than the local
        /// one". Use <see cref="XmsgSendOptionsExtensions.IsHighPriority"/> and
        /// <see cref="XmsgSendOptionsExtensions.IsRemoteXrout"/> rather than testing the bit
        /// directly.
        /// </remarks>
        HighPriority = 1 << 5,

        /// <summary>
        /// XFRRO non-local XROUT (bit 5, <c>0x20</c>) - the same bit as
        /// <see cref="HighPriority"/>, and what it means when <see cref="RoutedLetter"/> is set.
        /// </summary>
        /// <remarks>
        /// Declared as its own name so the overload is visible in code rather than buried in a
        /// comment. It is deliberately the SAME value - this is one wire bit, not two.
        /// </remarks>
        RemoteXrout = 1 << 5,

        /// <summary>
        /// XFWAK wake-on-status (bit 6, <c>0x40</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The ND documentation numbers this bit 14, and both are right</b></para>
        /// <para>
        /// <c>XMSG-PL-VALUES-M.INCL</c> declares <c>XFWAK=14</c> and <c>XFWTF=15</c>, and the
        /// COSMOS guide's PLANC samples build their flags word as <c>2**XFWTF</c>. Those are bit
        /// positions in the 16-bit options WORD a MON 200B caller passes.
        /// </para>
        /// <para>
        /// This enum is the ROLE BYTE on the wire, which is the HIGH BYTE of that word - so word
        /// bit 14 is byte bit 6, and word bit 15 is byte bit 7. Same wire bits, two widths.
        /// </para>
        /// <para>
        /// DO NOT "correct" one to match the other. They disagree only in the way a byte offset
        /// disagrees with a word offset, and making them numerically equal would move every option
        /// eight bits and break frames that are verified against real machines.
        /// </para>
        /// </remarks>
        WakeOnStatus = 1 << 6,

        /// <summary>
        /// XFWTF wait-for-transfer (bit 7, <c>0x80</c>).
        /// </summary>
        WaitForTransfer = 1 << 7,
    }

    /// <summary>
    /// Reads the two meanings of role bit 5 apart.
    /// </summary>
    /// <remarks>
    /// Bit 5 is XFHIP or XFRRO depending on XFROU - see
    /// <see cref="XmsgSendOptions.HighPriority"/>. Testing the bit directly gets it wrong half the
    /// time, so these two methods exist to make the right answer the easy one.
    /// </remarks>
    public static class XmsgSendOptionsExtensions
    {
        /// <summary>
        /// Reports whether the role octet asks for a high-priority message.
        /// </summary>
        /// <param name="options">
        /// The role octet.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when bit 5 is set AND the routed bit is clear, which is the only
        /// case in which bit 5 means priority.
        /// </returns>
        public static bool IsHighPriority(this XmsgSendOptions options)
        {
            return (options & XmsgSendOptions.HighPriority) != 0
                && (options & XmsgSendOptions.RoutedLetter) == 0;
        }

        /// <summary>
        /// Reports whether the role octet asks XROUT to reach a system other than the local one.
        /// </summary>
        /// <param name="options">
        /// The role octet.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when bit 5 is set AND the routed bit is set, which is the only
        /// case in which bit 5 means a non-local XROUT request.
        /// </returns>
        /// <remarks>
        /// ND's note adds that the system number then rides in the A register
        /// (<c>XMSG-VALUES-M.SYMB:80</c>). That is the MON 200B calling convention, not something
        /// carried in the frame, so nothing here reads it.
        /// </remarks>
        public static bool IsRemoteXrout(this XmsgSendOptions options)
        {
            return (options & XmsgSendOptions.RemoteXrout) != 0
                && (options & XmsgSendOptions.RoutedLetter) != 0;
        }
    }
}
