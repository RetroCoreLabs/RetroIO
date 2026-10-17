//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.Servers.Tad;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Stopping the server must log its users out rather than vanish on them.
    /// </summary>
    /// <remarks>
    /// <para><b>The failure this guards, and it was not cheap</b></para>
    /// <para>
    /// A TAD session is live state on BOTH sides. On 2026-08-17 the runner was stopped with three
    /// sessions open and D100's XMSG died outright - <c>ERROR 46 ... XMSG FATAL ERROR - INTERNAL
    /// ERROR OR INCONSISTENCY, XMSG ERROR CODE: 27</c> - taking XROUT with it, because it was left
    /// holding half a session each. Recovery was an emulator restart.
    /// </para>
    /// <para>
    /// The teardown frames are the SAME ladder a user's own logout produces, so this adds no new
    /// wire behaviour; it only makes shutdown take the path that already works.
    /// </para>
    /// </remarks>
    public sealed class TadShutdownTeardownTests
    {
        /// <summary>
        /// Every open session is torn down, and the sessions are gone afterwards.
        /// </summary>
        [Fact]
        public void ShutdownAllSessions_TearsDownEveryOpenSession()
        {
            TadServer server = new TadServer(() => new DateTime(1998, 8, 17, 10, 0, 0));
            XmsgServerHost host = new XmsgServerHost(19999);
            host.Register(server);

            OpenSession(host, clientPort: 0x0211);
            OpenSession(host, clientPort: 0x02C6);
            Assert.Equal(2, server.SessionCount);

            IReadOnlyList<XmsgFrame> teardown = server.ShutdownAllSessions(host);

            Assert.True(
                teardown.Count > 0,
                "stopping with two sessions open produced NO teardown frames - the peer is left"
                    + " holding both sessions, which is what killed D100's XMSG.");

            Assert.Equal(0, server.SessionCount);
        }

        /// <summary>
        /// With nothing open there is nothing to say, and no frames are produced.
        /// </summary>
        [Fact]
        public void ShutdownAllSessions_IsSilentWhenNoSessionsAreOpen()
        {
            TadServer server = new TadServer(() => new DateTime(1998, 8, 17, 10, 0, 0));
            XmsgServerHost host = new XmsgServerHost(19999);
            host.Register(server);

            Assert.Empty(server.ShutdownAllSessions(host));
        }

        /// <summary>
        /// A null transport is refused at the call rather than part-way through the walk.
        /// </summary>
        [Fact]
        public void ShutdownAllSessions_RefusesANullTransport()
        {
            TadServer server = new TadServer(() => new DateTime(1998, 8, 17, 10, 0, 0));

            Assert.Throws<ArgumentNullException>(() => server.ShutdownAllSessions(null!));
        }

        /// <summary>
        /// Drives a connect letter through the host so the server opens a real session.
        /// </summary>
        /// <param name="host">
        /// The server host to deliver through.
        /// </param>
        /// <param name="clientPort">
        /// The client's source port; distinct ports give distinct sessions.
        /// </param>
        private static void OpenSession(XmsgServerHost host, ushort clientPort)
        {
            TadConnectClient client = new TadConnectClient(100, 19999, clientPort, seed: 0x5B);
            host.Route(client.BuildConnect("D19999"));
        }
    }
}
