//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The serial incoming call, end to end: bytes on a mock serial line, through HDLC framing, the
    /// frame check sequence and LAPB, to the X.25 engine of the line, the router and the answer back
    /// out as a LAPB I frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frames that go in are built by the helpers in this file, from the text of ITU-T X.25
    /// (10/96) and not by the code under test: the CRC is a bit-at-a-time version of the generator
    /// in clause 2.2.7.4 (checked against the published check value of CRC-16/X-25, 0x906E for the
    /// text 123456789), the octet stuffing is written out here, and the addresses and control
    /// octets are written as numbers. A frame the transport sends is taken apart by the same helpers.
    /// So the transport is not graded against itself.
    /// </para>
    /// <para>
    /// X.25 passages used: 2.2.1 (flags), 2.2.7.4 (FCS), 2.3.2 (control field formats), 2.3.5.3
    /// (invalid frames are discarded with no action), 2.4.2 (addresses A and B for the DTE and DCE),
    /// 2.4.4.1 (either side may start the set-up), 2.4.4.3 (disconnection).
    /// </para>
    /// <para>
    /// The line is a mock serial port of the physical layer; no hardware is used.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class SerialIncomingCallTests
    {
        private const string LocalMachineId = "200";
        private const string CallingAddress = "300";
        private const string InterfaceName = "ser0";
        private const int MockPort = 1;

        // X.25 2.4.2, coded 11000000 and 10000000 in transmission order, which is 0x03 and 0x01.
        private const byte AddressA = 0x03;
        private const byte AddressB = 0x01;

        // X.25 Table 2-4/2-5 control fields with the poll or final bit set, as bytes.
        private const byte Sabm = 0x3F;
        private const byte Ua = 0x73;
        private const byte Disc = 0x53;

        private ProtocolRuntime _runtime = null!;
        private SerialTransport _transport = null!;
        private readonly List<byte[]> _written = new List<byte[]>();
        private readonly object _lock = new object();

        [SetUp]
        public void SetUp()
        {
            // NUnit uses one fixture object for every test, so the capture is cleared here.
            lock (_lock)
            {
                _written.Clear();
            }
        }

        [TearDown]
        public void TearDown()
        {
            _runtime?.Dispose();
        }

        // ------------------------------------------------------------------ the rig

        /// <summary>
        /// Builds a runtime with one mock serial interface, starts it, and captures every byte the
        /// runtime writes to the line.
        /// </summary>
        private void Build(SerialX25LinkOptions? options)
        {
            SerialInterfaceConfig serial = new SerialInterfaceConfig
            {
                Name = InterfaceName,
                PortName = "MOCK" + MockPort,
                RemoteMachineId = "REMOTE1",
                X25Link = options,
            };

            RuntimeConfig config = new RuntimeConfig { TestMode = true };
            config.X25.LocalMachineId = LocalMachineId;
            config.Routing.Enabled = true;
            config.Routing.SerialInterfaces.Add(serial);

            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
            _runtime.PhysicalLayer.MockSerialTransmit += (s, e) =>
            {
                lock (_lock)
                {
                    _written.Add(e.Data);
                }
            };

            _runtime.PhysicalLayer.InitializeSerialPorts(config.Routing.SerialInterfaces);
            Assert.That(_runtime.PhysicalLayer.Start(), Is.True, "the mock serial port must start");
            _transport = (SerialTransport)_runtime.TransportInterfaces[InterfaceName];

            // The runtime constructor tries to start its transports before any host can open the
            // physical layer's serial ports, so this one is in Error with "interface not active".
            // A host that opens the ports afterwards has to restart it, as done here. (Nothing in
            // the runtime calls InitializeSerialPorts; see the report for this change.)
            _transport.Stop();
            Assert.That(_transport.Start(), Is.True, "the serial transport must start");
        }

        private static SerialX25LinkOptions Dce(bool initiate = false)
        {
            return new SerialX25LinkOptions(SerialX25LinkRole.Dce, initiate);
        }

        private static SerialX25LinkOptions Dte(bool initiate = false)
        {
            return new SerialX25LinkOptions(SerialX25LinkRole.Dte, initiate);
        }

        private void Inject(byte[] wire)
        {
            _runtime.PhysicalLayer.InjectMockSerialData(MockPort, wire);
        }

        /// <summary>
        /// Every frame body (address, control, information) the runtime has written to the line so
        /// far, taken apart with this file's own un-stuffing and CRC. A frame with a bad CRC fails
        /// the test, because the transport must never send one.
        /// </summary>
        private List<byte[]> SentFrames()
        {
            List<byte[]> frames = new List<byte[]>();
            lock (_lock)
            {
                for (int i = 0; i < _written.Count; i++)
                {
                    frames.AddRange(SplitFrames(_written[i]));
                }
            }

            return frames;
        }

        private List<byte[]> SentInformationPackets()
        {
            List<byte[]> packets = new List<byte[]>();
            List<byte[]> frames = SentFrames();
            for (int i = 0; i < frames.Count; i++)
            {
                // Control bit 0 clear is an I frame (X.25 2.3.2); the packet follows address and control.
                if (frames[i].Length > 2 && (frames[i][1] & 0x01) == 0)
                {
                    byte[] packet = new byte[frames[i].Length - 2];
                    Buffer.BlockCopy(frames[i], 2, packet, 0, packet.Length);
                    packets.Add(packet);
                }
            }

            return packets;
        }

        private int CountPacketsOfType(byte pti)
        {
            int count = 0;
            List<byte[]> packets = SentInformationPackets();
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i].Length > 2 && packets[i][2] == pti)
                {
                    count++;
                }
            }

            return count;
        }

        // ------------------------------------------------------------------ X.25 2.2 written out here

        /// <summary>
        /// The FCS of clause 2.2.7.4 computed bit by bit: initial value all ones, polynomial
        /// x16 + x12 + x5 + 1 (0x8408 reflected), result complemented. The low octet is sent first.
        /// </summary>
        private static ushort Crc(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
                }
            }

            return (ushort)~crc;
        }

        /// <summary>
        /// Flag, the body and its FCS with control escape stuffing, flag. Flag 0x7E and escape 0x7D
        /// are escaped, as the text of RFC 1662 section 4.2 and X.25 2.2.2 require.
        /// </summary>
        private static byte[] Wire(byte[] body)
        {
            byte[] withFcs = new byte[body.Length + 2];
            Buffer.BlockCopy(body, 0, withFcs, 0, body.Length);
            ushort crc = Crc(body, body.Length);
            withFcs[body.Length] = (byte)(crc & 0xFF);
            withFcs[body.Length + 1] = (byte)(crc >> 8);

            List<byte> output = new List<byte>();
            output.Add(0x7E);
            for (int i = 0; i < withFcs.Length; i++)
            {
                byte b = withFcs[i];
                if (b == 0x7E || b == 0x7D)
                {
                    output.Add(0x7D);
                    output.Add((byte)(b ^ 0x20));
                }
                else
                {
                    output.Add(b);
                }
            }

            output.Add(0x7E);
            return output.ToArray();
        }

        private static byte[] Frame(byte address, byte control, byte[]? info = null)
        {
            int infoLength = info == null ? 0 : info.Length;
            byte[] body = new byte[2 + infoLength];
            body[0] = address;
            body[1] = control;
            if (info != null)
            {
                Buffer.BlockCopy(info, 0, body, 2, info.Length);
            }

            return Wire(body);
        }

        /// <summary>
        /// Control octet of an I frame: N(S) in bits 2 to 4, P in bit 5, N(R) in bits 6 to 8 (2.3.2.1).
        /// </summary>
        private static byte IControl(int ns, int nr)
        {
            return (byte)((ns << 1) | (nr << 5));
        }

        /// <summary>
        /// Takes captured bytes apart: splits on flags, undoes the escapes, checks the FCS, and
        /// returns the bodies without the FCS.
        /// </summary>
        private static List<byte[]> SplitFrames(byte[] captured)
        {
            List<byte[]> frames = new List<byte[]>();
            List<byte> current = new List<byte>();
            bool escaped = false;
            for (int i = 0; i < captured.Length; i++)
            {
                byte b = captured[i];
                if (b == 0x7E)
                {
                    if (current.Count > 0)
                    {
                        byte[] raw = current.ToArray();
                        Assert.That(raw.Length, Is.GreaterThanOrEqualTo(4), "a frame is at least address, control and FCS");
                        ushort crc = Crc(raw, raw.Length - 2);
                        Assert.That(raw[raw.Length - 2], Is.EqualTo((byte)(crc & 0xFF)), "the transport sent a bad FCS (low octet)");
                        Assert.That(raw[raw.Length - 1], Is.EqualTo((byte)(crc >> 8)), "the transport sent a bad FCS (high octet)");
                        byte[] body = new byte[raw.Length - 2];
                        Buffer.BlockCopy(raw, 0, body, 0, body.Length);
                        frames.Add(body);
                        current.Clear();
                    }

                    escaped = false;
                    continue;
                }

                if (escaped)
                {
                    current.Add((byte)(b ^ 0x20));
                    escaped = false;
                }
                else if (b == 0x7D)
                {
                    escaped = true;
                }
                else
                {
                    current.Add(b);
                }
            }

            return frames;
        }

        private static byte[] CallRequest(ushort lcn, string called)
        {
            return X25PacketBuilder.CreateCallRequest(lcn: lcn, calledAddress: called, callingAddress: CallingAddress);
        }

        // ------------------------------------------------------------------ the helpers themselves

        [Test]
        public void TheTestCrc_MatchesThePublishedCheckValueOfCrc16X25()
        {
            // CRC-16/X-25 (the X.25 FCS): the check value for the ASCII text 123456789 is 0x906E.
            byte[] text = new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

            Assert.That(Crc(text, text.Length), Is.EqualTo((ushort)0x906E));
        }

        // ------------------------------------------------------------------ the incoming call

        [Test]
        public void ACallRequestInAnIFrame_IsAcceptedOnceAndTheAnswerGoesOutInAnIFrame()
        {
            Build(Dce());
            int routerRaises = 0;
            int transportRaises = 0;
            _runtime.Router.IncomingCallRouted += (s, e) => routerRaises++;
            _transport.IncomingCall += (s, e) => transportRaises++;

            // The DTE brings the link up: SABM with P=1, address B (a command from the DTE, 2.4.2).
            Inject(Frame(AddressB, Sabm));
            Assert.That(_transport.IsLinkUp, Is.True, "the SABM must bring the data link up");

            // N(S)=0, N(R)=0, the Call Request on logical channel 5 as the information field.
            Inject(Frame(AddressB, IControl(0, 0), CallRequest(5, LocalMachineId)));

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "exactly one Call Accepted (type 0x0F, Table 5-1) must go back");
            List<byte[]> packets = SentInformationPackets();
            byte[]? accepted = null;
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i].Length > 2 && packets[i][2] == 0x0F)
                {
                    accepted = packets[i];
                }
            }

            Assert.That(accepted, Is.Not.Null);
            Assert.That(accepted![1], Is.EqualTo((byte)5), "the answer must carry the channel the call came in on");

            // The DCE's I frame is a command from the DCE: address A (2.4.2).
            List<byte[]> frames = SentFrames();
            bool sawIFrameFromDce = false;
            for (int i = 0; i < frames.Count; i++)
            {
                if ((frames[i][1] & 0x01) == 0)
                {
                    sawIFrameFromDce = true;
                    Assert.That(frames[i][0], Is.EqualTo(AddressA));
                }
            }

            Assert.That(sawIFrameFromDce, Is.True);

            // Raised once: by the engine, not also by the transport or the router (one route only).
            Assert.That(transportRaises, Is.EqualTo(0), "the transport event must not also route the call");
            Assert.That(routerRaises, Is.EqualTo(0), "the router event must not also route the call");
        }

        [Test]
        public void ACallToAnUnknownAddress_IsClearedBackOverTheLine()
        {
            Build(Dce());
            Inject(Frame(AddressB, Sabm));

            Inject(Frame(AddressB, IControl(0, 0), CallRequest(6, "999")));

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0), "an unroutable call must not be accepted");
            List<byte[]> packets = SentInformationPackets();
            bool sawClear = false;
            for (int i = 0; i < packets.Count; i++)
            {
                // Clear Request / Clear Indication is 0x13 (Table 5-1).
                if (packets[i].Length > 2 && packets[i][2] == 0x13)
                {
                    sawClear = true;
                    Assert.That(packets[i][1], Is.EqualTo((byte)6));
                }
            }

            Assert.That(sawClear, Is.True, "the call must be cleared, not left open");
        }

        [Test]
        public void TheSameIFrameTwice_IsOneCall()
        {
            Build(Dce());
            Inject(Frame(AddressB, Sabm));
            byte[] callFrame = Frame(AddressB, IControl(0, 0), CallRequest(5, LocalMachineId));

            Inject(callFrame);
            Inject(callFrame);

            // The second copy has N(S)=0 while V(R)=1: a duplicate, which LAPB must not pass up.
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "a repeated I frame must not make a second call");
        }

        [Test]
        public void TwoFramesInOneRead_AndAFrameInTwoReads_BothWork()
        {
            Build(Dce());
            byte[] sabm = Frame(AddressB, Sabm);
            byte[] call = Frame(AddressB, IControl(0, 0), CallRequest(7, LocalMachineId));

            // The SABM and the first half of the call frame in one read, the rest in the next.
            int split = call.Length / 2;
            byte[] first = new byte[sabm.Length + split];
            Buffer.BlockCopy(sabm, 0, first, 0, sabm.Length);
            Buffer.BlockCopy(call, 0, first, sabm.Length, split);
            byte[] second = new byte[call.Length - split];
            Buffer.BlockCopy(call, split, second, 0, second.Length);

            Inject(first);
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0), "half a frame must not be acted on");
            Inject(second);

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ invalid frames (2.3.5.3)

        [Test]
        public void AFrameWithABadFcs_IsDroppedWithNoAction()
        {
            Build(Dce());
            byte[] sabm = Frame(AddressB, Sabm);
            // Flip a bit in the first FCS octet (the third octet after the opening flag, 0x7E 0x01 0x3F lo hi 0x7E).
            sabm[3] ^= 0x01;

            Inject(sabm);

            Assert.That(SentFrames(), Is.Empty, "2.3.5.3 d): a frame with an FCS error is discarded and nothing is sent");
            Assert.That(_transport.IsLinkUp, Is.False);
        }

        [Test]
        public void AFrameWithAnAddressOtherThanAOrB_IsDroppedWithNoAction()
        {
            Build(Dce());

            // Address 0x05 is neither A (0x03) nor B (0x01), and the FCS is good.
            Inject(Frame(0x05, Sabm));

            Assert.That(SentFrames(), Is.Empty, "2.3.5.3 e): a frame with another address is discarded and nothing is sent");
            Assert.That(_transport.IsLinkUp, Is.False);
            Assert.That(_transport.Link!.InvalidAddressFrames, Is.EqualTo(1));
        }

        [Test]
        public void AnAbortedFrame_IsDroppedAndTheNextFrameIsStillRead()
        {
            Build(Dce());

            // Control escape followed by a closing flag is an abort (2.3.5.3 f).
            byte[] aborted = new byte[] { 0x7E, 0x01, 0x3F, 0x7D, 0x7E };
            Inject(aborted);
            Assert.That(SentFrames(), Is.Empty);

            Inject(Frame(AddressB, Sabm));
            Assert.That(_transport.IsLinkUp, Is.True, "the frame after the abort must still be read");
        }

        // ------------------------------------------------------------------ roles and set-up

        [Test]
        public void ADte_AnswersAnSabmFromTheDceWithAUaCarryingAddressA()
        {
            Build(Dte());

            // The DCE's command carries address A (2.4.2); the DTE's response to it carries A as well.
            Inject(Frame(AddressA, Sabm));

            List<byte[]> frames = SentFrames();
            Assert.That(frames.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(frames[0], Is.EqualTo(new byte[] { AddressA, Ua }));
            Assert.That(_transport.IsLinkUp, Is.True);
        }

        [Test]
        public void ADce_AnswersAnSabmFromTheDteWithAUaCarryingAddressB()
        {
            Build(Dce());

            Inject(Frame(AddressB, Sabm));

            List<byte[]> frames = SentFrames();
            Assert.That(frames.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(frames[0], Is.EqualTo(new byte[] { AddressB, Ua }));
        }

        [Test]
        public void WhenToldToInitiate_ADteSendsTheFirstSabmWithAddressB()
        {
            Build(Dte(initiate: true));

            // 2.4.4.1: the DTE initiates by sending an SABM; the DTE's command carries address B.
            List<byte[]> frames = SentFrames();
            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(frames[0], Is.EqualTo(new byte[] { AddressB, Sabm }));
            Assert.That(_transport.IsLinkUp, Is.False, "the link is not up until the UA comes back");

            // The DCE's UA response carries address B.
            Inject(Frame(AddressB, Ua));

            Assert.That(_transport.IsLinkUp, Is.True);
        }

        [Test]
        public void WhenToldToInitiate_ADceSendsTheFirstSabmWithAddressA()
        {
            Build(Dce(initiate: true));

            // 2.4.4.1: the DCE initiates the same way; the DCE's command carries address A.
            List<byte[]> frames = SentFrames();
            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(frames[0], Is.EqualTo(new byte[] { AddressA, Sabm }));
        }

        [Test]
        public void WhenNotToldToInitiate_NothingIsSentUntilThePeerSpeaks()
        {
            Build(Dce(initiate: false));

            Assert.That(SentFrames(), Is.Empty);
        }

        [Test]
        public void WithNoLinkOptions_TheTransportReportsRawBytesOnlyAndRunsNoX25()
        {
            Build(null);
            int raw = 0;
            _transport.DataReceived += (s, e) => raw++;

            Inject(Frame(AddressB, Sabm));

            Assert.That(raw, Is.EqualTo(1), "the raw byte event is unchanged");
            Assert.That(SentFrames(), Is.Empty, "no role was given, so no role is guessed and no UA is sent");
            Assert.That(_transport.LinkRole, Is.Null);
            Assert.That(_transport.Link, Is.Null);
        }

        [Test]
        public void WithOnlyTheRoleSet_TheTransportDoesNotGuessTheSetupChoice()
        {
            Build(new SerialX25LinkOptions { Role = SerialX25LinkRole.Dce });

            Inject(Frame(AddressB, Sabm));

            Assert.That(SentFrames(), Is.Empty, "InitiateLinkSetup has no default, so the link is not started");
            Assert.That(_transport.Link, Is.Null);
        }

        // ------------------------------------------------------------------ disconnect and loss

        [Test]
        public void ADiscFromThePeer_BringsTheLinkDown_AndALaterCallOnTheSameChannelIsAcceptedAgain()
        {
            Build(Dce());
            List<bool> states = new List<bool>();
            _transport.LinkStateChanged += (s, up) => states.Add(up);

            Inject(Frame(AddressB, Sabm));
            Inject(Frame(AddressB, IControl(0, 0), CallRequest(5, LocalMachineId)));
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1));

            // DISC with P=1 from the DTE (2.4.4.3): the DCE answers UA and the link is down.
            Inject(Frame(AddressB, Disc));
            Assert.That(_transport.IsLinkUp, Is.False);
            Assert.That(states, Is.EqualTo(new List<bool> { true, false }));

            // The link comes back and the same channel number is used for a new call: this works
            // only if the engine of the dead link was dropped, because channel 5 was in data transfer.
            Inject(Frame(AddressB, Sabm));
            Inject(Frame(AddressB, IControl(0, 0), CallRequest(5, LocalMachineId)));

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(2), "a fresh engine must accept the new call on channel 5");
        }

        [Test]
        public void StoppingTheTransport_SendsADiscWhenTheLinkIsUp()
        {
            Build(Dce());
            Inject(Frame(AddressB, Sabm));

            _transport.Stop();

            // 2.4.4.3: the DCE disconnects with a DISC command, P=1; a command from the DCE carries address A.
            List<byte[]> frames = SentFrames();
            Assert.That(frames[frames.Count - 1], Is.EqualTo(new byte[] { AddressA, Disc }));
            Assert.That(_transport.IsLinkUp, Is.False);
        }

        // ------------------------------------------------------------------ the data link on its own

        [Test]
        public void AnUnansweredSabm_IsSentAgainWhenT1RunsOut()
        {
            long now = 0;
            List<byte[]> written = new List<byte[]>();
            SerialX25LinkOptions options = new SerialX25LinkOptions(SerialX25LinkRole.Dte, true) { T1Ms = 3000 };
            SerialLapbLink link = new SerialLapbLink(
                NullLogger.Instance, "t1", options, bytes => { written.Add(bytes); return true; }, () => now);
            link.Start();
            Assert.That(written.Count, Is.EqualTo(1));

            now = 2999;
            link.Tick();
            Assert.That(written.Count, Is.EqualTo(1), "T1 has not run out yet");

            now = 3000;
            link.Tick();
            Assert.That(written.Count, Is.EqualTo(2), "2.4.4.1: the SABM is sent again when T1 runs out");
            List<byte[]> second = SplitFrames(written[1]);
            Assert.That(second[0], Is.EqualTo(new byte[] { AddressB, Sabm }));
            link.Dispose();
        }

        [Test]
        public void ALinkWithNoRole_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => new SerialLapbLink(
                NullLogger.Instance, "x", new SerialX25LinkOptions(), bytes => true));
        }

        [Test]
        public void SendingBeforeTheLinkIsUp_ReturnsFalseAndSendsNothing()
        {
            Build(Dce());

            bool taken = _transport.SendPacket(new byte[] { 0x10, 0x05, 0x0F });

            Assert.That(taken, Is.False);
            Assert.That(SentFrames(), Is.Empty);
        }
    }
}
