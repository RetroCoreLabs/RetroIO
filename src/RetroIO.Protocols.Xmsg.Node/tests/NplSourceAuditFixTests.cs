using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.SubProtocol;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The things the version J NPL source settled, pinned as bytes and values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every case here corresponds to a finding in <c>DOC/XMSG-NPL-SOURCE-AUDIT.md</c> that was
    /// confirmed by reading the cited routine in the source, not by trusting the audit. The routine
    /// is named in each test so the next reader can go and check it rather than take this on faith.
    /// </para>
    /// <para>
    /// The sources are <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL</c> (the resident
    /// half of the TAD driver) and <c>20-COS-TAD-POF-CODE.NPL</c> (the paged half), with the opcode
    /// VALUES cross-checked in all four symbol tables under <c>SINTRAN/NPL-SOURCE/SYMBOLS</c>.
    /// </para>
    /// </remarks>
    public sealed class NplSourceAuditFixTests
    {
        /// <summary>
        /// REJE is three bytes: the opcode, a count of one, and the rejected type.
        /// </summary>
        /// <remarks>
        /// <c>REJECT</c>: <c>A:=7REJE; CALL STORBYT; A:=1; CALL STORBYT; DFOPP.CURMES/\377;
        /// CALL STORBYT</c>. Nothing built or parsed 0xFE before this change.
        /// </remarks>
        [Fact]
        public void Reje_IsOpcodeCountOneAndTheRejectedType()
        {
            byte[] bytes = new TadMessageBuilder().Reje(0x03).Build();

            Assert.Equal(3, bytes.Length);
            Assert.Equal(0xFE, bytes[0]);
            Assert.Equal(0x01, bytes[1]);
            Assert.Equal(0x03, bytes[2]);
        }

        /// <summary>
        /// The reject reader recovers the offending opcode, and refuses anything that is not one.
        /// </summary>
        [Fact]
        public void TryReadReject_ReadsTheOffendingOpcodeAndRefusesOtherMessages()
        {
            byte[] reject = new TadMessageBuilder().Reje(0x77).Build();
            Assert.True(TadRejectPolicy.TryReadReject(reject, out byte offending));
            Assert.Equal(0x77, offending);

            // A DUMM is not a reject, however short it is.
            byte[] dumm = new TadMessageBuilder().Dumm().Build();
            Assert.False(TadRejectPolicy.TryReadReject(dumm, out _));
        }

        /// <summary>
        /// ISRQ carries no data at all, and ISRS carries the count big-endian.
        /// </summary>
        /// <remarks>
        /// <c>BISIZ</c>/<c>OISIZ</c> build the request with <c>T:=0</c> - no I-field. The driver reads
        /// the response as <c>byte6 * 256 + byte7</c>, so the high byte goes first.
        /// </remarks>
        [Fact]
        public void Isrq_IsEmpty_AndIsrs_IsBigEndian()
        {
            byte[] request = new TadMessageBuilder().Isrq().Build();
            Assert.Equal(2, request.Length);
            Assert.Equal(0x22, request[0]);
            Assert.Equal(0x00, request[1]);

            byte[] response = new TadMessageBuilder().Isrs(0x0102).Build();
            Assert.Equal(4, response.Length);
            Assert.Equal(0x23, response[0]);
            Assert.Equal(0x02, response[1]);
            Assert.Equal(0x01, response[2]);   // high byte first
            Assert.Equal(0x02, response[3]);
        }

        /// <summary>
        /// EDRS and ESRS are distinct empty control messages, 0x29 and 0x20.
        /// </summary>
        /// <remarks>
        /// The driver holds both as prebuilt heads - <c>ERESP := (7ESRS\0,0,2)</c> and
        /// <c>EDRSP := (7EDRS\0,0,2) % ESCAPE RESPONSE ESCAPE DISABLED BUFFER</c>.
        /// </remarks>
        [Fact]
        public void Edrs_And_Esrs_AreDistinctEmptyMessages()
        {
            byte[] esrs = new TadMessageBuilder().Esrs().Build();
            byte[] edrs = new TadMessageBuilder().Edrs().Build();

            Assert.Equal(2, esrs.Length);
            Assert.Equal(0x20, esrs[0]);
            Assert.Equal(0x00, esrs[1]);

            Assert.Equal(2, edrs.Length);
            Assert.Equal(0x29, edrs[0]);
            Assert.Equal(0x00, edrs[1]);
        }

        /// <summary>
        /// Every opcode the audit recovered has the value the symbol tables give it.
        /// </summary>
        /// <remarks>
        /// The octal in the comment is what <c>SYMBOL-1-LIST.SYMB.TXT</c> prints, and the same value
        /// appears in J, K03, L07 and M06 - checked by hand on 2026-08-18. That is what makes it safe
        /// to use these against the Release L machines we drive.
        /// </remarks>
        [Fact]
        public void TheTwelveRecoveredOpcodesHaveTheirSymbolTableValues()
        {
            Assert.Equal(0x14, (byte)TadOp.Uscn);   // 7USCN = 000024
            Assert.Equal(0x22, (byte)TadOp.Isrq);   // 7ISRQ = 000042
            Assert.Equal(0x23, (byte)TadOp.Isrs);   // 7ISRS = 000043
            Assert.Equal(0x24, (byte)TadOp.Nowt);   // 7NOWT = 000044
            Assert.Equal(0x25, (byte)TadOp.Tnow);   // 7TNOW = 000045
            Assert.Equal(0x26, (byte)TadOp.Nwre);   // 7NWRE = 000046
            Assert.Equal(0x27, (byte)TadOp.Rloc);   // 7RLOC = 000047
            Assert.Equal(0x29, (byte)TadOp.Edrs);   // 7EDRS = 000051
            Assert.Equal(0x2A, (byte)TadOp.Trep);   // 7TREP = 000052
            Assert.Equal(0xFA, (byte)TadOp.Cpco);   // 7CPCO = 000372
            Assert.Equal(0xFB, (byte)TadOp.Errs);   // 7ERRS = 000373
            Assert.Equal(0xFE, (byte)TadOp.Reje);   // 7REJE = 000376
        }

        /// <summary>
        /// The mnemonic table names the two escape responses instead of printing a bare hex byte.
        /// </summary>
        /// <remarks>
        /// Both were missing from the dissector table this was copied from, so a captured escape
        /// response used to decode as "0x20".
        /// </remarks>
        [Fact]
        public void TheOpcodeNameTableKnowsBothEscapeResponses()
        {
            Assert.Equal("ESRS", TadOpcodes.Name(0x20));
            Assert.Equal("EDRS", TadOpcodes.Name(0x29));
            Assert.Equal("REJE", TadOpcodes.Name(0xFE));
        }

        /// <summary>
        /// The one TMOD value we have on the wire, 0x08, decodes as "log me out if the carrier drops".
        /// </summary>
        /// <remarks>
        /// Sender <c>CTMOD</c> and receiver <c>BDTMOD</c> agree bit for bit: bit 0 5CAPITAL, bit 1
        /// 5CRDLY, bit 2 SCREEN, bit 3 5LBLOG. The byte was undecoded here before.
        /// </remarks>
        [Fact]
        public void TmodByte08_MeansLogoutOnMissingCarrier()
        {
            TerminalModeFlags observed = (TerminalModeFlags)0x08;

            Assert.Equal(TerminalModeFlags.LogoutOnMissingCarrier, observed);
            Assert.Equal(0x01, (byte)TerminalModeFlags.CapitalLettersOnly);
            Assert.Equal(0x02, (byte)TerminalModeFlags.CarriageReturnDelay);
            Assert.Equal(0x04, (byte)TerminalModeFlags.StopOnFullPage);

            TadNegotiatedParameters parameters = new TadNegotiatedParameters();
            Assert.Null(parameters.TerminalModeFlagsValue);
            parameters.TerminalMode = 0x08;
            Assert.Equal(TerminalModeFlags.LogoutOnMissingCarrier, parameters.TerminalModeFlagsValue);
        }

        /// <summary>
        /// CNVERR turns an XMSG error into a SINTRAN one: negate it and OR on 0o41000.
        /// </summary>
        /// <remarks>
        /// XENSE is -34 decimal, which is -0o42, so it surfaces as 0o41042 = 16930.
        /// </remarks>
        [Fact]
        public void XmsgErrorsConvertOntoTheOctal41000Base()
        {
            Assert.Equal(16896, TadErrorCodes.XmsgErrorBase);
            Assert.Equal(16930, TadErrorCodes.ToSintranError(-34));   // XENSE -> 0o41042
            Assert.Equal(16897, TadErrorCodes.ToSintranError(-1));    // 0o41001

            Assert.Equal(204, TadErrorCodes.InputDuringDelayedEscape);
            Assert.Equal(205, TadErrorCodes.MessageRejected);
            Assert.Equal(206, TadErrorCodes.TadNotConnected);
        }

        /// <summary>
        /// The reject policy names everything TadOp names, and nothing else.
        /// </summary>
        [Fact]
        public void RejectPolicy_KnowsEveryNamedOpcodeAndRefusesTheRest()
        {
            Assert.True(TadRejectPolicy.IsKnownOpcode((byte)TadOp.Bdat));
            Assert.True(TadRejectPolicy.IsKnownOpcode((byte)TadOp.Opsv));
            Assert.True(TadRejectPolicy.IsKnownOpcode((byte)TadOp.Edrs));
            Assert.False(TadRejectPolicy.IsKnownOpcode(0x77));
            Assert.False(TadRejectPolicy.IsKnownOpcode(0x00));

            // Recorded, deliberately NOT enforced: the J driver's normal-priority accept set is only
            // these five. It excludes OPSV, which every real client we have captured sends, so it is
            // evidence about version J rather than a rule we can apply to a Release L peer.
            Assert.True(TadRejectPolicy.IsAcceptedByVersionJNormalPriority((byte)TadOp.Bdat));
            Assert.True(TadRejectPolicy.IsAcceptedByVersionJNormalPriority((byte)TadOp.Tmod));
            Assert.True(TadRejectPolicy.IsAcceptedByVersionJNormalPriority((byte)TadOp.Ttyp));
            Assert.True(TadRejectPolicy.IsAcceptedByVersionJNormalPriority((byte)TadOp.Desc));
            Assert.True(TadRejectPolicy.IsAcceptedByVersionJNormalPriority((byte)TadOp.Dumm));
            Assert.False(TadRejectPolicy.IsAcceptedByVersionJNormalPriority((byte)TadOp.Opsv));
        }
    }
}
