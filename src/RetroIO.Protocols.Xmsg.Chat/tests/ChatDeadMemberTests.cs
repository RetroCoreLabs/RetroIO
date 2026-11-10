//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// A member whose program dies without leaving is found and removed, as the SINTRAN server does
    /// it (CHATSV.PLNC, measured on D100 2026-08-18): the server sends secure, the message to the dead
    /// port comes back, and the member it names leaves the room.
    /// </summary>
    /// <remarks>
    /// Found running RetroServer live on 2026-11-10: a <c>retro-chat</c> that ended before it was let
    /// in kept the nickname RONNY taken until the server restarted.
    /// </remarks>
    public sealed class ChatDeadMemberTests
    {
        private const string Service = "*CHAT";

        /// <summary>
        /// RONNY's port closes without a Leave. When KARI next speaks, the message to RONNY comes back,
        /// the room is told RONNY left, the seat is free, and the nickname can be used again.
        /// </summary>
        [Fact]
        public void AMemberWhosePortClosed_IsRemovedWhenAMessageToThemComesBack()
        {
            XroutDirectory directory = new XroutDirectory();
            XmsgKernel kernel = new XmsgKernel(100, 0x1111, null);
            ChatServer server = new ChatServer(kernel, directory);
            Assert.Equal(XroutError.XRSOK, server.Open(Service, 4, "welcome"));

            // Port 1 is the server's; RONNY's client opens port 2 when it joins.
            ChatClient ronny = new ChatClient(kernel, directory, "RONNY");
            Assert.Equal(XroutError.XRSOK, ronny.Join(Service));
            server.Poll();
            ronny.Poll();
            Assert.True(ronny.IsJoined);

            ChatClient kari = new ChatClient(kernel, directory, "KARI");
            Assert.Equal(XroutError.XRSOK, kari.Join(Service));
            server.Poll();
            kari.Poll();
            Assert.True(kari.IsJoined);
            Assert.Equal(2, server.MemberCount);

            // RONNY's program dies: the port closes, no Leave is sent.
            kernel.ClosePort(new XmsgPortNumber(2));

            // KARI speaks. The broadcast to RONNY comes back, and the next Poll reaps RONNY.
            Assert.True(kari.Say("anyone there?"));
            server.Poll();
            server.Poll();

            Assert.Equal(1, server.MemberCount);
            IReadOnlyList<ChatMessage> heard = kari.Poll();
            bool toldLeft = false;
            for (int i = 0; i < heard.Count; i++)
            {
                toldLeft |= heard[i].Kind == ChatMessageKind.Left && heard[i].Nickname == "RONNY";
            }

            Assert.True(toldLeft);

            // The nickname is free again.
            ChatClient again = new ChatClient(kernel, directory, "RONNY");
            Assert.Equal(XroutError.XRSOK, again.Join(Service));
            server.Poll();
            again.Poll();
            Assert.True(again.IsJoined);
        }
    }
}
