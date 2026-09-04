using System;
using System.Text;

using RetroIO.Protocols.Xmsg;

using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Parses XROUT service messages captured from a running SINTRAN's own memory, proving the
    /// message-buffer form carries the four-byte header the wire form omits.
    /// </summary>
    /// <remarks>
    /// Bytes taken verbatim from a MON 200 XFWRI trace on the BIGDISK0-L image - the buffers tasks
    /// hand to XROUT, before anything reaches a wire. Evidence and method:
    /// DOC/XMSG-XROUT-BUFFER-FORM-CAPTURED-2026-07-26.md.
    /// </remarks>
    public sealed class XroutBufferFormTests
    {
        /// <summary>
        /// The name registration a server sends to XROUT: XSNAM plus its name.
        /// </summary>
        /// <remarks>
        /// This is the call that publishes a server. It never crosses a wire, so it could only
        /// ever be seen from inside the machine.
        /// </remarks>
        [Fact]
        public void XsnamRegistration_ParsesWithTheHeader()
        {
            byte[] buffer = FromHex("534200" + "0A" + "FF082A584D2D4649444F");

            XroutMessage message = XroutMessage.Parse(buffer, XroutMessageFraming.WithHeader);

            Assert.Equal(0x53, message.Serial);
            Assert.Equal((byte)XroutService.XSNAM, message.Service);
            Assert.Equal(10, message.Length);

            Assert.Single(message.Parameters);
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.True(message.Parameters[0].IsString);
            Assert.Equal("*XM-FIDO", message.Parameters[0].AsText());
        }

        /// <summary>
        /// A reply carrying a magic number decomposes with the carved layout, from guest memory
        /// rather than from the wire.
        /// </summary>
        [Fact]
        public void ReplyMagicNumber_DecomposesAsPortAndRandom()
        {
            byte[] buffer = FromHex("01000010" + "0104006401F8" + "FE082A584D2D4649444F");

            XroutMessage message = XroutMessage.Parse(buffer, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutError.XRSOK, message.Service);
            Assert.Equal(2, message.Parameters.Count);

            uint magic;
            Assert.True(message.Parameters[0].TryGetUInt32(out magic));
            Assert.Equal(0x006401F8u, magic);

            // system 100, port word 504 = (3 << 7) | 120
            Assert.Equal(100u, magic >> 16);
            ushort portWord = (ushort)(magic & 0xFFFF);
            Assert.Equal(504, (int)portWord);

            int portNumber;
            int random;
            Assert.True(XmsgPortWordAllocator.TrySplit(portWord, out portNumber, out random));
            Assert.Equal(3, portNumber);
            Assert.Equal(120, random);

            Assert.Equal("*XM-FIDO", message.Parameters[1].AsText());
        }

        /// <summary>
        /// The define-remote-name command produces exactly the parameters appendix B section 3.12
        /// specifies: the system name, then the system number.
        /// </summary>
        [Fact]
        public void XsdrnDefineRemoteName_MatchesTheManual()
        {
            byte[] buffer = FromHex("0149000A" + "FF0444313030" + "02020064");

            XroutMessage message = XroutMessage.Parse(buffer, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSDRN, message.Service);
            Assert.Equal(2, message.Parameters.Count);

            Assert.True(message.Parameters[0].IsString);
            Assert.Equal("D100", message.Parameters[0].AsText());

            uint systemNumber;
            Assert.True(message.Parameters[1].TryGetUInt32(out systemNumber));
            Assert.Equal(100u, systemNumber);
        }

        /// <summary>
        /// Our own builder produces the same bytes the guest wrote, which is the round trip that
        /// makes the builders trustworthy.
        /// </summary>
        [Fact]
        public void OurBuilder_ReproducesTheCapturedDefineRemoteName()
        {
            byte[] captured = FromHex("0149000A" + "FF0444313030" + "02020064");

            XroutMessage built = new XroutMessageBuilder()
                .WithSerial(0x01)
                .WithService(XroutService.XSDRN)
                .AddString(1, "D100")
                .AddInteger16(2, 100)
                .Build();

            Assert.Equal(captured, built.ToArray(XroutMessageFraming.WithHeader));
        }

        /// <summary>
        /// Our own builder reproduces the captured registration too.
        /// </summary>
        [Fact]
        public void OurBuilder_ReproducesTheCapturedRegistration()
        {
            byte[] captured = FromHex("5342000A" + "FF082A584D2D4649444F");

            XroutMessage built = new XroutMessageBuilder()
                .WithSerial(0x53)
                .WithService(XroutService.XSNAM)
                .AddString(1, "*XM-FIDO")
                .Build();

            Assert.Equal(captured, built.ToArray(XroutMessageFraming.WithHeader));
        }

        /// <summary>
        /// The walk service starts from a magic number and the next call resumes one past the
        /// answer, which is how "greater than or equal" becomes an enumeration.
        /// </summary>
        [Fact]
        public void XsgniWalk_ResumesPastThePreviousAnswer()
        {
            XroutMessage first = XroutMessage.Parse(
                FromHex("01450004" + "01020000"), XroutMessageFraming.WithHeader);
            XroutMessage next = XroutMessage.Parse(
                FromHex("01450006" + "0104006401F9"), XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSGNI, first.Service);
            Assert.Equal((byte)XroutService.XSGNI, next.Service);

            uint from;
            Assert.True(next.Parameters[0].TryGetUInt32(out from));

            // One past the 0x006401F8 the previous answer reported.
            Assert.Equal(0x006401F9u, from);
        }

        /// <summary>
        /// The file-transfer server creates its connection port with the NAME ALONE - no count
        /// parameter at all.
        /// </summary>
        /// <remarks>
        /// Captured 2026-07-27 while *XFTRA started. The manual lists three parameters for XSCRS;
        /// this proves parameters 2 and 3 are genuinely optional.
        /// </remarks>
        [Fact]
        public void XscrsForXftra_CarriesTheNameOnly()
        {
            byte[] buffer = FromHex("5350 0008 FF06 2A5846545241");

            XroutMessage message = XroutMessage.Parse(buffer, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSCRS, message.Service);
            Assert.Single(message.Parameters);
            Assert.Equal("*XFTRA", message.Parameters[0].AsText());
        }

        /// <summary>
        /// The file-access servers create their connection ports with an initial count of ZERO,
        /// then raise it one service point at a time.
        /// </summary>
        /// <remarks>
        /// This is the mechanism behind the "Free SPs" column: XSCRS does not set the total. Note
        /// the pad byte after the odd-length name - the integer block that follows is even-aligned.
        /// </remarks>
        [Theory]
        [InlineData("5350 000E FF07 2A46412D465341 00 0202 0000", "*FA-FSA")]
        [InlineData("5350 0010 FF09 2A46412D4653412D49 00 0202 0000", "*FA-FSA-I")]
        [InlineData("5350 0010 FF0A 2A46412D534552564552 0202 0000", "*FA-SERVER")]
        public void XscrsForFileAccess_StartsAtZeroConnections(string hex, string expectedName)
        {
            byte[] buffer = FromHex(hex);

            XroutMessage message = XroutMessage.Parse(buffer, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSCRS, message.Service);
            Assert.Equal(2, message.Parameters.Count);
            Assert.Equal(expectedName, message.Parameters[0].AsText());

            uint initialConnections;
            Assert.True(message.Parameters[1].TryGetUInt32(out initialConnections));
            Assert.Equal(0u, initialConnections);
        }

        /// <summary>
        /// Each service point is added by its own XSNSP of exactly +1.
        /// </summary>
        /// <remarks>
        /// Captured once for *XFTRA, twice for *FA-FSA and thirty times for *FA-SERVER - matching
        /// the 1 / 2 / 30 the registry then reported.
        /// </remarks>
        [Fact]
        public void Xsnsp_AddsOneServicePoint()
        {
            byte[] buffer = FromHex("54510004" + "01020001");

            XroutMessage message = XroutMessage.Parse(buffer, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSNSP, message.Service);
            Assert.Single(message.Parameters);

            uint delta;
            Assert.True(message.Parameters[0].TryGetUInt32(out delta));
            Assert.Equal(1u, delta);
        }

        /// <summary>
        /// A registry walk answers with a third parameter - the free-connection count - but only
        /// for connection ports.
        /// </summary>
        /// <remarks>
        /// The *FA-SERVER entry reports 0x1E = 30, the total its thirty XSNSP calls built up. A
        /// plain XSNAM port such as *XM-FIDO answers with no parameter 3 at all, which is how a
        /// caller tells the two port kinds apart.
        /// </remarks>
        [Fact]
        public void XsgniReply_ReportsFreeConnectionsForAConnectionPort()
        {
            byte[] connectionPort = FromHex(
                "0100 0016 0104 006405CC FE0A 2A46412D534552564552 0302 001E");

            XroutMessage message = XroutMessage.Parse(connectionPort, XroutMessageFraming.WithHeader);

            Assert.Equal(3, message.Parameters.Count);
            Assert.Equal("*FA-SERVER", message.Parameters[1].AsText());

            uint freeConnections;
            Assert.True(message.Parameters[2].TryGetUInt32(out freeConnections));
            Assert.Equal(30u, freeConnections);

            // ... and the magic number decomposes as port 11, which is where the registry listed it.
            uint magic;
            Assert.True(message.Parameters[0].TryGetUInt32(out magic));
            int portNumber;
            int random;
            Assert.True(XmsgPortWordAllocator.TrySplit((ushort)(magic & 0xFFFF), out portNumber, out random));
            Assert.Equal(11, portNumber);
            Assert.Equal(76, random);
        }

        /// <summary>
        /// The named-port answer for *XM-FIDO has no free-connection parameter.
        /// </summary>
        [Fact]
        public void XsgniReply_HasNoFreeConnectionsForANamedPort()
        {
            byte[] namedPort = FromHex(
                "01000010" + "0104006401F8" + "FE082A584D2D4649444F");

            XroutMessage message = XroutMessage.Parse(namedPort, XroutMessageFraming.WithHeader);

            Assert.Equal(2, message.Parameters.Count);
            Assert.Equal("*XM-FIDO", message.Parameters[1].AsText());
        }

        /// <summary>
        /// Looking a SYSTEM name up with XSGIN answers with the system number as parameter 2 and
        /// no parameter 1.
        /// </summary>
        /// <remarks>
        /// Captured 2026-07-27 from the XMSG command program's Get-System-Name-or-Number. The
        /// absent parameter 1 is the point: the manual makes the port number optional, returned
        /// only when the name is a port name. See
        /// DOC/XMSG-XSGIN-NAME-LOOKUP-CAPTURED-2026-07-27.md.
        /// </remarks>
        [Fact]
        public void XsginForASystemName_AnswersWithTheSystemNumberOnly()
        {
            XroutMessage request = XroutMessage.Parse(
                FromHex("0152 0006 FF04 44313032"), XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSGIN, request.Service);
            Assert.Single(request.Parameters);
            Assert.Equal("D102", request.Parameters[0].AsText());

            XroutMessage reply = XroutMessage.Parse(
                FromHex("0100 0004 0202 0066"), XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutError.XRSOK, reply.Service);
            Assert.Single(reply.Parameters);
            Assert.Equal(2, reply.Parameters[0].ParameterNumber);

            uint systemNumber;
            Assert.True(reply.Parameters[0].TryGetUInt32(out systemNumber));
            Assert.Equal(102u, systemNumber);
        }

        /// <summary>
        /// Looking a PORT name up answers with both outputs - port number as parameter 1, system
        /// number as parameter 2.
        /// </summary>
        /// <remarks>
        /// `*TADADM` sat on port 4 of system 100 in that boot, which is exactly what came back.
        /// This is the form that resolves a name without needing privilege, and without ever
        /// yielding a magic number.
        /// </remarks>
        [Fact]
        public void XsginForAPortName_AnswersWithPortAndSystem()
        {
            XroutMessage request = XroutMessage.Parse(
                FromHex("0152 000A FF07 2A5441444144 4D 00"), XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSGIN, request.Service);
            Assert.Equal("*TADADM", request.Parameters[0].AsText());

            XroutMessage reply = XroutMessage.Parse(
                FromHex("0100 0008 0102 0004 0202 0064"), XroutMessageFraming.WithHeader);

            Assert.Equal(2, reply.Parameters.Count);

            uint portNumber;
            Assert.True(reply.Parameters[0].TryGetUInt32(out portNumber));
            Assert.Equal(4u, portNumber);

            uint systemNumber;
            Assert.True(reply.Parameters[1].TryGetUInt32(out systemNumber));
            Assert.Equal(100u, systemNumber);
        }

        /// <summary>
        /// An unknown name comes back with the service byte overwritten by a status, and no
        /// parameters at all.
        /// </summary>
        /// <remarks>
        /// Captured by asking for "D10", an abbreviation that is not a defined name. The console
        /// then printed "System name D10 is not known".
        /// </remarks>
        [Fact]
        public void XsginForAnUnknownName_AnswersWithAStatusAndNoParameters()
        {
            XroutMessage reply = XroutMessage.Parse(
                FromHex("0102 0000"), XroutMessageFraming.WithHeader);

            Assert.Equal(0x02, reply.Service);
            Assert.Empty(reply.Parameters);
        }

        /// <summary>
        /// The file-transfer request: an XSLET letter to *XFTRA carrying the whole transfer
        /// specification as extra parameters.
        /// </summary>
        /// <remarks>
        /// Captured 2026-07-28 - the first working traffic ever decoded from a COSMOS file server.
        /// Note the parameters are NOT in numerical order on the wire (1, 2, 12, 13, 8, 9, 10, 11);
        /// they are tagged, so order carries no meaning and a parser must not assume ascending.
        /// See DOC/XMSG-XFTRA-FILE-TRANSFER-REQUEST-CAPTURED-2026-07-28.md.
        /// </remarks>
        [Fact]
        public void XsletToXftra_CarriesTheWholeTransferSpecification()
        {
            byte[] request = FromHex(
                "0141 003A "                                 // XSLET, 58 bytes of body
                + "FF06 2A5846545241 "                       // p1  string "*XFTRA"
                + "FE04 44313032 "                           // p2  string "D102"
                + "F406 53595354454D "                       // p12 string "SYSTEM"
                + "0D02 0000 "                               // p13 integer 0
                + "F810 22584D53472D434F50593A4241544322 "   // p8  string "\"XMSG-COPY:BATC\""
                + "F704 53594D42 "                           // p9  string "SYMB"
                + "0A02 0400 "                               // p10 integer 1024
                + "0B02 0002");                              // p11 integer 2

            XroutMessage message = XroutMessage.Parse(request, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSLET, message.Service);
            Assert.Equal(8, message.Parameters.Count);

            // The two documented XSLET fields: who to reach, and on which system.
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.Equal("*XFTRA", message.Parameters[0].AsText());
            Assert.Equal(2, message.Parameters[1].ParameterNumber);
            Assert.Equal("D102", message.Parameters[1].AsText());

            // Everything else is the application's own, documented nowhere.
            Assert.Equal(12, message.Parameters[2].ParameterNumber);
            Assert.Equal("SYSTEM", message.Parameters[2].AsText());

            // The destination file name keeps its quotes - the REMOTE system is what reads them
            // as "create this file", so the client must not strip them.
            Assert.Equal(8, message.Parameters[4].ParameterNumber);
            Assert.Equal("\"XMSG-COPY:BATC\"", message.Parameters[4].AsText());

            Assert.Equal(9, message.Parameters[5].ParameterNumber);
            Assert.Equal("SYMB", message.Parameters[5].AsText());

            // 1024 and 2 - a buffer size and a buffer count.
            //
            // This was a GUESS until 2026-07-29, when the FILE-TRANSFER program's advanced mode was
            // found to expose exactly:
            //     Define-transfer-conditions <No of buffers>,<Size in bytes>,<Secure messages>
            // Those are the only three transfer knobs the program has, and the COSMOS User Guide
            // p.146 says "files are transferred using two 1024-byte buffers at a time" - which is
            // precisely 2 and 1024. So p10 is the size and p11 the count.
            //
            // Still NOT proven: controlled variation on 2026-07-28 moved neither field, so no
            // capture yet shows them changing together with the setting. Driving
            // Define-transfer-conditions with different values and re-capturing would settle it.
            // The third knob, "Secure messages", has not been located in the letter at all.
            uint firstConstant;
            Assert.True(message.Parameters[6].TryGetUInt32(out firstConstant));
            Assert.Equal(1024u, firstConstant);

            uint secondConstant;
            Assert.True(message.Parameters[7].TryGetUInt32(out secondConstant));
            Assert.Equal(2u, secondConstant);
        }

        /// <summary>
        /// The remote system name is parameter 2 and the remote user name is parameter 12, each
        /// proven by moving alone.
        /// </summary>
        /// <remarks>
        /// Captured on 2026-07-28 by driving TRANSFER-FILE six times on the SINTRAN K image and
        /// changing exactly one input per run. This request differs from the baseline only in the
        /// remote user - SYSTEM became RT - so the message shrank by four bytes and nothing else
        /// moved. A separate run changing only the system to D101 moved only parameter 2.
        /// </remarks>
        [Fact]
        public void XftraRequest_NamesTheRemoteSystemInP2AndTheRemoteUserInP12()
        {
            byte[] request = FromHex(
                "0141 0036 "                                 // XSLET, 54 bytes - four fewer than the baseline
                + "FF06 2A5846545241 "                       // p1  string "*XFTRA"
                + "FE04 44313032 "                           // p2  string "D102"   - the remote SYSTEM
                + "F402 5254 "                               // p12 string "RT"     - the remote USER
                + "0D02 0000 "                               // p13 integer 0       - empty password
                + "F810 22584D53472D434F50593A4241544322 "   // p8  string "\"XMSG-COPY:BATC\""
                + "F704 53594D42 "                           // p9  string "SYMB"
                + "0A02 0400 "                               // p10 integer 1024
                + "0B02 0002");                              // p11 integer 2

            XroutMessage message = XroutMessage.Parse(request, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSLET, message.Service);
            Assert.Equal(8, message.Parameters.Count);

            Assert.Equal(2, message.Parameters[1].ParameterNumber);
            Assert.Equal("D102", message.Parameters[1].AsText());

            Assert.Equal(12, message.Parameters[2].ParameterNumber);
            Assert.Equal("RT", message.Parameters[2].AsText());
        }

        /// <summary>
        /// An odd-length string is followed by a pad byte, and the declared message length counts
        /// the pad.
        /// </summary>
        /// <remarks>
        /// Every string captured before 2026-07-28 happened to be even-length, so the padding rule
        /// had never been exercised. Renaming the destination to a 17-character spec grew the
        /// message from 58 to 60 bytes - two, not one - and put a zero byte after the text.
        /// A parser that advances by the declared parameter length alone desynchronises here and
        /// misreads every parameter that follows.
        /// </remarks>
        [Fact]
        public void OddLengthStringParameter_IsPaddedToAWordAndTheLengthCountsThePad()
        {
            byte[] request = FromHex(
                "0141 003C "                                 // XSLET, 60 bytes - two more than the baseline
                + "FF06 2A5846545241 "                       // p1  string "*XFTRA"
                + "FE04 44313032 "                           // p2  string "D102"
                + "F406 53595354454D "                       // p12 string "SYSTEM"
                + "0D02 0000 "                               // p13 integer 0
                + "F811 224F544845522D434F50593A53594D4222 00 "  // p8 17 chars + one pad byte
                + "F704 53594D42 "                           // p9  string "SYMB" - UNCHANGED, see below
                + "0A02 0400 "                               // p10 integer 1024
                + "0B02 0002");                              // p11 integer 2

            XroutMessage message = XroutMessage.Parse(request, XroutMessageFraming.WithHeader);

            Assert.Equal(8, message.Parameters.Count);

            // The odd-length value itself is 17 bytes; the pad is framing, not content.
            Assert.Equal(8, message.Parameters[4].ParameterNumber);
            Assert.Equal("\"OTHER-COPY:SYMB\"", message.Parameters[4].AsText());

            // Parameter 9 stayed "SYMB" even though the destination type became SYMB in this run
            // and was BATC in the baseline. So p9 is NOT the destination file type, which was the
            // obvious reading of the first capture. Its meaning is still unknown.
            Assert.Equal(9, message.Parameters[5].ParameterNumber);
            Assert.Equal("SYMB", message.Parameters[5].AsText());

            // The parameters after the pad still parse, which is the point of the test.
            uint firstConstant;
            Assert.True(message.Parameters[6].TryGetUInt32(out firstConstant));
            Assert.Equal(1024u, firstConstant);
        }

        /// <summary>
        /// DEF-REMOTE emits XSDRN with the system name as string parameter 1 and the system number
        /// as integer parameter 2.
        /// </summary>
        /// <remarks>
        /// Service 73 was carved out of the ENNS0 binary; this is the first time it has been seen
        /// on live traffic, emitted by the XMSG command program's DEF-REMOTE.
        /// </remarks>
        [Fact]
        public void DefineRemoteName_CarriesTheNameAndTheSystemNumber()
        {
            byte[] request = FromHex(
                "0149 000A "                 // service 0x49 = 73 = XSDRN, 10 bytes of body
                + "FF04 44313031 "           // p1 string "D101"
                + "0202 0065");              // p2 integer 101

            XroutMessage message = XroutMessage.Parse(request, XroutMessageFraming.WithHeader);

            Assert.Equal(73, message.Service);
            Assert.Equal(2, message.Parameters.Count);

            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.Equal("D101", message.Parameters[0].AsText());

            uint systemNumber;
            Assert.Equal(2, message.Parameters[1].ParameterNumber);
            Assert.True(message.Parameters[1].TryGetUInt32(out systemNumber));
            Assert.Equal(101u, systemNumber);
        }

        /// <summary>
        /// An unroutable letter comes back with the status in the service byte and the body
        /// untouched.
        /// </summary>
        /// <remarks>
        /// We modelled this rule from header-only error replies. This is the first time it has been
        /// seen on a message with a real 58-byte body, and the body survives intact - which is how
        /// a sender matches the returned letter to what it sent.
        /// </remarks>
        [Fact]
        public void UnroutableLetter_ReturnsTheWholeBodyWithTheStatusInPlaceOfTheService()
        {
            byte[] reply = FromHex(
                "010C 003A "                                 // service byte replaced by 12 = XRNRO
                + "FF06 2A5846545241 "
                + "FE04 44313032 "
                + "F406 53595354454D "
                + "0D02 0000 "
                + "F810 22584D53472D434F50593A4241544322 "
                + "F704 53594D42 "
                + "0A02 0400 "
                + "0B02 0002");

            XroutMessage message = XroutMessage.Parse(reply, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutError.XRNRO, message.Service);
            Assert.Equal(8, message.Parameters.Count);
            Assert.Equal("*XFTRA", message.Parameters[0].AsText());
        }

        /// <summary>
        /// The remote-file-access request: an XSLET letter to *FA-SERVER whose application data is
        /// RAW BYTES after the parameter block, not tagged parameters.
        /// </summary>
        /// <remarks>
        /// Captured 2026-07-28 on the SINTRAN K image - it cannot be captured on L or M, where the
        /// File User is blocked by the revision-F gate. The contrast with *XFTRA matters: that
        /// server packs its whole specification into tagged parameters 8-13, this one declares a
        /// length covering only the two documented XSLET fields and appends opaque payload. Both
        /// are "the remainder of the message can contain data for the receiving task".
        /// See DOC/XMSG-FA-SERVER-REQUEST-CAPTURED-2026-07-28.md.
        /// </remarks>
        [Fact]
        public void XsletToFaServer_DeclaresOnlyTheLetterAndAppendsRawPayload()
        {
            byte[] request = FromHex(
                "1B41 0012 "                     // XSLET, length 18 - the LETTER only
                + "FF0A 2A46412D534552564552 "   // p1 string "*FA-SERVER"
                + "FE04 44313032 "               // p2 string "D102"
                + "07E2 0000 0006 6400");        // raw payload, outside the declared length

            XroutMessage message = XroutMessage.Parse(request, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSLET, message.Service);

            // Only the two documented fields are parameters. The trailing eight bytes are payload
            // for the receiving task and must NOT be mistaken for a malformed parameter.
            Assert.Equal(2, message.Parameters.Count);
            Assert.Equal("*FA-SERVER", message.Parameters[0].AsText());
            Assert.Equal("D102", message.Parameters[1].AsText());
        }

        /// <summary>
        /// The file server's letter comes back refused exactly as the file-transfer one did.
        /// </summary>
        /// <remarks>
        /// Worth pinning because the CONSOLE said something quite different - the File User retries
        /// for about a minute and then reports "NO ANSWER FROM REMOTE SYSTEM", which reads as if the
        /// request had been accepted and gone unanswered. It had not: XROUT refused it immediately,
        /// with the same status as file transfer. A console message is not evidence about the wire.
        /// </remarks>
        [Fact]
        public void FaServerLetter_IsRefusedWithTheSameStatusAsFileTransfer()
        {
            byte[] reply = FromHex(
                "1B0C 0012 "
                + "FF0A 2A46412D534552564552 "
                + "FE04 44313032 "
                + "07E2 0000 0006 6400");

            XroutMessage message = XroutMessage.Parse(reply, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutError.XRNRO, message.Service);
            Assert.Equal("*FA-SERVER", message.Parameters[0].AsText());
        }

        /// <summary>
        /// LIST-SYSTEMS probes each system by opening a letter to that system's <c>*TADADM</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Captured in <c>E:\Dev\Ronny\X25Emulator\pcap\li-syst-tad-103.pcapng</c>, frame 529. Frame
        /// 541 is byte-identical except that parameter 2 reads <c>"D102"</c>, so the command walks the
        /// systems it knows about and sends one of these per system.
        /// </para>
        /// <para>
        /// This is the THIRD server to confirm the XSLET parameter tagging - after <c>*FA-SERVER</c>
        /// and <c>*XFTRA</c> - and the first to exercise the word pad on parameter 1 rather than on a
        /// file name. <c>*TADADM</c> is 7 characters, so the parameter declares 7 and a pad byte
        /// follows; the pad is counted by the MESSAGE length, not by the parameter length.
        /// </para>
        /// <para>
        /// It also distinguishes LIST-SYSTEMS from LIST-ROUTING: the routing captures
        /// (<c>li-rout-103-tree.pcapng</c>, <c>li-rout-102-tree.pcapng</c>) carry no server name at
        /// all, so they never open a conversation like this one.
        /// </para>
        /// </remarks>
        [Fact]
        public void ListSystems_ProbesEachSystemViaItsTadadm()
        {
            byte[] request = FromHex(
                "0141 0014 "                    // XSLET, 20 bytes of body
                + "FF07 2A54414441444D 00 "     // p1  string "*TADADM", 7 chars plus a pad byte
                + "FE04 44313030 "              // p2  string "D100" - the system being probed
                + "0402 0001");                 // p4  integer 1

            XroutMessage message = XroutMessage.Parse(request, XroutMessageFraming.WithHeader);

            Assert.Equal((byte)XroutService.XSLET, message.Service);
            Assert.Equal(3, message.Parameters.Count);

            // The server being reached, with the pad stripped as framing rather than content.
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.Equal("*TADADM", message.Parameters[0].AsText());

            Assert.Equal(2, message.Parameters[1].ParameterNumber);
            Assert.Equal("D100", message.Parameters[1].AsText());

            // Parameter 4 was 1 on every probe in the capture. Meaning UNKNOWN - nothing varied it.
            Assert.Equal(4, message.Parameters[2].ParameterNumber);
            uint probeConstant;
            Assert.True(message.Parameters[2].TryGetUInt32(out probeConstant));
            Assert.Equal(1u, probeConstant);
        }

        /// <summary>
        /// The same probe addressed to a different system differs only in parameter 2.
        /// </summary>
        /// <remarks>
        /// Frames 529 and 541 of the same capture. This is what makes the reading "one letter per
        /// system" rather than "one letter that carries a list".
        /// </remarks>
        [Fact]
        public void ListSystemsProbe_DiffersOnlyInTheSystemName()
        {
            byte[] toD100 = FromHex("0141 0014 FF07 2A54414441444D 00 FE04 44313030 0402 0001");
            byte[] toD102 = FromHex("0141 0014 FF07 2A54414441444D 00 FE04 44313032 0402 0001");

            Assert.Equal(toD100.Length, toD102.Length);

            int differences = 0;
            for (int i = 0; i < toD100.Length; i++)
            {
                if (toD100[i] != toD102[i]) { differences++; }
            }

            // Exactly one byte: the last character of the system name.
            Assert.Equal(1, differences);

            XroutMessage a = XroutMessage.Parse(toD100, XroutMessageFraming.WithHeader);
            XroutMessage b = XroutMessage.Parse(toD102, XroutMessageFraming.WithHeader);

            Assert.Equal("D100", a.Parameters[1].AsText());
            Assert.Equal("D102", b.Parameters[1].AsText());
            Assert.Equal(a.Parameters[0].AsText(), b.Parameters[0].AsText());
        }

    }
}
