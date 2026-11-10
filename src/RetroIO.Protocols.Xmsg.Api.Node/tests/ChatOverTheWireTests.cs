//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Node.Tests
{
    /// <summary>
    /// The C# chat server answers a join that arrives off the wire, through the node's server list
    /// and the bridge, the way the SINTRAN CHAT client sends it.
    /// </summary>
    /// <remarks>
    /// The SINTRAN client (NDInsight SINTRAN/XMSG/SINTRAN-CHAT/CHAT.PLNC, sendJoin) sends its join as
    /// a letter to the service name <c>*CHAT</c>, with the chat message behind the XSLET header.
    /// NOT VERIFIED: the frame around the letter is our own frame builder's, not a capture of CHAT
    /// on one machine joining a server on another. No such capture exists yet.
    /// </remarks>
    public sealed class ChatOverTheWireTests
    {
        /// <summary>
        /// A join from system 100 comes back as a welcome addressed to system 100.
        /// </summary>
        [Fact]
        public void JoinFromTheWire_IsAnsweredWithAWelcome()
        {
            XmsgKernelServer bridge = XmsgKernelServer.Create(102, 0x1111, "*KERNEL", 4);
            XroutDirectory directory = new XroutDirectory();
            bridge.Directory = directory;

            ChatServer chat = new ChatServer(bridge.Kernel, directory);
            Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

            XmsgServerHost node = new XmsgServerHost(102);
            node.Register(bridge);

            XmsgMagicNumber client = XmsgMagicNumber.Create(100, 5, 43);
            ChatMessage join = new ChatMessage(ChatMessageKind.Join, "RONNY", string.Empty);
            byte[] data = new byte[join.ByteCount];
            join.Encode(data);

            XmsgFrame letter = new XmsgFrameBuilder()
                .Between(102, 100)
                .WithDatagramSequence(1)
                .WithProtocol(SintranProtocolId.Tad)
                .WithFlags2(0x0108)
                .WithSubHeaderControl(0x10, 0x86, 0x84)
                .WithEndpoints(102, 0, 100, client.PortWord)
                .WithControlService(0x04000041)
                .Build();
            letter.TrailingBytes = XroutLetter.Build(123, "*CHAT", data);

            node.Route(letter);
            Assert.Equal(1, chat.Poll());

            IReadOnlyList<XmsgFrame> sent = node.DrainPending();
            Assert.Single(sent);
            Assert.Equal(100, sent[0].SubHeader!.DestinationSystem);
            Assert.Equal(client.PortWord, sent[0].SubHeader!.DestinationPort);

            // Laid out as D100's own Welcome was measured on the wire (RetroXmsg ChatLoadRun.cs):
            // frame flags 0x82, role 0x84, XMCSM = the data's length, the data at offset 28.
            byte[] raw = sent[0].ToArray();
            Assert.Equal(0x82, sent[0].SubHeader!.FrameFlags);
            Assert.Equal(0x84, sent[0].SubHeader!.Role);
            Assert.True(raw.Length > 28);
            Assert.Equal(raw.Length - 28, sent[0].SubHeader!.Xmcsm);
            ChatMessage welcome;
            Assert.True(ChatMessage.TryDecode(new ReadOnlySpan<byte>(raw, 28, raw.Length - 28), out welcome));
            Assert.Equal(ChatMessageKind.Welcome, welcome.Kind);
        }
    }
}
