//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// A letter to port 0 (XSLET) reaches the server whose name is the letter's string parameter 1.
    /// </summary>
    /// <remarks>
    /// COSMOS Programmer Guide appendix B section 3.4: XSLET IN parameter 1 is the port or connection
    /// name. Before 2026-11-10 the node scanned the frame for the first printable run that starts with
    /// <c>*</c>, so a named port without a star (a chat server, a plug-in) could never be reached.
    /// </remarks>
    public sealed class LetterNameRoutingTests
    {
        // The captured connect letter (frame 0 of the connect-to capture, node 103 to node 102), also
        // used by XroutLetterFramingTests. Letter body: FF 07 "*TADADM" 00 FE 04 "D102".
        private const string CapturedLetterHex =
            "2113000E0066006700040400DA0D210086E4006600000067024504000041" +
            "0010FF072A54414441444D00FE0444313032";

        /// <summary>
        /// The captured letter is routed to *TADADM.
        /// </summary>
        [Fact]
        public void CapturedLetter_ReachesTheNamedServer()
        {
            XmsgServerHost host = new XmsgServerHost(0x0066);
            RecordingServer server = new RecordingServer("*TADADM");
            host.Register(server);

            host.Route(XmsgFrame.Parse(FromHex(CapturedLetterHex)));

            Assert.Equal(1, server.Handled);
        }

        /// <summary>
        /// The same letter with a seven-letter name that has no star reaches the server of that name.
        /// </summary>
        /// <remarks>
        /// Only the seven name bytes are changed ("*TADADM" to "CHATSRV"), so every length in the frame
        /// stays as captured. The old star scan found no name here and dropped the letter.
        /// </remarks>
        [Fact]
        public void LetterToANameWithoutAStar_ReachesTheNamedServer()
        {
            string hex = CapturedLetterHex.Replace("2A54414441444D", "43484154535256", StringComparison.Ordinal);
            XmsgServerHost host = new XmsgServerHost(0x0066);
            RecordingServer tad = new RecordingServer("*TADADM");
            RecordingServer chat = new RecordingServer("CHATSRV");
            host.Register(tad);
            host.Register(chat);

            host.Route(XmsgFrame.Parse(FromHex(hex)));

            Assert.Equal(1, chat.Handled);
            Assert.Equal(0, tad.Handled);
        }

        /// <summary>
        /// Counts the frames handed to it and answers nothing.
        /// </summary>
        private sealed class RecordingServer : IXmsgServer
        {
            public RecordingServer(string name)
            {
                Name = name;
            }

            public int Handled { get; private set; }

            public string Name { get; }

            public int LogicalPort
            {
                get { return 4; }
            }

            public ushort WirePort
            {
                get { return 0x0800; }
            }

            public int SessionCount
            {
                get { return 0; }
            }

            public int SessionCapacity
            {
                get { return 1; }
            }

            public bool AdvancesOutputOnAck
            {
                get { return false; }
            }

            public IReadOnlyList<XmsgFrame> Handle(XmsgFrame incoming, IXmsgServerTransport transport)
            {
                Handled++;
                return Array.Empty<XmsgFrame>();
            }

            public bool OwnsPort(ushort port)
            {
                return false;
            }

            public IReadOnlyList<XmsgFrame> DrainPending(IXmsgServerTransport transport)
            {
                return Array.Empty<XmsgFrame>();
            }

            public void NotifyAck(ushort remoteNode, ushort ackedFlags1)
            {
            }
        }
    }
}
