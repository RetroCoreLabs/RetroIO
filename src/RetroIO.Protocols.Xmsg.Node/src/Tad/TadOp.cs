//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The TAD opcode byte, identifying what a TAD message carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VERIFIED: opcode values are taken from <c>TAD/TAD-Message-Formats.md</c> section 2,
    /// which verifies them against the SINTRAN III K03/L07/M06 symbol tables. The mnemonic
    /// naming (for human display) is provided separately by
    /// <see cref="SubProtocol.TadOpcodes"/>.
    /// </para>
    /// <para>
    /// The values are sparse and not a contiguous range, so a byte off the wire that matches no
    /// member here is an opcode we have not decoded rather than a malformed message. Cast and
    /// compare, but never assume the set is complete.
    /// </para>
    /// </remarks>
    public enum TadOp : byte
    {
        /// <summary>
        /// BDAT - terminal data block (<c>0x01</c>).
        /// </summary>
        Bdat = 0x01,

        /// <summary>
        /// RFI - ready for input / flow-control credit (<c>0x02</c>).
        /// </summary>
        Rfi = 0x02,

        /// <summary>
        /// ECKM - echo strategy (<c>0x03</c>). One strategy byte; strategy 7 adds a 16-byte table.
        /// </summary>
        /// <remarks>
        /// <c>BDECHO</c> in <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/20-COS-TAD-POF-CODE.NPL</c> reserves an
        /// I-field of 0o1 bytes normally and 0o21 (17 decimal) for strategy 7, which is 1 strategy byte
        /// plus the 16-byte table <c>CBRECTA</c> copies as 8 whole words. See
        /// <see cref="EchoStrategy"/> for the size correction this settled.
        /// </remarks>
        Eckm = 0x03,

        /// <summary>
        /// BMMX - BREAK parameters (<c>0x04</c>): a break-strategy byte, a 16-bit BRKMAX word, and for
        /// strategy 7 a 16-byte break table.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It configures WHICH characters break the input and how many characters may accumulate
        /// before a break - it is NOT "the largest block the far end will accept", which is what the
        /// protocol registry used to say and what the J source refutes.
        /// </para>
        /// <para>
        /// <c>BDBREA</c> in <c>20-COS-TAD-POF-CODE.NPL</c>: the strategy is CLAMPED to 7
        /// (the source tests "if the strategy is above 7 then use 7"), the I-field is 0o3 bytes
        /// normally and 0o23 (19 decimal) for strategy 7 - that is 1 + 2 + 16 - and the BRKMAX word is
        /// written with a whole-word store, which is why the header is laid down on an ODD byte
        /// (see <c>CRHEOD</c> and
        /// <see cref="TadMessageBuilder"/>).
        /// </para>
        /// </remarks>
        Bmmx = 0x04,

        /// <summary>
        /// ESCA - escape received (<c>0x08</c>). Asker-sent; the host answers with opcode <c>0x20</c>.
        /// </summary>
        Esca = 0x08,

        /// <summary>
        /// DCON - disconnect indication (<c>0x09</c>).
        /// </summary>
        Dcon = 0x09,

        /// <summary>
        /// TMOD - terminal mode flags (<c>0x0C</c>).
        /// </summary>
        Tmod = 0x0C,

        /// <summary>
        /// TTYP - terminal type id (<c>0x0D</c>).
        /// </summary>
        Ttyp = 0x0D,

        /// <summary>
        /// CESC - enable (<c>0x01</c>) or disable (<c>0x00</c>) the escape function for this session
        /// (<c>0x0E</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// CORRECTED 2026-08-18 against the SINTRAN III version J NPL source. The earlier remark here
        /// read "a 1-byte state that steps 0x00 (auth-prompt) to 0x01 (auth-complete)" - a story fitted
        /// to the login capture. The executable source says what the byte IS:
        /// <c>BCESC</c> in <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL</c> builds the
        /// message as <c>IF DFOPP.DFLAG BIT 5IESC THEN A:=0 ELSE A:=1</c> - 5IESC is "inhibit escape",
        /// so payload 1 means the escape function is ENABLED and 0 means it is DISABLED.
        /// </para>
        /// <para>
        /// The observed 0 to 1 step during login is therefore the host turning escape off while
        /// credentials are typed and back on afterwards - the same bytes, correctly explained. Choosing
        /// WHICH character means escape is <see cref="Desc"/> (0x0F), not this.
        /// </para>
        /// <para>
        /// A responder that has escape inhibited answers an incoming <see cref="Esca"/> with
        /// <see cref="Edrs"/> rather than <see cref="Esrs"/> - see <c>ESCDIS</c> in
        /// <c>20-COS-TAD-POF-CODE.NPL</c>.
        /// </para>
        /// </remarks>
        Cesc = 0x0E,

        /// <summary>
        /// DESC - define escape character (<c>0x0F</c>).
        /// </summary>
        Desc = 0x0F,

        /// <summary>
        /// SYCN - the SYSTEM CONTROL word (<c>0x13</c>). One 16-bit big-endian payload word.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CORRECTED 2026-08-18. The op is a general system-control channel, not a "login-state word":
        /// <c>CTOBAD</c> in <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL</c> sends
        /// <c>7SYCN</c> when the output-ioset control code is 23 (octal) and <see cref="Uscn"/> when it
        /// is 24, one word by <c>WORDPUT</c> in both cases, flushing the buffer immediately only for
        /// parameter values 1, 13 and 17 (octal).
        /// </para>
        /// <para>
        /// The observed login stepping stays exactly as measured and is one USE of the channel: the
        /// 16-bit value steps 0x0002 (connected/ENTER) to 0x0003 (password prompt) to 0x0006
        /// (password OK) to 0x000A (logged in). VERIFIED from conn-to-d102 frames 62/64/68/70;
        /// reaching 0x000A is what marks the TAD "logged in" so SINTRAN stops applying the
        /// 1-minute "not logged in" idle drop. Whatever writes those particular codes runs above the
        /// TAD driver and is NOT in the J listing, so their encoding remains UNKNOWN.
        /// </para>
        /// </remarks>
        Sycn = 0x13,

        /// <summary>
        /// USCN - the USER CONTROL word (<c>0x14</c>). One 16-bit payload word; the sender then waits
        /// for an <see cref="Errs"/> response.
        /// </summary>
        /// <remarks>
        /// <c>7USCN = 000024</c> octal in the J, K03, L07 and M06 symbol tables. Built by <c>CTOBAD</c>
        /// (<c>06-COS-TAD-RES-CODE.NPL</c>) for output-ioset control code 24 (octal): it stores
        /// <c>7ERRS</c> into the datafield's response-wait slot and suspends the caller. Not handled
        /// here; named so the byte is not re-carved.
        /// </remarks>
        Uscn = 0x14,

        /// <summary>
        /// RESE - reset connection request (<c>0x16</c>).
        /// </summary>
        Rese = 0x16,

        /// <summary>
        /// RECO - reset confirm (<c>0x17</c>).
        /// </summary>
        Reco = 0x17,

        /// <summary>
        /// DUMM - dummy / no-op keep-the-stream-moving message (<c>0x18</c>).
        /// </summary>
        Dumm = 0x18,

        /// <summary>
        /// OPSV - OS / protocol version handshake (<c>0x1F</c>).
        /// </summary>
        Opsv = 0x1F,

        /// <summary>
        /// ESRS - escape response (<c>0x20</c>), the host's answer to the asker's ESCA.
        /// </summary>
        /// <remarks>
        /// Observed live from a real D102 as the first of the two frames answering ESCA (the second
        /// is <see cref="Rese"/>), and confirmed as <c>7ESRS=000040</c> octal in the SINTRAN symbol
        /// table - see the remarks on <see cref="Lun"/> for where that table lives.
        /// </remarks>
        Esrs = 0x20,

        /// <summary>
        /// CERS - escape / CESC response (<c>0x21</c>). Asker-sent after each host burst / CESC change.
        /// </summary>
        Cers = 0x21,

        // ---------------------------------------------------------------------------------------
        // Added 2026-08-18 from the SINTRAN III version J NPL source audit (DOC/XMSG-NPL-SOURCE-AUDIT.md
        // sections 5.1 and C1/C3/C4). Every value below was re-checked BY HAND in all four symbol
        // tables under SINTRAN/NPL-SOURCE/SYMBOLS - J, K03, L07 and M06 - and is identical in all
        // four, so the values are stable across the whole J..M range and safe for the Release L
        // machines we drive. What each op DOES comes from the executable driver in
        // SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL (RES, the resident half) and
        // 20-COS-TAD-POF-CODE.NPL (POF, the paged-out half).
        //
        // None of these has been seen on OUR wire yet - a login does not trigger them, a running
        // program does. They are named so an arriving byte decodes instead of being cast raw.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// ISRQ - remote ISIZE request (<c>0x22</c>). Empty I-field.
        /// </summary>
        /// <remarks>
        /// <c>7ISRQ = 000042</c> octal. When a program calls ISIZE (MON 66) or IBRSIZ (MON 313) and
        /// the local TAD input buffer holds no data, <c>BISIZ</c>/<c>OISIZ</c> in
        /// <c>06-COS-TAD-RES-CODE.NPL</c> build <c>7ISRQ</c> with <c>T:=0</c> (no data bytes), send it
        /// to the partner, and suspend the caller until the matching <see cref="Isrs"/> arrives.
        /// The awaited op is remembered with bit 15 set when the caller was ISIZE rather than IBRSIZ -
        /// that bit is a LOCAL variant flag and never appears on the wire.
        /// </remarks>
        Isrq = 0x22,

        /// <summary>
        /// ISRS - remote ISIZE response (<c>0x23</c>). Two data bytes, big-endian character count.
        /// </summary>
        /// <remarks>
        /// <c>7ISRS = 000043</c> octal. Travels as a high-priority message whose prebuilt head is
        /// <c>ISZRS := (7ISRS\2)</c>; the driver reads the count as <c>byte6 * 256 + byte7</c> in the
        /// <c>BDRINP</c> dispatch (<c>20-COS-TAD-POF-CODE.NPL</c>) and hands it back to the suspended
        /// caller's A register.
        /// </remarks>
        Isrs = 0x23,

        /// <summary>
        /// NOWT - nowait status (<c>0x24</c>). One status byte.
        /// </summary>
        /// <remarks>
        /// <c>7NOWT = 000044</c> octal. <c>NWSTA</c> (<c>20-COS-TAD-POF-CODE.NPL</c>) picks this op
        /// when the entry status in A is zero and <see cref="Tnow"/> when it is not, then writes the
        /// single status byte.
        /// </remarks>
        Nowt = 0x24,

        /// <summary>
        /// TNOW - nowait status, the variant chosen for a non-zero status (<c>0x25</c>). One status byte.
        /// </summary>
        /// <remarks>
        /// <c>7TNOW = 000045</c> octal. Same builder as <see cref="Nowt"/>; see its remarks.
        /// </remarks>
        Tnow = 0x25,

        /// <summary>
        /// NWRE - nowait restart (<c>0x26</c>). High priority, empty.
        /// </summary>
        /// <remarks>
        /// <c>7NWRE = 000046</c> octal. The receiving driver BOUNCES the message straight back to the
        /// partner and then restarts the suspended user program - the <c>NWREM</c> arm of the
        /// <c>BDRINP</c> high-priority dispatch in <c>20-COS-TAD-POF-CODE.NPL</c>.
        /// </remarks>
        Nwre = 0x26,

        /// <summary>
        /// RLOC - "REMOTE LOCAL (RUBOUT NORD-NET)" (<c>0x27</c>). High priority, empty.
        /// </summary>
        /// <remarks>
        /// <c>7RLOC = 000047</c> octal. Handled in the SAME branch as <see cref="Esca"/> by
        /// <c>ESCDIS</c>: with escape enabled it delivers either the configured local character (when
        /// the datafield's 5LCHAR flag is set) or a rubout (0o177) and answers <see cref="Esrs"/>; with
        /// escape disabled it answers <see cref="Edrs"/>.
        /// </remarks>
        Rloc = 0x27,

        /// <summary>
        /// EDRS - escape response, escape DISABLED (<c>0x29</c>). High priority, empty.
        /// </summary>
        /// <remarks>
        /// <c>7EDRS = 000051</c> octal, prebuilt in the driver as
        /// <c>EDRSP := (7EDRS\0,0,2) % ESCAPE RESPONSE ESCAPE DISABLED BUFFER</c>. <c>ESCDIS</c> sends
        /// this - NOT <see cref="Esrs"/> - when an <see cref="Esca"/> or <see cref="Rloc"/> arrives
        /// while the datafield's 5IESC (inhibit-escape) flag is set, and it does so immediately without
        /// running any escape handling.
        /// </remarks>
        Edrs = 0x29,

        /// <summary>
        /// TREP - terminal report status (<c>0x2A</c>). Two data bytes, big-endian.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>7TREP = 000052</c> octal, prebuilt head <c>TREPS := (7TREP\2)</c>. The receiver bounces
        /// the message back to the partner and then folds three bits of the 16-bit value into its own
        /// TINFO word (<c>20-COS-TAD-POF-CODE.NPL</c>, the <c>TREPS</c> arm of <c>BDRINP</c>):
        /// </para>
        /// <para>
        ///  - bit 2 - buffer overrun (sets 5BFUL)
        /// </para>
        /// <para>
        ///  - bit 3 - parity error (sets 5PAER)
        /// </para>
        /// <para>
        ///  - bit 4 - framing error (sets 5FRER)
        /// </para>
        /// </remarks>
        Trep = 0x2A,

        /// <summary>
        /// CPCO - completion code (<c>0xFA</c>). Four data bytes - two 16-bit words.
        /// </summary>
        /// <remarks>
        /// <c>7CPCO = 000372</c> octal. <c>SNDCP</c> (<c>20-COS-TAD-POF-CODE.NPL</c>) creates the header
        /// with the even-start builder and then stores the two words of the completion code with
        /// whole-word stores, advancing the byte pointer by 4.
        /// </remarks>
        Cpco = 0xFA,

        /// <summary>
        /// ERRS - error response (<c>0xFB</c>). Two data bytes, big-endian; the answer to
        /// <see cref="Uscn"/>.
        /// </summary>
        /// <remarks>
        /// <c>7ERRS = 000373</c> octal, prebuilt head <c>ERRSP := (7ERRS\2)</c>. Read big-endian from
        /// head bytes 6-7 exactly like <see cref="Isrs"/>, and it releases the caller that
        /// <c>CTOBAD</c> suspended after sending a <see cref="Uscn"/>.
        /// </remarks>
        Errs = 0xFB,

        /// <summary>
        /// REJE - reject (<c>0xFE</c>). One data byte: the type of the message being rejected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>7REJE = 000376</c> octal. <c>REJECT</c> (<c>20-COS-TAD-POF-CODE.NPL</c>) writes exactly
        /// three bytes - <c>7REJE</c>, count 1, then <c>CURMES AND 0xFF</c>, the offending message type.
        /// It is sent when a normal-priority message is of a type the driver does not accept, when a
        /// message claims more bytes than the buffer holds, and (via the <c>SRJE</c> path in
        /// <c>ESCDIS</c>) when a high-priority head matches none of the prebuilt ones.
        /// <c>SNDREJ</c> additionally appends an <see cref="Rfi"/> when the rejected message was data.
        /// </para>
        /// <para>
        /// On the machine that asked, a rejected message surfaces to the calling program as SINTRAN
        /// error TER01 (0o315 = 205).
        /// </para>
        /// </remarks>
        Reje = 0xFE,

        /// <summary>
        /// LUN - the TAD logical-unit index carried in the port assignment (<c>0x0B</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Where these values come from - read this before carving another opcode</b></para>
        /// <para>
        /// SINTRAN's own symbol tables are in this repository and give every TAD opcode outright:
        /// <c>SINTRAN/NPL-SOURCE/SYMBOLS/K03/*.SYMB.TXT</c>, one <c>NAME=oooooo</c> line per symbol
        /// in OCTAL. <c>7LUN=000013</c> is this member; <c>7DUMM=000030</c> is <see cref="Dumm"/>.
        /// Every value in this enum that had been carved from captures was checked against that
        /// table on 2026-08-17 and all twelve matched, so the carving was right - and would have
        /// been far quicker as a grep.
        /// </para>
        /// <para>
        /// The value byte after this tag is the logical unit index, where <c>LU = 768 + value</c> -
        /// measured, not inferred: with index <c>0x02</c> a real ND printed
        /// <c>TAD LOGICAL UNIT NO: 770</c>. Real captures carry both <c>0x04</c> and <c>0x02</c>;
        /// the allocation rule is still unknown.
        /// </para>
        /// </remarks>
        Lun = 0x0B,

        /// <summary>
        /// FBSI - the field/buffer size tag carried in the port assignment (<c>0x15</c>).
        /// </summary>
        /// <remarks>
        /// <c>7FBSI=000025</c> octal in the symbol table cited on <see cref="Lun"/>. We emit it with
        /// the two-byte value <c>01 08</c> copied from a real capture; what the two bytes MEAN is
        /// not established, so treat the pair as an observed constant rather than a decoded field.
        /// </remarks>
        Fbsi = 0x15,

        /// <summary>
        /// UMOD - a mode message that arrived with Release L (<c>0x2B</c>). NOT handled here.
        /// </summary>
        /// <remarks>
        /// <c>7UMOD=000053</c> octal. Its meaning is not established. See <see cref="Mod8"/> for the
        /// release note that applies to both.
        /// </remarks>
        Umod = 0x2B,

        /// <summary>
        /// 78MOD - 8-bit mode negotiation for the terminal line (<c>0x2C</c>). NOT handled here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>78MOD=000054</c> octal in <c>SYMBOLS/L07</c> and <c>SYMBOLS/M06</c>, and absent from
        /// K03 - it arrived after Release K, along with <see cref="Umod"/>.
        /// </para>
        /// <para><b>Why the release matters, checked rather than assumed</b></para>
        /// <para>
        /// Every other opcode here was first validated against the K03 table while the live machine
        /// reports XMSG Release L, so the tables were diffed: K03 to L07 only ADDS symbols and
        /// changes no existing value, and L07 to M06 changes only two large address-like values
        /// (<c>7LOGG</c>, <c>7MCTY</c>) which are not opcodes. So the opcode values are stable
        /// across K03/L07/M06 and the earlier validation stands - but the SET is not, and these two
        /// are what a Release K table would have hidden.
        /// </para>
        /// </remarks>
        Mod8 = 0x2C,
    }
}
