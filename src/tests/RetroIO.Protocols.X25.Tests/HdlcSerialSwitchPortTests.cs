//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A one-way byte pipe: what one end writes, the other end reads. Reading waits for bytes; closing ends the wait.
    /// </summary>
    internal sealed class MemoryPipe
    {
        private readonly Queue<byte> _bytes = new Queue<byte>();
        private bool _closed;

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (_bytes)
            {
                if (_closed)
                {
                    throw new IOException("the line is closed");
                }

                for (int i = 0; i < count; i++)
                {
                    _bytes.Enqueue(buffer[offset + i]);
                }

                System.Threading.Monitor.PulseAll(_bytes);
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_bytes)
            {
                while (_bytes.Count == 0)
                {
                    if (_closed)
                    {
                        return 0;
                    }

                    System.Threading.Monitor.Wait(_bytes);
                }

                int n = 0;
                while (n < count && _bytes.Count > 0)
                {
                    buffer[offset + n] = _bytes.Dequeue();
                    n++;
                }

                return n;
            }
        }

        public void Close()
        {
            lock (_bytes)
            {
                _closed = true;
                System.Threading.Monitor.PulseAll(_bytes);
            }
        }
    }

    /// <summary>
    /// One end of an in-memory serial cable: writes go to the other end, reads come from it.
    /// </summary>
    internal sealed class MemoryLineEnd : Stream
    {
        private readonly MemoryPipe _in;
        private readonly MemoryPipe _out;
        private readonly Func<byte[], bool>? _dropWrite;

        public MemoryLineEnd(MemoryPipe inbound, MemoryPipe outbound, Func<byte[], bool>? dropWrite)
        {
            _in = inbound;
            _out = outbound;
            _dropWrite = dropWrite;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _in.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_dropWrite != null)
            {
                byte[] copy = new byte[count];
                Array.Copy(buffer, offset, copy, 0, count);
                if (_dropWrite(copy))
                {
                    return;
                }
            }

            _out.Write(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            // Closing one end ends the reads at both ends, as pulling a cable out of a USB adapter does.
            _in.Close();
            _out.Close();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An in-memory serial cable between two ports. The device can be unplugged and plugged back in.
    /// </summary>
    internal sealed class MemoryCable
    {
        private readonly object _sync = new object();
        private MemoryPipe _aToB = new MemoryPipe();
        private MemoryPipe _bToA = new MemoryPipe();
        private readonly List<MemoryLineEnd> _ends = new List<MemoryLineEnd>();
        private bool _unplugged;

        /// <summary>Decides, for each write by end A, whether it is lost. Null loses nothing.</summary>
        public Func<byte[], bool>? DropFromA;

        /// <summary>How many times end A was opened.</summary>
        public int OpensOfA;

        public Stream OpenA(PortConfig config)
        {
            return Open(true);
        }

        public Stream OpenB(PortConfig config)
        {
            return Open(false);
        }

        private Stream Open(bool isA)
        {
            lock (_sync)
            {
                if (_unplugged)
                {
                    throw new IOException("the device is not there (unplugged)");
                }

                if (isA)
                {
                    OpensOfA++;
                }

                MemoryLineEnd end = isA
                    ? new MemoryLineEnd(_bToA, _aToB, bytes => DropFromA != null && DropFromA(bytes))
                    : new MemoryLineEnd(_aToB, _bToA, null);
                _ends.Add(end);
                return end;
            }
        }

        public void Unplug()
        {
            lock (_sync)
            {
                _unplugged = true;
                for (int i = 0; i < _ends.Count; i++)
                {
                    _ends[i].Dispose();
                }

                _ends.Clear();
            }
        }

        public void Plug()
        {
            lock (_sync)
            {
                _aToB = new MemoryPipe();
                _bToA = new MemoryPipe();
                _unplugged = false;
            }
        }
    }

    /// <summary>
    /// Tests of the serial trunk port.
    /// </summary>
    /// <remarks>
    /// IMPORTANT: these tests do NOT exercise a real UART. This machine has no virtual serial pair (only physical
    /// USB serial devices, which must not be touched), so the port runs here over an in-memory cable through the
    /// same seam the real device uses (the stream the port reads and writes). Opening a real serial device, baud rate,
    /// parity and flow control are NOT proved by this file. What it does prove is the framing, LAPB, X.25 restart,
    /// loss recovery and device-loss handling that sit above the stream.
    /// </remarks>
    [TestFixture]
    public class HdlcSerialSwitchPortTests
    {
        private static PortConfig Config(int id, PortMode mode, string device = "TEST-LINE")
        {
            return new PortConfig
            {
                Id = id,
                Type = PortType.Trunk,
                Transport = PortTransport.HdlcSerial,
                Mode = mode,
                SerialDevice = device,
                SerialBaud = 115200,
                LciBase = 1,
                LciCount = 16,
                DefaultWindowSize = 7,
                LapbT1Ms = 500,
            };
        }

        private sealed class Ends : IDisposable
        {
            public HdlcSerialSwitchPort Dce = null!;
            public HdlcSerialSwitchPort Dte = null!;
            public readonly MemoryCable Cable = new MemoryCable();
            public readonly TrunkPair Rec = new TrunkPair();

            public void Dispose()
            {
                Dte?.Dispose();
                Dce?.Dispose();
            }
        }

        private static Ends Start()
        {
            Ends e = new Ends();
            // The DTE is end A so the test can lose frames it sends.
            e.Dte = new HdlcSerialSwitchPort(Config(1, PortMode.Dte), e.Rec.DteLog, e.Cable.OpenA);
            e.Dce = new HdlcSerialSwitchPort(Config(0, PortMode.Dce), e.Rec.DceLog, e.Cable.OpenB);
            e.Rec.Hook(e.Dce, e.Rec.DceIn, e.Rec.DceOut, e.Rec.AtDce, e.Rec.ClearedAtDce);
            e.Rec.Hook(e.Dte, e.Rec.DteIn, e.Rec.DteOut, e.Rec.AtDte, e.Rec.ClearedAtDte);
            Assert.That(e.Dce.Start(), Is.True, e.Rec.DceLog.All());
            Assert.That(e.Dte.Start(), Is.True, e.Rec.DteLog.All());
            return e;
        }

        private static void WaitUp(Ends e)
        {
            if (!SpinWait.SpinUntil(() => e.Dce.PeerConnected && e.Dte.PeerConnected, 10000))
            {
                throw new TimeoutException("the serial trunk did not come up. DCE:\n" + e.Rec.DceLog.All() + "\nDTE:\n" + e.Rec.DteLog.All());
            }
        }

        private static ushort Call(HdlcSwitchPortBase caller, HdlcSwitchPortBase callee)
        {
            using ManualResetEventSlim connected = new ManualResetEventSlim(false);
            PortIncomingCall onIncoming = (port, lci, c, g, cud) => callee.AcceptCall(lci);
            PortCallConnected onConnected = (port, lci) => connected.Set();
            callee.IncomingCall += onIncoming;
            caller.CallConnected += onConnected;
            try
            {
                ushort lci = caller.PlaceCall("23420001", "23400001", null);
                Assert.That(lci, Is.Not.EqualTo((ushort)0), "the call was not placed");
                Assert.That(connected.Wait(10000), Is.True, "the call was not accepted");
                return lci;
            }
            finally
            {
                callee.IncomingCall -= onIncoming;
                caller.CallConnected -= onConnected;
            }
        }

        /// <summary>
        /// Two serial ports over a cable come up as a LAPB link, and calls and data go both ways, intact.
        /// </summary>
        [Test]
        public void TwoPorts_OverTheLine_LinkUp_CallsAndDataGoBothWays()
        {
            using Ends e = Start();
            WaitUp(e);

            ushort lciFromDte = Call(e.Dte, e.Dce);
            byte[] up = new byte[128];
            for (int i = 0; i < up.Length; i++)
            {
                up[i] = (byte)(i ^ 0x7E);
            }

            TrunkPair.SendAll(e.Dte, lciFromDte, up);
            ushort lciFromDce = Call(e.Dce, e.Dte);
            byte[] down = new byte[] { 0x7D, 0x7E, 0x00, 0xFF, 0x7E };
            TrunkPair.SendAll(e.Dce, lciFromDce, down);

            Assert.That(SpinWait.SpinUntil(() => e.Rec.AtDce.Count >= 1 && e.Rec.AtDte.Count >= 1, 10000), Is.True);
            lock (e.Rec.AtDce)
            {
                Assert.That(e.Rec.AtDce[0].Data, Is.EqualTo(up));
            }

            lock (e.Rec.AtDte)
            {
                Assert.That(e.Rec.AtDte[0].Data, Is.EqualTo(down));
            }

            Assert.That(e.Dte.GetLinkSnapshot().Medium, Is.EqualTo("serial"));
            Assert.That(e.Dte.GetLinkSnapshot().FcsErrors, Is.EqualTo(0));
        }

        /// <summary>
        /// A frame lost on the line is sent again and arrives once, in order.
        /// </summary>
        [Test]
        public void ALostFrame_IsRetransmitted_AndDataArrivesOnceInOrder()
        {
            using Ends e = Start();
            WaitUp(e);
            ushort lci = Call(e.Dte, e.Dce);

            int informationFrames = 0;
            e.Cable.DropFromA = bytes =>
            {
                // A frame is 0x7E, address, control, ... ; an information frame has bit 0 of control clear.
                // The first information frame the DTE writes carrying the data is lost.
                if (bytes.Length > 3 && (bytes[2] & 0x01) == 0)
                {
                    return Interlocked.Increment(ref informationFrames) == 1;
                }

                return false;
            };

            for (int i = 0; i < 20; i++)
            {
                TrunkPair.SendAll(e.Dte, lci, new byte[] { (byte)i, (byte)(i + 1) });
            }

            Assert.That(SpinWait.SpinUntil(() => e.Rec.AtDce.Count >= 20, 15000), Is.True, "the data did not all arrive");
            lock (e.Rec.AtDce)
            {
                Assert.That(e.Rec.AtDce.Count, Is.EqualTo(20));
                for (int i = 0; i < 20; i++)
                {
                    Assert.That(e.Rec.AtDce[i].Data, Is.EqualTo(new byte[] { (byte)i, (byte)(i + 1) }));
                }
            }

            Assert.That(e.Dte.GetLinkSnapshot().Retransmissions, Is.GreaterThan(0));
        }

        /// <summary>
        /// A device that cannot be opened stops the port: Start returns false and the error names the device.
        /// </summary>
        [Test]
        public void ADeviceThatCannotBeOpened_StopsThePort_NamingTheDevice()
        {
            TrunkListLogger log = new TrunkListLogger();
            using HdlcSerialSwitchPort port = new HdlcSerialSwitchPort(Config(3, PortMode.Dte, "NO-SUCH-DEVICE-FOR-THE-TEST"), log);

            bool started = port.Start();

            Assert.That(started, Is.False);
            Assert.That(port.ConnectFailures, Is.GreaterThan(0));
            Assert.That(port.LastError, Does.Contain("NO-SUCH-DEVICE-FOR-THE-TEST").Or.Contain("Port 3"));
            Assert.That(log.Contains("Error"), Is.True, log.All());
        }

        /// <summary>
        /// A device that goes away clears the calls on it and ends the link; when it is back the port opens it
        /// again, the link comes up and a new call works.
        /// </summary>
        [Test]
        public void ADeviceThatDisappears_ClearsCalls_AndComesBackWhenItIsPluggedInAgain()
        {
            using Ends e = Start();
            WaitUp(e);
            Call(e.Dte, e.Dce);

            e.Cable.Unplug();

            Assert.That(SpinWait.SpinUntil(() => !e.Dce.PeerConnected && !e.Dte.PeerConnected, 10000), Is.True, "the link did not go down");
            Assert.That(SpinWait.SpinUntil(() => e.Rec.ClearedAtDte.Count >= 1, 10000), Is.True, "the call was not cleared");
            lock (e.Rec.ClearedAtDte)
            {
                Assert.That(e.Rec.ClearedAtDte[0].Cause, Is.EqualTo((byte)9));
            }

            e.Cable.Plug();

            WaitUp(e);
            Call(e.Dce, e.Dte);
            Assert.That(e.Dte.GetLinkSnapshot().LinkUps, Is.GreaterThanOrEqualTo(2));
        }

        /// <summary>
        /// Settings that cannot work are refused when the port is made, naming the port and the key.
        /// </summary>
        [TestCase("device", "", "needs 'device")]
        [TestCase("databits", "9", "databits")]
        [TestCase("parity", "mark", "parity")]
        [TestCase("stopbits", "3", "stopbits")]
        [TestCase("flow", "xonxoff", "flow")]
        [TestCase("baud", "10", "baud")]
        public void SettingsThatCannotWork_AreRefused(string key, string value, string expected)
        {
            PortConfig c = Config(5, PortMode.Dce);
            switch (key)
            {
                case "device": c.SerialDevice = value; break;
                case "databits": c.SerialDataBits = int.Parse(value); break;
                case "parity": c.SerialParity = value; break;
                case "stopbits": c.SerialStopBits = int.Parse(value); break;
                case "flow": c.SerialFlow = value; break;
                case "baud": c.SerialBaud = uint.Parse(value); break;
            }

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => new HdlcSerialSwitchPort(c))!;

            Assert.That(ex.Message, Does.Contain("Port 5"));
            Assert.That(ex.Message, Does.Contain(expected));
        }
    }
}
