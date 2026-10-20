//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins how <c>SintranPvcServer</c> treats the first byte of the call user data.
    /// </summary>
    /// <remarks>
    /// Sources read for this rule:
    /// ITU-T X.29 (12/97) clause 4.2.1 says the first octet of the call user data is the protocol
    /// identifier: bits 8 and 7 = 00 for ITU-T use, and bits 6 to 1 = 000001 means PAD messages;
    /// octets 2 to 4 are all zero. So 0x01 means PAD, not "routing".
    /// The recordings in the X25Emulator pcap folder (214 LAPB I frames listed in the .md traces)
    /// all begin with the SINTRAN routing marker 0x21 0x13 and carry no X.25 Call Request, so they
    /// give no call user data rule at all. The values 0x02, 0x03 and 0xDD appear in no source.
    /// </remarks>
    [TestFixture]
    public class SintranPvcServerCallUserDataTests : TestBase
    {
        private const ushort Lcn = 9;

        private SintranPvcServer _server = null!;
        private List<X25PacketEventArgs> _sent = null!;

        public override void Setup()
        {
            base.Setup();
            _server = new SintranPvcServer(Logger);
            _server.Initialize();
            _sent = new List<X25PacketEventArgs>();
            _server.PacketGenerated += (s, e) => { lock (_sent) { _sent.Add(e); } };
        }

        public override void TearDown()
        {
            _server.Shutdown();
            base.TearDown();
        }

        private void Call(string called, byte[] cud)
        {
            byte[] call = X25PacketBuilder.CreateCallRequest(Lcn, called, "100", null, cud);
            _server.OnX25Event(new X25PacketEventArgs
            {
                Packet = new X25Packet(call) { LCN = Lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            });
        }

        private X25PacketType LastSentType()
        {
            lock (_sent)
            {
                Assert.That(_sent.Count, Is.GreaterThan(0), "no reply was generated");
                return _sent[_sent.Count - 1].Packet.PacketType;
            }
        }

        [TestCase((byte)0x02)]
        [TestCase((byte)0x03)]
        [TestCase((byte)0xDD)]
        [TestCase((byte)0x55)]
        public void CallUserDataFirstByteWithNoSource_IsClearedNotRouted(byte first)
        {
            Call("500", new byte[] { first });

            Assert.That(_server.GetActiveHandlers().ContainsKey(Lcn), Is.False);
            Assert.That(LastSentType(), Is.EqualTo(X25PacketType.ClearRequest));
        }

        [Test]
        public void X29PadProtocolIdentifier_IsNotRoutedToTheRoutingHandler()
        {
            // X.29 4.2.1: 01 00 00 00 = PAD. No PAD handler is registered in this server.
            Call("500", new byte[] { 0x01, 0x00, 0x00, 0x00 });

            Assert.That(_server.GetActiveHandlers().ContainsKey(Lcn), Is.False);
            Assert.That(LastSentType(), Is.EqualTo(X25PacketType.ClearRequest));
        }

        [Test]
        public void EmptyCallUserData_StillGetsTheRoutingHandler()
        {
            Call("500", Array.Empty<byte>());

            Assert.That(_server.GetActiveHandlers().ContainsKey(Lcn), Is.True);
            Assert.That(LastSentType(), Is.EqualTo(X25PacketType.CallAccepted));
        }
    }
}
