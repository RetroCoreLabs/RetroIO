using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Proves the management-service builders emit the service and sub-service numbers appendix B
    /// sections 3.18 to 3.24 tabulate.
    /// </summary>
    public sealed class XroutManagementRequestTests
    {
        /// <summary>
        /// Opening a trace names the file as string parameter 1; closing takes no parameters.
        /// </summary>
        [Fact]
        public void TraceControl_UsesTheDocumentedShape()
        {
            XroutMessage open = XroutRequests.OpenTrace(1, "TRACE:DATA");
            Assert.Equal((byte)XroutService.XSTIN, open.Service);
            Assert.Single(open.Parameters);
            Assert.True(open.Parameters[0].IsString);
            Assert.Equal("TRACE:DATA", open.Parameters[0].AsText());

            XroutMessage close = XroutRequests.CloseTrace(1);
            Assert.Equal((byte)XroutService.XSTCL, close.Service);
            Assert.Empty(close.Parameters);
        }

        /// <summary>
        /// The trace-condition parameter is signed: positive enables, negative disables, zero
        /// disables everything.
        /// </summary>
        [Fact]
        public void DefineTraceConditions_CarriesASignedEventNumber()
        {
            XroutMessage enable = XroutRequests.DefineTraceConditions(1, 8);
            XroutMessage disable = XroutRequests.DefineTraceConditions(1, -8);
            XroutMessage all = XroutRequests.DefineTraceConditions(1, 0);

            Assert.Equal(new byte[] { 0x00, 0x08 }, enable.Parameters[0].Data);
            Assert.Equal(new byte[] { 0xFF, 0xF8 }, disable.Parameters[0].Data);
            Assert.Equal(new byte[] { 0x00, 0x00 }, all.Parameters[0].Data);
        }

        /// <summary>
        /// Every crash-information request selects its sub-service in parameter 1.
        /// </summary>
        [Fact]
        public void CrashInfo_SelectsTheSubserviceInParameterOne()
        {
            XroutMessage dump = XroutRequests.CrashInfo(2, XroutSetCrashInfoSubservice.XSDUX);

            Assert.Equal((byte)XroutService.XSSCI, dump.Service);
            Assert.Equal(1, dump.Parameters[0].ParameterNumber);
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutSetCrashInfoSubservice.XSDUX },
                dump.Parameters[0].Data);

            XroutMessage restart = XroutRequests.SetAutoRestart(2, true);
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutSetCrashInfoSubservice.XSDAR },
                restart.Parameters[0].Data);
            Assert.Equal(new byte[] { 0x00, 0x01 }, restart.Parameters[1].Data);
        }

        /// <summary>
        /// Omitting a file name clears the definition rather than sending an empty string.
        /// </summary>
        [Fact]
        public void FileDefinitions_ClearByOmission()
        {
            XroutMessage both = XroutRequests.DefineRestartFiles(3, "IN:SYMB", "OUT:SYMB");
            XroutMessage cleared = XroutRequests.DefineRestartFiles(3, null, null);

            Assert.Equal(3, both.Parameters.Count);
            Assert.Single(cleared.Parameters);

            XroutMessage dumps = XroutRequests.DefineDumpFiles(3, "A:DATA", "B:DATA", "C:DATA");
            Assert.Equal(4, dumps.Parameters.Count);

            XroutMessage partial = XroutRequests.DefineDumpFiles(3, "A:DATA", null, null);
            Assert.Equal(2, partial.Parameters.Count);
        }

        /// <summary>
        /// The attribute services dispatch on their own sub-service numbers.
        /// </summary>
        [Fact]
        public void AttributeServices_UseTheirSubserviceNumbers()
        {
            XroutMessage version = XroutRequests.GetXmsgVersion(4);
            Assert.Equal((byte)XroutService.XSGAT, version.Service);
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutGetAttributeSubservice.XSGXV },
                version.Parameters[0].Data);

            XroutMessage check = XroutRequests.CheckMagicNumber(4, new XmsgMagicNumber(0x00660245));
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutGetAttributeSubservice.XSCMG },
                check.Parameters[0].Data);
            Assert.Equal(new byte[] { 0x00, 0x66, 0x02, 0x45 }, check.Parameters[1].Data);

            XroutMessage name = XroutRequests.DeabbreviateName(4, "TAD");
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutGetAttributeSubservice.XSGCN },
                name.Parameters[0].Data);
            Assert.True(name.Parameters[1].IsString);
        }

        /// <summary>
        /// Adding and removing a friend system differ only in the sub-service number.
        /// </summary>
        [Fact]
        public void FriendSystem_AddAndRemoveShareAShape()
        {
            XroutMessage add = XroutRequests.SetFriendSystem(5, 103, true);
            XroutMessage remove = XroutRequests.SetFriendSystem(5, 103, false);

            Assert.Equal((byte)XroutService.XSDAT, add.Service);
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutDefineAttributeSubservice.XSDFR },
                add.Parameters[0].Data);
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutDefineAttributeSubservice.XSRFR },
                remove.Parameters[0].Data);
            Assert.Equal(add.Parameters[1].Data, remove.Parameters[1].Data);
        }

        /// <summary>
        /// The network-server enquiry carries the virtual system number to walk from.
        /// </summary>
        [Fact]
        public void NetworkServerInformation_CarriesTheVirtualSystem()
        {
            XroutMessage request = XroutRequests.GetNetworkServerInformation(6, 500);

            Assert.Equal((byte)XroutService.XSNSI, request.Service);
            Assert.Single(request.Parameters);
            Assert.Equal(new byte[] { 0x01, 0xF4 }, request.Parameters[0].Data);
        }

        /// <summary>
        /// The link enquiry carries the link number.
        /// </summary>
        [Fact]
        public void LinkInformation_CarriesTheLinkNumber()
        {
            XroutMessage request = XroutRequests.GetLinkInformation(6, 2);

            Assert.Equal((byte)XroutService.XSLIN, request.Service);
            Assert.Single(request.Parameters);
            Assert.Equal(new byte[] { 0x00, 0x02 }, request.Parameters[0].Data);
        }

        /// <summary>
        /// Enable and disable checksum differ ONLY in the sub-service, per ND's two tables.
        /// </summary>
        /// <remarks>
        /// Both are <c>XSDAT</c> with the system number in parameter 2; the whole difference is
        /// <c>XSECS</c> against <c>XSDCS</c>. Asserted as a pair so a copy-paste that sent "enable"
        /// for both cannot pass.
        /// </remarks>
        [Fact]
        public void DatagramChecksum_EnableAndDisableDifferOnlyInTheSubservice()
        {
            XroutMessage enable = XroutRequests.SetDatagramChecksum(6, 102, true);
            XroutMessage disable = XroutRequests.SetDatagramChecksum(6, 102, false);

            Assert.Equal((byte)XroutService.XSDAT, enable.Service);
            Assert.Equal((byte)XroutService.XSDAT, disable.Service);

            Assert.Equal(
                new byte[] { 0x00, (byte)XroutDefineAttributeSubservice.XSECS },
                enable.Parameters[0].Data);
            Assert.Equal(
                new byte[] { 0x00, (byte)XroutDefineAttributeSubservice.XSDCS },
                disable.Parameters[0].Data);

            // The system number is identical in both, and is the one ND restricts: sent to a REMOTE
            // XROUT it is accepted only when it equals the SENDING system's own number.
            Assert.Equal(new byte[] { 0x00, 0x66 }, enable.Parameters[1].Data);
            Assert.Equal(enable.Parameters[1].Data, disable.Parameters[1].Data);
        }

        /// <summary>
        /// A version-K reply carries no utilisation, and that is a version difference, not an error.
        /// </summary>
        /// <remarks>
        /// ND are explicit that a version-K system cannot return parameters 16 to 18. A caller that
        /// treated their absence as a malformed reply would report a fault against a perfectly
        /// healthy older machine, so the distinction is pinned here.
        /// </remarks>
        [Fact]
        public void LinkInformation_ShortReplyIsAVersionDifference()
        {
            XmsgLinkInformation versionK = new XmsgLinkInformation(0, 0, 0, 0, false);
            XmsgLinkInformation versionL = new XmsgLinkInformation(0, 4, 2, 3, true);

            Assert.False(versionK.HasUtilisation);
            Assert.True(versionL.HasUtilisation);
            Assert.Equal(4, versionL.TableSize);
            Assert.Equal(2, versionL.InUse);
            Assert.Equal(3, versionL.MaximumUsed);
        }

        /// <summary>
        /// Bit 2 of parameter 15 marks a link to a gateway.
        /// </summary>
        [Fact]
        public void LinkInformation_GatewayBitIsBitTwo()
        {
            Assert.Equal(0x0004, XmsgLinkInformation.GatewayLinkBit);

            Assert.True(new XmsgLinkInformation(0x0004, 0, 0, 0, true).IsGatewayLink);
            Assert.False(new XmsgLinkInformation(0x0002, 0, 0, 0, true).IsGatewayLink);
            Assert.False(new XmsgLinkInformation(0x0000, 0, 0, 0, true).IsGatewayLink);
        }

        /// <summary>
        /// Every management request round-trips through the wire form.
        /// </summary>
        [Fact]
        public void ManagementRequests_RoundTripBodyOnly()
        {
            XroutMessage original = XroutRequests.DefineDumpFiles(7, "A:DATA", "B:DATA", "C:DATA");
            byte[] bytes = original.ToArray(XroutMessageFraming.BodyOnly);

            XroutMessage parsed = XroutMessage.Parse(bytes, XroutMessageFraming.BodyOnly);

            Assert.Equal(original.Parameters.Count, parsed.Parameters.Count);
            for (int i = 0; i < original.Parameters.Count; i++)
            {
                Assert.Equal(original.Parameters[i].ParameterNumber, parsed.Parameters[i].ParameterNumber);
                Assert.Equal(original.Parameters[i].Data, parsed.Parameters[i].Data);
            }
        }

        /// <summary>
        /// An RR-LIB status recognises its appendix E code and reports context loss.
        /// </summary>
        [Fact]
        public void RrStatus_RecognisesAppendixECodes()
        {
            RrStatus timeout = new RrStatus((int)RrError.RRErnttm);
            Assert.False(timeout.IsOk);
            Assert.Equal(RrError.RRErnttm, timeout.RrCode);
            Assert.False(timeout.IsContextLost);

            RrStatus crashed = new RrStatus((int)RrError.RRERxcra);
            Assert.True(crashed.IsContextLost);

            RrStatus notRunning = new RrStatus((int)RrError.RRERxnru);
            Assert.True(notRunning.IsContextLost);

            // A status carrying a lower-layer failure is not an RR-LIB code.
            RrStatus fromXmsg = new RrStatus((int)XmsgError.XEIMA);
            Assert.Null(fromXmsg.RrCode);
            Assert.False(fromXmsg.IsContextLost);

            Assert.True(RrStatus.Ok.IsOk);
            Assert.Equal(RrError.Ok, RrStatus.Ok.RrCode);
        }
    }
}
