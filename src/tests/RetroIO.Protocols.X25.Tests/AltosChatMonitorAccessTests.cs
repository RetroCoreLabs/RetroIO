//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Services;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The read-only view of the chat that the Desktop chat monitor uses (step 4 of RetroX25
    /// docs/altos-chat-port-plan.md): users with place, nickname, origin, channel and connect time; channels with their
    /// user count and the protected flag; and a notice when something may have changed.
    /// </summary>
    [TestFixture]
    public class AltosChatMonitorAccessTests
    {
        private static readonly DateTime T0 = new DateTime(1987, 5, 29, 12, 0, 0);

        private static void Line(AltosChatEngine chat, int session, string text)
        {
            chat.Input(session, Encoding.ASCII.GetBytes(text + "\r"));
        }

        [Test]
        public void GetUsers_ListsEachJoinedUser_ByPlace_WithNickOriginChannelAndTime()
        {
            AltosChatActionList output = new AltosChatActionList();
            AltosChatEngine chat = new AltosChatEngine(new AltosChatSettings { WaitPause = TimeSpan.Zero }, output, output);
            chat.Start(T0);
            chat.Connect(1, "guest", "26240001");
            chat.Tick(T0.AddMinutes(1));
            chat.Connect(2, "guest", "26240002");
            Line(chat, 2, ".n bob");
            Line(chat, 2, ".c 5");

            List<AltosChatUserInfo> users = new List<AltosChatUserInfo>();
            Assert.That(chat.GetUsers(users), Is.EqualTo(2));
            Assert.That(users[0].Slot, Is.EqualTo(0));
            Assert.That(users[0].Nickname, Is.EqualTo("guest"));
            Assert.That(users[0].From, Is.EqualTo("26240001"), "without the padding to 16");
            Assert.That(users[0].Login, Is.EqualTo("guest"), "without the padding to 8");
            Assert.That(users[0].Channel, Is.EqualTo(0L));
            Assert.That(users[0].ConnectedAt, Is.EqualTo(T0));
            Assert.That(users[1].Slot, Is.EqualTo(1));
            Assert.That(users[1].Nickname, Is.EqualTo("bob"));
            Assert.That(users[1].Channel, Is.EqualTo(5L));
            Assert.That(users[1].ConnectedAt, Is.EqualTo(T0.AddMinutes(1)));

            // Read only: nobody was told anything.
            output.Reset();
            chat.GetUsers(users);
            Assert.That(output.Items.Count, Is.EqualTo(0));
        }

        [Test]
        public void GetChannels_ListsChannelsInUse_InOrder_WithCountsAndTheProtectedFlag()
        {
            AltosChatActionList output = new AltosChatActionList();
            AltosChatEngine chat = new AltosChatEngine(new AltosChatSettings { WaitPause = TimeSpan.Zero }, output, output);
            chat.Start(T0);
            chat.Connect(1, "guest", "1");
            chat.Connect(2, "guest", "2");
            chat.Connect(3, "guest", "3");
            Line(chat, 2, ".c 7");
            Line(chat, 2, ".p secret");
            Line(chat, 3, ".c 3");

            List<AltosChatChannelInfo> channels = new List<AltosChatChannelInfo>();
            Assert.That(chat.GetChannels(channels), Is.EqualTo(3));
            Assert.That(channels[0].Channel, Is.EqualTo(0L));
            Assert.That(channels[0].Users, Is.EqualTo(1));
            Assert.That(channels[0].IsProtected, Is.False);
            Assert.That(channels[1].Channel, Is.EqualTo(3L));
            Assert.That(channels[1].IsProtected, Is.False);
            Assert.That(channels[2].Channel, Is.EqualTo(7L));
            Assert.That(channels[2].IsProtected, Is.True);

            // The protection goes when the channel empties (CHAT 3.1 serv.c:107-117), and so does the channel.
            Line(chat, 2, ".c 0");
            chat.GetChannels(channels);
            Assert.That(channels.Count, Is.EqualTo(2));
            Assert.That(channels[0].Channel, Is.EqualTo(0L));
            Assert.That(channels[0].Users, Is.EqualTo(2));
            Assert.That(channels[1].Channel, Is.EqualTo(3L));
        }

        [Test]
        public void TheService_SaysWhenSomethingChanged_AndHandsOutTheSameView()
        {
            List<TadOutputEventArgs> sent = new List<TadOutputEventArgs>();
            DateTime now = T0;
            using ChatProtocolService chat = new ChatProtocolService(NullLogger.Instance, sent.Add, "26245890040004",
                new AltosChatSettings { WaitPause = TimeSpan.FromSeconds(3) }, () => now);
            int changes = 0;
            chat.Changed += (s, e) => changes++;

            byte[] pad = { 0x01, 0x00, 0x00, 0x00 };
            Assert.That(chat.HandleIncomingCall(4, "26245890040004", "26240001", pad, Array.Empty<byte>()), Is.True);
            Assert.That(changes, Is.EqualTo(1), "a call");

            List<AltosChatUserInfo> users = new List<AltosChatUserInfo>();
            Assert.That(chat.GetUsers(users), Is.EqualTo(0), "still in the pause after Waiting for connection.");

            chat.Tick();
            Assert.That(changes, Is.EqualTo(1), "a tick that joins nobody is not a change");
            now = T0.AddSeconds(3);
            chat.Tick();
            Assert.That(changes, Is.EqualTo(2), "the join");
            Assert.That(chat.GetUsers(users), Is.EqualTo(1));
            Assert.That(users[0].From, Is.EqualTo("26240001"));

            chat.HandleData(4, Encoding.ASCII.GetBytes(".n carol\r"), false);
            Assert.That(changes, Is.EqualTo(3), "a line");
            chat.GetUsers(users);
            Assert.That(users[0].Nickname, Is.EqualTo("carol"));

            chat.HandleClear(4, 0, 0);
            Assert.That(changes, Is.EqualTo(4), "a clear");
            Assert.That(chat.GetUsers(users), Is.EqualTo(0));

            List<AltosChatChannelInfo> channels = new List<AltosChatChannelInfo>();
            Assert.That(chat.GetChannels(channels), Is.EqualTo(0));
        }
    }
}
