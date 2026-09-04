using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.ListRouting;

using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Proves the XROUT request builders emit the service numbers and parameter numbers tabulated
    /// in appendix B of the COSMOS Programmer Guide, and that replies decode back.
    /// </summary>
    public sealed class XroutRequestTests
    {
        /// <summary>
        /// Naming a port carries the name as string parameter 1.
        /// </summary>
        [Fact]
        public void NamePort_UsesStringParameterOne()
        {
            XroutMessage message = XroutRequests.NamePort(0x11, "*TADADM");

            Assert.Equal(0x11, message.Serial);
            Assert.Equal((byte)XroutService.XSNAM, message.Service);
            Assert.Single(message.Parameters);
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.True(message.Parameters[0].IsString);
            Assert.Equal("*TADADM", message.Parameters[0].AsText());
        }

        /// <summary>
        /// A connection port carries name, maximum connections and uniqueness as parameters 1 to 3.
        /// </summary>
        [Fact]
        public void CreateConnectionPort_UsesParametersOneToThree()
        {
            XroutMessage message = XroutRequests.CreateConnectionPort(1, "*FA-SERVER", 4, 1);

            Assert.Equal((byte)XroutService.XSCRS, message.Service);
            Assert.Equal(3, message.Parameters.Count);
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.Equal(2, message.Parameters[1].ParameterNumber);
            Assert.Equal(3, message.Parameters[2].ParameterNumber);
            Assert.False(message.Parameters[1].IsString);
        }

        /// <summary>
        /// Our builders reproduce, byte for byte, the XSCRS registrations captured from a running
        /// SINTRAN - including the two shorter forms the real servers actually use.
        /// </summary>
        /// <remarks>
        /// Bytes from a MON 200 XFWRI trace, 2026-07-27. *XFTRA sends the name alone; the file
        /// access servers add an initial count of zero and no uniqueness flag. Evidence:
        /// DOC/XMSG-XSCRS-CONNECTION-PORTS-CAPTURED-2026-07-27.md.
        /// </remarks>
        [Fact]
        public void CreateConnectionPort_ReproducesTheCapturedNameOnlyRegistration()
        {
            XroutMessage message = XroutRequests.CreateConnectionPort(0x53, "*XFTRA");

            Assert.Equal(
                FromHex("5350 0008 FF06 2A5846545241"),
                message.ToArray(XroutMessageFraming.WithHeader));
        }

        /// <summary>
        /// The file-access form: name plus an initial count of zero, no uniqueness flag, with the
        /// pad byte that even-aligns the integer block after an odd-length name.
        /// </summary>
        [Fact]
        public void CreateConnectionPort_ReproducesTheCapturedZeroCountRegistration()
        {
            XroutMessage message = XroutRequests.CreateConnectionPort(0x53, "*FA-FSA", 0);

            Assert.Equal(
                FromHex("5350 000E FF07 2A46412D465341 00 0202 0000"),
                message.ToArray(XroutMessageFraming.WithHeader));
        }

        /// <summary>
        /// The +1 that adds a single service point, as every captured server issues it.
        /// </summary>
        [Fact]
        public void AdjustFreeConnections_ReproducesTheCapturedIncrement()
        {
            XroutMessage message = XroutRequests.AdjustFreeConnections(0x54, 1);

            Assert.Equal(
                FromHex("5451 0004 0102 0001"),
                message.ToArray(XroutMessageFraming.WithHeader));
        }

        /// <summary>
        /// Our XSGIN builder reproduces the captured even-length lookup exactly.
        /// </summary>
        /// <remarks>
        /// Bytes from the XMSG command program, 2026-07-27. Evidence:
        /// DOC/XMSG-XSGIN-NAME-LOOKUP-CAPTURED-2026-07-27.md.
        /// </remarks>
        [Fact]
        public void GetNameInformation_ReproducesTheCapturedSystemNameLookup()
        {
            XroutMessage message = XroutRequests.GetNameInformation(0x01, "D102");

            Assert.Equal(
                FromHex("0152 0006 FF04 44313032"),
                message.ToArray(XroutMessageFraming.WithHeader));
        }

        /// <summary>
        /// With an ODD-length name as the last parameter, we emit no trailing pad byte and the
        /// XMSG command program does. Both forms are accepted by XROUT.
        /// </summary>
        /// <remarks>
        /// This is a real difference between writers, not a bug on either side, and it is worth a
        /// test so nobody "fixes" one into the other by accident:
        ///  - XMSG-COMMAND writes <c>*TADADM</c> as <c>FF 07 7 chars 00</c>, length 10 - padded
        ///    to a word boundary even though nothing follows.
        ///  - TADADM itself writes its XSNAM registration as <c>FF 07 7 chars</c>, length 8,
        ///    with no pad (see XMSG-XSCRS-CONNECTION-PORTS-CAPTURED-2026-07-27.md, section 6).
        /// Both registered and resolved correctly on the same machine, so the trailing pad is the
        /// caller's choice. We match TADADM. Padding BETWEEN parameters is not optional and we do
        /// emit it - see the XSCRS round trip above.
        /// </remarks>
        [Fact]
        public void GetNameInformation_OmitsTheOptionalTrailingPadByte()
        {
            XroutMessage message = XroutRequests.GetNameInformation(0x01, "*TADADM");
            byte[] ours = message.ToArray(XroutMessageFraming.WithHeader);

            byte[] captured = FromHex("0152 000A FF07 2A54414441444D 00");

            Assert.Equal(captured.Length - 1, ours.Length);
            for (int i = 4; i < ours.Length; i++)
            {
                // Identical from the first parameter byte on; only the header length and the
                // absent trailing pad differ.
                Assert.Equal(captured[i], ours[i]);
            }
        }

        /// <summary>
        /// A letter without a system name omits parameter 2 entirely rather than sending it empty.
        /// </summary>
        [Fact]
        public void SendLetter_WithoutSystemName_OmitsParameterTwo()
        {
            XroutMessage message = XroutRequests.SendLetter(2, "*TADADM", null, null);

            Assert.Equal((byte)XroutService.XSLET, message.Service);
            Assert.Single(message.Parameters);
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
        }

        /// <summary>
        /// A letter carries the system name as parameter 2, the local-area flag as parameter 4, and
        /// any caller payload after them.
        /// </summary>
        [Fact]
        public void SendLetter_WithSystemAndPayload_OrdersParameters()
        {
            XroutMessage message = XroutRequests.SendLetter(
                3,
                "*TADADM",
                "D100",
                1,
                XroutParameter.Text(5, "SYSTEM"));

            Assert.Equal(4, message.Parameters.Count);
            Assert.Equal(1, message.Parameters[0].ParameterNumber);
            Assert.Equal(2, message.Parameters[1].ParameterNumber);
            Assert.Equal(4, message.Parameters[2].ParameterNumber);
            Assert.Equal(5, message.Parameters[3].ParameterNumber);
        }

        /// <summary>
        /// Clearing a remote name is expressed by omitting the system number, not by sending zero.
        /// </summary>
        [Fact]
        public void DefineRemoteName_WithoutNumber_ClearsByOmission()
        {
            XroutMessage defined = XroutRequests.DefineRemoteName(4, "MAIL-HANDLER", 103);
            XroutMessage cleared = XroutRequests.DefineRemoteName(4, "MAIL-HANDLER", null);

            Assert.Equal(2, defined.Parameters.Count);
            Assert.Single(cleared.Parameters);
        }

        /// <summary>
        /// A magic number is carried as a four-byte big-endian integer parameter.
        /// </summary>
        [Fact]
        public void GetNameFromMagic_EncodesFourByteBigEndian()
        {
            XroutMessage message = XroutRequests.GetNameFromMagic(5, new XmsgMagicNumber(0x12345678));

            Assert.Equal((byte)XroutService.XSGNM, message.Service);
            XroutParameter parameter = message.Parameters[0];
            Assert.False(parameter.IsString);
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, parameter.Data);

            uint decoded;
            Assert.True(parameter.TryGetUInt32(out decoded));
            Assert.Equal(0x12345678u, decoded);
        }

        /// <summary>
        /// Every built request serialises and parses back to the same service and parameters.
        /// </summary>
        [Fact]
        public void Request_RoundTripsThroughTheWireFormat()
        {
            XroutMessage original = XroutRequests.GetRoutingInformation(6, 102);
            byte[] bytes = original.ToArray();

            XroutMessage parsed = XroutMessage.Parse(bytes);

            Assert.Equal(original.Serial, parsed.Serial);
            Assert.Equal(original.Service, parsed.Service);
            Assert.Equal(original.Parameters.Count, parsed.Parameters.Count);
            Assert.Equal(original.Parameters[0].Data, parsed.Parameters[0].Data);
        }

        /// <summary>
        /// A reply is recognised as successful when XROUT wrote a zero status over the service byte.
        /// </summary>
        [Fact]
        public void Reply_StatusZero_IsSuccess()
        {
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(7)
                .WithServiceByte((byte)XroutError.XRSOK)
                .AddString(2, "*TADADM")
                .Build();

            XroutReply reply = new XroutReply(message);

            Assert.True(reply.IsSuccess);
            Assert.False(reply.IsUserStatus);
            Assert.Equal(7, reply.Serial);

            string name;
            Assert.True(reply.TryGetString(2, out name));
            Assert.Equal("*TADADM", name);
        }

        /// <summary>
        /// A status byte with bit 7 set is a user status, not an XROUT error.
        /// </summary>
        [Fact]
        public void Reply_HighBitStatus_IsUserStatus()
        {
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(8)
                .WithServiceByte(0x81)
                .Build();

            XroutReply reply = new XroutReply(message);

            Assert.True(reply.IsUserStatus);
            Assert.False(reply.IsSuccess);
        }

        /// <summary>
        /// The four-parameter routing reply decodes into the existing routing-table entry type,
        /// including the split of the network-info word into wide-area and hop counts.
        /// </summary>
        [Fact]
        public void Reply_RoutingInformation_DecodesIntoRoutingTableEntry()
        {
            // Network info 0x0103 = one wide-area hop in the left byte, three hops in the right.
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(9)
                .WithServiceByte((byte)XroutError.XRSOK)
                .AddInteger16(1, 103)
                .AddInteger16(2, (ushort)XroutConnectionType.Via)
                .AddInteger16(3, 100)
                .AddInteger16(4, 0x0103)
                .Build();

            XroutReply reply = new XroutReply(message);

            RoutingTableEntry entry;
            Assert.True(reply.TryGetRoutingEntry(out entry));
            Assert.Equal(103, entry.System);
            Assert.Equal(XroutConnectionType.Via, entry.ConnectionType);
            Assert.Equal(100, entry.ExtraInfo);
            Assert.Equal(3, entry.Hops);
            Assert.Equal(1, entry.Wans);
        }
    }
}
