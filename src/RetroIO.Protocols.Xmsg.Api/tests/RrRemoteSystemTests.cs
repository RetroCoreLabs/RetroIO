//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// The request-response client reaches servers by name through the local directory only. These
    /// tests pin that asking for a named remote system is refused, instead of the system name being
    /// ignored and the letter going to whatever server has that name locally.
    /// </summary>
    public sealed class RrRemoteSystemTests
    {
        private static RrClient NewClient()
        {
            XroutDirectory directory = new XroutDirectory();
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            RrServer server = new RrServer(kernel, directory);
            server.Initialise("*DEMO");
            RrClient client = new RrClient(kernel, directory);
            client.Initialise();
            return client;
        }

        [Fact]
        public void RequestConnection_WithASystemName_IsRefused()
        {
            RrClient client = NewClient();
            int connection;

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => client.RequestConnection("OTHER", "*DEMO", Array.Empty<byte>(), out connection));

            Assert.Contains("not implemented", ex.Message);
        }

        [Fact]
        public void Select_WithASystemName_IsRefused()
        {
            RrClient client = NewClient();
            byte[] serverData;
            int connection;

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => client.Select("OTHER", "*DEMO", Array.Empty<byte>(), out serverData, out connection));

            Assert.Contains("not implemented", ex.Message);
        }

        [Fact]
        public void RequestConnection_WithoutASystemName_StillWorks()
        {
            RrClient client = NewClient();
            int connection;

            Assert.True(client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection).IsOk);
        }
    }
}
