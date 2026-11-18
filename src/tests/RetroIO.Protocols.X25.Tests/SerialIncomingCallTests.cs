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
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;

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
        private void Build(SerialX25LinkOptions? options, bool answerAutomatically = true, bool withRoute = true)
        {
            SerialInterfaceConfig serial = new SerialInterfaceConfig
            {
                Name = InterfaceName,
                PortName = "MOCK" + MockPort,
                RemoteMachineId = "REMOTE1",
                X25Link = options,
                AnswerIncomingCallsAutomatically = answerAutomatically,
            };

            RuntimeConfig config = new RuntimeConfig { TestMode = true };
            config.X25.LocalMachineId = LocalMachineId;
            config.Routing.Enabled = true;
            config.Routing.SerialInterfaces.Add(serial);

            // The router answers every incoming call first (decided by Ronny, 2026-11-17): a call with
            // no inbound route is refused with access barred before a service or the program hears of
            // it. So the line's own address gets a route to the local TAD unless a test wants none.
            if (withRoute)
            {
                config.Routing.RoutingTable.Add(new X25RouteEntry
                {
                    Name = "LocalTAD",
                    X121AddressPattern = "^" + LocalMachineId + "$",
                    Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200,
                    Enabled = true
                });
            }

            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
            _runtime.PhysicalLayer.MockSerialTransmit += (s, e) =>
            {
                lock (_lock)
                {
                    _written.Add(e.Data);
                }
            };

            _transport = (SerialTransport)_runtime.TransportInterfaces[InterfaceName];

            if (options != null && options.IsComplete)
            {
                // Since 2026-11-17 the runtime opens a line with complete X25Link options itself and
                // starts its transport inside the constructor (SerialPortOwnershipTests). The capture
                // above can only be attached after the constructor, so a SABM sent at that start
                // would not be seen. The transport is stopped and started again so the capture sees
                // the start-up from its first byte. The stop itself may send a DISC (a side that sent
                // a SABM is no longer disconnected, SerialLapbLink.Stop), so what the stop wrote is
                // dropped from the capture before the new start.
                Assert.That(_transport.Status, Is.EqualTo(TransportStatus.Active), "the runtime must have opened the line and started the transport");
                _transport.Stop();
                lock (_lock)
                {
                    _written.Clear();
                }
            }
            else
            {
                // A line without complete X25Link options belongs to the link manager, so the
                // runtime does not open it. These tests drive the serial transport on such a line
                // on its own, with no link manager link, so the port is opened here by hand.
                _runtime.PhysicalLayer.InitializeSerialPorts(config.Routing.SerialInterfaces);
                Assert.That(_runtime.PhysicalLayer.Start(), Is.True, "the mock serial port must start");
                _transport.Stop();
            }

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

        // Restart Indication with cause Network operational: GFI 0001, channel 0, type 0xFB (Table 5-1), cause 00000111
        // (Table 5-9), diagnostic 0.
        private static readonly byte[] RestartIndicationNetworkOperational = new byte[] { 0x10, 0x00, 0xFB, 0x07, 0x00 };

        // DTE Restart Confirmation: GFI 0001, channel 0, type 0xFF (Table 5-1).
        private static readonly byte[] RestartConfirmation = new byte[] { 0x10, 0x00, 0xFF };

        /// <summary>
        /// Brings a DCE line up the way X.25 orders it: the DTE sends SABM, the DCE answers UA and then sends its
        /// Restart Indication with cause "Network operational" (4.6.5, text line 5938-5941), and the DTE confirms it
        /// (3.3.2: "The DTE will confirm the restart by transferring a DTE restart confirmation packet"). The
        /// confirmation goes in the DTE's first I frame, N(S)=0, and acknowledges the DCE's I frame, N(R)=1. Until
        /// it is confirmed the DCE "will ignore all packets except for restart request and DTE restart
        /// confirmation" (3.3.2), so every test that sends a call on a DCE line comes through here first. The DTE's
        /// next I frame is therefore N(S)=1, and N(R) is at least 1.
        /// </summary>
        private void BringUpTheDceLineAndConfirmTheRestart()
        {
            int indicationsBefore = CountPacketsOfType(0xFB);
            Inject(Frame(AddressB, Sabm));
            Assert.That(_transport.IsLinkUp, Is.True, "the SABM must bring the data link up");
            Assert.That(CountPacketsOfType(0xFB), Is.EqualTo(indicationsBefore + 1), "the DCE must send its Restart Indication when the link comes up");
            Inject(Frame(AddressB, IControl(0, 1), RestartConfirmation));
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
            int transportRaises = 0;
            _transport.IncomingCall += (s, e) => transportRaises++;

            // The DTE brings the link up: SABM with P=1, address B (a command from the DTE, 2.4.2), and confirms
            // the DCE's restart.
            BringUpTheDceLineAndConfirmTheRestart();

            // N(S)=1, N(R)=1, the Call Request on logical channel 5 as the information field.
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));

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

            // Raised once: by the engine, not also by the transport (one route only). The router's
            // own IncomingCallRouted event, also checked here before, was removed on 2026-11-17.
            Assert.That(transportRaises, Is.EqualTo(0), "the transport event must not also route the call");
        }

        [Test]
        public void ACallToAnUnknownAddress_IsClearedBackOverTheLine()
        {
            Build(Dce());
            BringUpTheDceLineAndConfirmTheRestart();

            Inject(Frame(AddressB, IControl(1, 1), CallRequest(6, "999")));

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
            BringUpTheDceLineAndConfirmTheRestart();
            byte[] callFrame = Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId));

            Inject(callFrame);
            Inject(callFrame);

            // The second copy has N(S)=1 while V(R)=2: a duplicate, which LAPB must not pass up.
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "a repeated I frame must not make a second call");
        }

        [Test]
        public void TwoFramesInOneRead_AndAFrameInTwoReads_BothWork()
        {
            Build(Dce());
            byte[] sabm = Frame(AddressB, Sabm);
            byte[] confirm = Frame(AddressB, IControl(0, 1), RestartConfirmation);
            byte[] call = Frame(AddressB, IControl(1, 1), CallRequest(7, LocalMachineId));

            // The SABM is read alone: the DCE's Restart Indication answers it, and the DTE's confirmation of that
            // restart (4.6.5, 3.3.2) has to come after. Then the confirmation and the first half of the call frame
            // in one read, the rest in the next.
            Inject(sabm);
            int split = call.Length / 2;
            byte[] first = new byte[confirm.Length + split];
            Buffer.BlockCopy(confirm, 0, first, 0, confirm.Length);
            Buffer.BlockCopy(call, 0, first, confirm.Length, split);
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

        /// <summary>
        /// X.25 (10/96) 4.6.5 (text line 5938-5941): when an out of order condition is recovered "the DCE will send a
        /// restart indication packet with the cause 'Network operational' to the local DTE"; 3.3.2: "The DTE will confirm
        /// the restart by transferring a DTE restart confirmation packet". The runtime as the DTE of a serial line must
        /// learn of the switch's return that way: after the link comes up it answers the Restart Indication with a
        /// Restart Confirmation (type 0xFF, Table 5-1) on channel 0.
        /// </summary>
        [Test]
        public void ADte_ConfirmsTheRestartIndicationTheDceSendsWhenTheLinkComesBack()
        {
            Build(Dte());
            Inject(Frame(AddressA, Sabm));
            Assert.That(_transport.IsLinkUp, Is.True);

            // Restart Indication, cause Network operational 0x07 (Table 5-9), diagnostic 0, in the DCE's first I frame.
            Inject(Frame(AddressA, IControl(0, 0), new byte[] { 0x10, 0x00, 0xFB, 0x07, 0x00 }));

            Assert.That(CountPacketsOfType(0xFF), Is.EqualTo(1), "the DTE did not confirm the restart");
        }

        /// <summary>
        /// X.25 (10/96) 4.6.5 (text line 5936-5941): "When the out of order condition is recovered: 1) the DCE will send
        /// a restart indication packet with the cause 'Network operational' to the local DTE". A serial line run as the
        /// DCE sent nothing when its link came up until 2026-11-18 (BUGS.md).
        /// </summary>
        [Test]
        public void ADce_SendsARestartIndicationNetworkOperational_WhenTheLinkComesUp()
        {
            Build(Dce());

            Inject(Frame(AddressB, Sabm));

            List<byte[]> frames = SentFrames();
            Assert.That(frames[0], Is.EqualTo(new byte[] { AddressB, Ua }), "the UA answers the SABM first");
            List<byte[]> packets = SentInformationPackets();
            Assert.That(packets.Count, Is.EqualTo(1), "one packet: the Restart Indication");
            Assert.That(packets[0], Is.EqualTo(RestartIndicationNetworkOperational));

            // The DCE's I frame is a command from the DCE, address A, N(S)=0 (2.4.2, 2.3.2.1).
            byte[] iFrame = frames[frames.Count - 1];
            Assert.That(iFrame[0], Is.EqualTo(AddressA));
            Assert.That(iFrame[1] & 0x0F, Is.EqualTo(0), "an I frame with N(S)=0");
        }

        /// <summary>
        /// X.25 (10/96) 3.3.2 (text line 5066-5070): "the DCE will ignore all packets except for restart request and DTE
        /// restart confirmation" while its Restart Indication is unconfirmed (state r3); Table C.2 column r3 says
        /// DISCARD for a Call Request. After the confirmation the same call is taken.
        /// </summary>
        [Test]
        public void ADce_IgnoresACallRequestUntilTheDteConfirmsTheRestart()
        {
            Build(Dce());
            Inject(Frame(AddressB, Sabm));

            Inject(Frame(AddressB, IControl(0, 0), CallRequest(5, LocalMachineId)));
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0), "no Call Accepted in state r3");
            Assert.That(CountPacketsOfType(0x13), Is.EqualTo(0), "no Clear either: the packet is discarded");

            Inject(Frame(AddressB, IControl(1, 1), RestartConfirmation));
            Inject(Frame(AddressB, IControl(2, 1), CallRequest(5, LocalMachineId)));

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "after the confirmation the call is accepted");
        }

        /// <summary>
        /// T10 on a serial DCE line: X.25 (10/96) Table D.1 (text line 16129-16151): 60 s, and on the first expiry the
        /// DCE "signals a restart indication (local procedure error # 52) again". The runtime's T10 setting is used.
        /// </summary>
        [Test]
        public void ADce_SendsTheRestartIndicationAgainWhenT10RunsOut()
        {
            Build(Dce());
            Inject(Frame(AddressB, Sabm));
            X25Endpoint endpoint = _runtime.EndpointForTest(_transport)!;

            endpoint.Tick(Environment.TickCount64 + 50_000);
            Assert.That(CountPacketsOfType(0xFB), Is.EqualTo(1), "T10 is 60 s; not run out yet");

            endpoint.Tick(Environment.TickCount64 + 61_000);
            Assert.That(CountPacketsOfType(0xFB), Is.EqualTo(2), "the Restart Indication is sent again");
            List<byte[]> packets = SentInformationPackets();
            Assert.That(packets[packets.Count - 1], Is.EqualTo(new byte[] { 0x10, 0x00, 0xFB, 0x01, 52 }), "local procedure error (Table 5-9), # 52");
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

            BringUpTheDceLineAndConfirmTheRestart();
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1));

            // DISC with P=1 from the DTE (2.4.4.3): the DCE answers UA and the link is down.
            Inject(Frame(AddressB, Disc));
            Assert.That(_transport.IsLinkUp, Is.False);
            Assert.That(states, Is.EqualTo(new List<bool> { true, false }));

            // The link comes back: that is the recovery of an out of order condition (4.6.2: "the DCE has received
            // or transmitted a DISC command"), so the DCE sends a second Restart Indication (4.6.5), which the helper
            // checks. The same channel number is then used for a new call: this works only if the engine of the dead
            // link was dropped, because channel 5 was in data transfer.
            BringUpTheDceLineAndConfirmTheRestart();
            Assert.That(CountPacketsOfType(0xFB), Is.EqualTo(2), "one Restart Indication for each time the link came up");
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));

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

        // ------------------------------------------------------------------ the outgoing call (2026-11-17)

        [Test]
        public void PlaceCall_SendsACallRequestInAnIFrame_AndACallConnectedBringsTheCircuitUp()
        {
            Build(Dte());
            X25Endpoint? placedOn = null;
            ushort placedLci = 0;
            _runtime.CallPlacedOnEndpointForTest = (endpoint, lci) =>
            {
                placedOn = endpoint;
                placedLci = lci;
            };

            // The DCE brings the link up: SABM with P=1 and address A (a command from the DCE, 2.4.2).
            Inject(Frame(AddressA, Sabm));
            Assert.That(_transport.IsLinkUp, Is.True);

            bool placed = _transport.PlaceCall(new X25CallRequest("300", LocalMachineId));

            Assert.That(placed, Is.True, "the line's engine must take the call");
            Assert.That(placedOn, Is.Not.Null, "the call must be placed through the line's X.25 engine");

            // One Call Request (type 0x0B, Table 5-1) in an I frame; the DTE's command carries address B (2.4.2).
            Assert.That(CountPacketsOfType(0x0B), Is.EqualTo(1), "exactly one Call Request must go out");
            List<byte[]> frames = SentFrames();
            byte[]? callFrame = null;
            for (int i = 0; i < frames.Count; i++)
            {
                if ((frames[i][1] & 0x01) == 0 && frames[i].Length > 4 && frames[i][4] == 0x0B)
                {
                    callFrame = frames[i];
                }
            }

            Assert.That(callFrame, Is.Not.Null);
            Assert.That(callFrame![0], Is.EqualTo(AddressB), "an I frame from the DTE is a command with address B");
            ushort lcn = (ushort)(((callFrame[2] & 0x0F) << 8) | callFrame[3]);
            Assert.That(lcn, Is.EqualTo(placedLci));

            // The DCE answers with Call Connected (GFI 0x1, the channel, type 0x0F, Table 5-1), written
            // out here as bytes, in an I frame N(S)=0 that acknowledges our I frame (N(R)=1).
            byte[] callConnected = new byte[] { (byte)(0x10 | (lcn >> 8)), (byte)(lcn & 0xFF), 0x0F };
            Inject(Frame(AddressA, IControl(0, 1), callConnected));

            Assert.That(placedOn!.Layer.Circuits[placedLci].State, Is.EqualTo(PacketLayer.X25VcState.DataTransfer),
                "4.1.3: on Call Connected the calling DTE's channel enters data transfer");
        }

        [Test]
        public void PlaceCall_WhileTheDataLinkIsDown_IsRefusedAndSendsNothing()
        {
            Build(Dte());

            bool placed = _transport.PlaceCall(new X25CallRequest("300", LocalMachineId));

            Assert.That(placed, Is.False, "with no data link there is nothing to carry a Call Request");
            Assert.That(SentFrames(), Is.Empty);
        }

        [Test]
        public void PlaceCall_WithNoX25LinkConfigured_IsRefused()
        {
            Build(null);

            Assert.That(_transport.PlaceCall(new X25CallRequest("300", LocalMachineId)), Is.False);
            Assert.That(SentFrames(), Is.Empty);
        }

        // ------------------------------------------------------------------ waiting for the program's answer (2026-11-17)
        //
        // X.25 4.1.4: "The called DTE shall indicate its acceptance of the call by transferring across
        // the DTE/DCE interface a call accepted packet specifying the same logical channel as that of
        // the incoming call packet. This places the specified logical channel in the data transfer
        // state (p4). If the called DTE does not accept the call by a call accepted packet or does not
        // reject it by a clear request packet as described in 4.1.7 within time-out T11 (see Annex D),
        // the DCE will consider it as a procedure error from the called DTE and will clear the virtual
        // call according to the procedure described in 4.1.8." Table D.1: T11 = 180 s, on expiry a
        // clear indication with local procedure error # 49.

        [Test]
        public void TheDefault_IsToAnswerAutomatically()
        {
            Assert.That(new SerialInterfaceConfig().AnswerIncomingCallsAutomatically, Is.True,
                "nothing that works today may change: a line answers by itself unless told to wait");
        }

        /// <summary>
        /// Brings the link up and sends a Call Request on channel 5 to a line that waits for the
        /// program. Returns the call the transport offered.
        /// </summary>
        private X25CallRequest OfferACallOnChannel5()
        {
            Build(Dce(), answerAutomatically: false);
            List<X25CallRequest> offered = new List<X25CallRequest>();
            _transport.IncomingCall += (s, e) => offered.Add(e);

            BringUpTheDceLineAndConfirmTheRestart();
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));

            Assert.That(offered.Count, Is.EqualTo(1), "a waiting line must offer the call to the program once");
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0), "no Call Accepted may go out before the program answers");
            Assert.That(CountPacketsOfType(0x13), Is.EqualTo(0), "no Clear Request may go out before the program answers");
            Assert.That(offered[0].CalledAddress, Is.EqualTo(LocalMachineId));
            Assert.That(offered[0].CallingAddress, Is.EqualTo(CallingAddress));
            return offered[0];
        }

        private PacketLayer.X25Vc Channel5()
        {
            X25Endpoint? endpoint = _runtime.EndpointForTest(_transport);
            Assert.That(endpoint, Is.Not.Null, "the line's X.25 engine must exist");
            return endpoint!.Layer.Circuits[5];
        }

        private byte[] LastPacketOfType(byte pti)
        {
            byte[]? found = null;
            List<byte[]> packets = SentInformationPackets();
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i].Length > 2 && packets[i][2] == pti)
                {
                    found = packets[i];
                }
            }

            Assert.That(found, Is.Not.Null, "no packet of type 0x" + pti.ToString("X2") + " was sent");
            return found!;
        }

        [Test]
        public void WaitMode_AcceptCall_SendsCallAccepted_AndTheChannelIsInDataTransfer()
        {
            X25CallRequest call = OfferACallOnChannel5();
            Assert.That(Channel5().State, Is.EqualTo(PacketLayer.X25VcState.CallIncoming), "the call waits in p3");

            bool accepted = _transport.AcceptCall(call);

            Assert.That(accepted, Is.True);
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "exactly one Call Accepted (type 0x0F, Table 5-1)");
            Assert.That(LastPacketOfType(0x0F)[1], Is.EqualTo((byte)5), "on the channel of the incoming call");
            Assert.That(Channel5().State, Is.EqualTo(PacketLayer.X25VcState.DataTransfer), "4.1.4: the channel enters p4");
            Assert.That(_transport.AcceptCall(call), Is.False, "a call can be answered once");
        }

        [Test]
        public void WaitMode_RejectCall_SendsAClearRequestWithTheGivenCause()
        {
            X25CallRequest call = OfferACallOnChannel5();

            bool rejected = _transport.RejectCall(call, X25Cause.AccessBarred);

            Assert.That(rejected, Is.True);
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0), "a refused call is never accepted");
            byte[] clear = LastPacketOfType(0x13);
            Assert.That(clear[1], Is.EqualTo((byte)5), "on the channel of the incoming call");
            Assert.That(clear[3], Is.EqualTo(X25Cause.AccessBarred), "the cause the program gave (Table 5-7: 0x0B access barred)");
            Assert.That(clear[4], Is.EqualTo((byte)0), "diagnostic 0");
            Assert.That(Channel5().State, Is.EqualTo(PacketLayer.X25VcState.ClearRequest), "4.1.7: the channel is clearing (p6)");
        }

        [Test]
        public void WaitMode_NoAnswer_IsClearedWhenT11RunsOut()
        {
            X25CallRequest call = OfferACallOnChannel5();
            X25Endpoint endpoint = _runtime.EndpointForTest(_transport)!;

            // Short of T11 nothing happens. The engine's clock is driven by hand through Tick.
            endpoint.Tick(Environment.TickCount64 + 170_000);
            Assert.That(CountPacketsOfType(0x13), Is.EqualTo(0), "T11 is 180 s (Table D.1); it has not run out");

            endpoint.Tick(Environment.TickCount64 + 181_000);

            byte[] clear = LastPacketOfType(0x13);
            Assert.That(clear[1], Is.EqualTo((byte)5));
            Assert.That(clear[3], Is.EqualTo(X25Cause.ClearingLocalProcedureError), "Table D.1: local procedure error (this line is the DCE)");
            Assert.That(clear[4], Is.EqualTo(X25Diagnostic.TimeExpiredCall), "Table D.1: # 49");
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0));
            Assert.That(_transport.AcceptCall(call), Is.False, "a call cleared at T11 can no longer be accepted");
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

        // ------------------------------------------------------------------ the router answers first (2026-11-17)
        //
        // Decided by Ronny, 2026-11-17: the router answers an incoming call first. A call it has no
        // inbound route for is refused at once with a Clear Request, cause 0x0D not obtainable, diagnostic
        // 67 (X.25 4.1.4 and 4.1.7, Table C.1 row 24; it was 0x0B access barred until 2026-11-18), on the line it came in on; only a call it has a route for is
        // offered to the program (wait mode) or answered by the local service (automatic mode).

        private int CountClearsOn(byte lcn)
        {
            int count = 0;
            List<byte[]> packets = SentInformationPackets();
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i].Length > 3 && packets[i][2] == 0x13 && packets[i][1] == lcn)
                {
                    count++;
                }
            }

            return count;
        }

        private byte[] TheClearOn(byte lcn)
        {
            Assert.That(CountClearsOn(lcn), Is.EqualTo(1), "exactly one Clear Request (type 0x13, Table 5-1) on channel " + lcn);
            List<byte[]> packets = SentInformationPackets();
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i].Length > 3 && packets[i][2] == 0x13 && packets[i][1] == lcn)
                {
                    return packets[i];
                }
            }

            throw new AssertionException("unreachable");
        }

        [Test]
        public void WaitMode_NoRoute_IsClearedWithNotObtainable_AndNotOfferedToTheProgram()
        {
            Build(Dce(), answerAutomatically: false, withRoute: false);
            int offered = 0;
            _transport.IncomingCall += (s, e) => offered++;
            BringUpTheDceLineAndConfirmTheRestart();

            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));

            byte[] clear = TheClearOn(5);
            Assert.That(clear[3], Is.EqualTo(X25Cause.NotObtainable), "no inbound route: Table C.1 row 24, 0x0D not obtainable");
            Assert.That(clear[4], Is.EqualTo(X25Diagnostic.InvalidCalledAddress), "Table C.1 row 24: # 67");
            Assert.That(offered, Is.EqualTo(0), "the router refused it; the program must not be offered the call");
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0));
        }

        [Test]
        public void AutomaticMode_NoRoute_IsClearedWithNotObtainable()
        {
            Build(Dce(), answerAutomatically: true, withRoute: false);
            BringUpTheDceLineAndConfirmTheRestart();

            // The local TAD takes calls to the machine's own address, but with no inbound route the
            // router refuses the call before any service is asked.
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0), "a call with no route must not be accepted");
            Assert.That(TheClearOn(5)[3], Is.EqualTo(X25Cause.NotObtainable));
            Assert.That(TheClearOn(5)[4], Is.EqualTo(X25Diagnostic.InvalidCalledAddress), "Table C.1 row 24: # 67");
        }

        [Test]
        public void AutomaticMode_ACallToAnUnroutedAddress_IsClearedWithNotObtainable()
        {
            Build(Dce());
            BringUpTheDceLineAndConfirmTheRestart();

            Inject(Frame(AddressB, IControl(1, 1), CallRequest(6, "999")));

            Assert.That(TheClearOn(6)[3], Is.EqualTo(X25Cause.NotObtainable), "Table C.1 row 24");
            Assert.That(TheClearOn(6)[4], Is.EqualTo(X25Diagnostic.InvalidCalledAddress), "the router refuses it with # 67");
        }

        [Test]
        public void AutomaticMode_WithARoute_IsAcceptedWithoutAskingTheProgram()
        {
            Build(Dce());
            int offered = 0;
            _transport.IncomingCall += (s, e) => offered++;
            BringUpTheDceLineAndConfirmTheRestart();

            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "one Call Accepted");
            Assert.That(LastPacketOfType(0x0F)[1], Is.EqualTo((byte)5));
            Assert.That(offered, Is.EqualTo(0), "an automatic line does not offer the call to the program");
            Assert.That(Channel5().State, Is.EqualTo(PacketLayer.X25VcState.DataTransfer));
        }

        [Test]
        public void AnswersForARefusedAnAnsweredOrAnUnknownCall_ReturnFalse_AndSendNothing()
        {
            Build(Dce(), answerAutomatically: false);
            List<X25CallRequest> offered = new List<X25CallRequest>();
            _transport.IncomingCall += (s, e) => offered.Add(e);
            BringUpTheDceLineAndConfirmTheRestart();

            // Channel 6 has no route: the router refuses it.
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(6, "999")));
            Assert.That(TheClearOn(6)[3], Is.EqualTo(X25Cause.NotObtainable));
            Assert.That(offered.Count, Is.EqualTo(0));
            int framesBefore = SentFrames().Count;

            X25CallRequest lookalike = new X25CallRequest("999", CallingAddress) { SourceLcn = 6 };
            Assert.That(_transport.AcceptCall(lookalike), Is.False, "a call the router refused cannot be accepted");
            Assert.That(_transport.RejectCall(lookalike, X25Cause.OutOfOrder), Is.False, "nor refused a second time");
            Assert.That(_transport.AcceptCall(new X25CallRequest(LocalMachineId, CallingAddress)), Is.False, "a call never offered");
            Assert.That(SentFrames().Count, Is.EqualTo(framesBefore), "nothing may be sent for those answers");

            // Channel 5 is routed and offered; answered once, then every other answer is refused.
            Inject(Frame(AddressB, IControl(2, 1), CallRequest(5, LocalMachineId)));
            Assert.That(offered.Count, Is.EqualTo(1));
            Assert.That(_transport.AcceptCall(offered[0]), Is.True);
            Assert.That(_transport.AcceptCall(offered[0]), Is.False, "a call can be answered once");
            Assert.That(_transport.RejectCall(offered[0], X25Cause.OutOfOrder), Is.False, "an accepted call cannot then be refused");

            // Counted by packet type, not by frames on the line. Once the call is accepted the local TAD
            // service starts its session on channel 5 from its own thread (a Q-bit data packet, an X.29
            // "set PAD parameters" message, decoded from a failing run on 2026-11-18), so a frame count
            // taken after the accept raced that packet and failed about one run in three. What the two
            // refused answers must not send is a second Call Accepted (type 0x0F) or a Clear Request
            // (type 0x13, Table 5-1).
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1), "exactly one Call Accepted: the second accept sends nothing");
            Assert.That(CountClearsOn(5), Is.EqualTo(0), "the refused reject sends no Clear Request");
        }

        [Test]
        public void TwoCallsAtOnce_OneRoutedOneNot_AreEachAnsweredOnTheirOwnChannel()
        {
            Build(Dce(), answerAutomatically: false);
            List<X25CallRequest> offered = new List<X25CallRequest>();
            _transport.IncomingCall += (s, e) => offered.Add(e);
            BringUpTheDceLineAndConfirmTheRestart();

            // Both calls are in before either is answered: channel 5 routed, channel 6 not.
            Inject(Frame(AddressB, IControl(1, 1), CallRequest(5, LocalMachineId)));
            Inject(Frame(AddressB, IControl(2, 1), CallRequest(6, "999")));

            Assert.That(TheClearOn(6)[3], Is.EqualTo(X25Cause.NotObtainable), "the call with no route is refused at once");
            Assert.That(offered.Count, Is.EqualTo(1), "only the routed call is offered");
            Assert.That(offered[0].SourceLcn, Is.EqualTo((ushort)5));
            Assert.That(offered[0].CalledAddress, Is.EqualTo(LocalMachineId));
            Assert.That(Channel5().State, Is.EqualTo(PacketLayer.X25VcState.CallIncoming), "channel 5 still waits for the program");

            Assert.That(_transport.AcceptCall(offered[0]), Is.True);

            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(1));
            Assert.That(LastPacketOfType(0x0F)[1], Is.EqualTo((byte)5), "the Call Accepted is on channel 5");
            Assert.That(CountClearsOn(5), Is.EqualTo(0), "channel 5 is not cleared");
            Assert.That(Channel5().State, Is.EqualTo(PacketLayer.X25VcState.DataTransfer));
        }

        [Test]
        public void WaitMode_CallerClearsWhileTheProgramDecides_ALaterAcceptReturnsFalseAndSendsNothing()
        {
            X25CallRequest call = OfferACallOnChannel5();

            // The DTE gives up (4.1.7): Clear Request on channel 5, GFI 1, type 0x13, cause 0, diagnostic 0,
            // in its third I frame (N(S)=2, after the restart confirmation and the call); the DCE's only I frame
            // so far is its Restart Indication, already acknowledged, so N(R)=1.
            Inject(Frame(AddressB, IControl(2, 1), new byte[] { 0x10, 0x05, 0x13, 0x00, 0x00 }));
            Assert.That(CountPacketsOfType(0x17), Is.EqualTo(1), "4.1.7: the DCE confirms the clear (type 0x17)");
            int framesBefore = SentFrames().Count;

            Assert.That(_transport.AcceptCall(call), Is.False, "the call is gone; it cannot be accepted");
            Assert.That(_transport.RejectCall(call, X25Cause.OutOfOrder), Is.False, "nor refused");
            Assert.That(SentFrames().Count, Is.EqualTo(framesBefore), "nothing may be sent");
            Assert.That(CountPacketsOfType(0x0F), Is.EqualTo(0));
        }
    }
}
