//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Hdlc;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Which way a frame travels through the fault proxy.
    /// </summary>
    internal enum TrunkDirection
    {
        /// <summary>From the end that dials (the DTE) to the end that listens (the DCE).</summary>
        ToDce,

        /// <summary>From the DCE back to the DTE.</summary>
        ToDte,
    }

    /// <summary>
    /// What the fault proxy does with one frame.
    /// </summary>
    internal enum FrameFate
    {
        /// <summary>Pass it on unchanged.</summary>
        Pass,

        /// <summary>Do not pass it on.</summary>
        Drop,

        /// <summary>Pass it on twice.</summary>
        Duplicate,

        /// <summary>Pass it on with one bit changed in the middle, so the FCS is wrong.</summary>
        Corrupt,
    }

    /// <summary>
    /// What the fault proxy knows about one frame it is deciding on.
    /// </summary>
    internal sealed class ProxiedFrame
    {
        /// <summary>The direction the frame travels.</summary>
        public TrunkDirection Direction;

        /// <summary>How many frames went this way before this one on this connection, counting from 0.</summary>
        public int Index;

        /// <summary>The LAPB address octet; 0 if the frame was too short to have one.</summary>
        public byte Address;

        /// <summary>The LAPB control octet; 0 if the frame was too short to have one.</summary>
        public byte Control;

        /// <summary>True for an information frame.</summary>
        public bool IsInformation => (Control & 0x01) == 0;

        /// <summary>The send sequence number N(S) of an information frame.</summary>
        public int Ns => (Control >> 1) & 0x07;

        /// <summary>How many times this exact N(S) has been seen going this way on this connection, counting from 1.</summary>
        public int TimesSeen;

        /// <summary>
        /// The whole frame, unstuffed: address, control, information field, then the two FCS octets.
        /// </summary>
        public byte[] Body = Array.Empty<byte>();
    }

    /// <summary>
    /// A TCP relay between the two ends of a trunk that sees every HDLC frame go by and can drop, repeat or damage
    /// chosen ones. This is the "fault-injecting stream between the two ends" for the trunk tests.
    /// </summary>
    /// <remarks>
    /// It splits the byte stream at the 0x7E flags and does not unstuff more than it needs to read the address
    /// and control octets, so the frames it passes on are the original bytes. A damaged frame has one bit flipped
    /// inside it, which only the FCS can notice.
    /// </remarks>
    internal sealed class HdlcFaultProxy : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<ProxiedFrame, FrameFate> _rule;
        private readonly ushort _targetPort;
        private readonly string _targetHost;
        private readonly List<ProxiedFrame> _seen = new List<ProxiedFrame>();
        private readonly object _gate = new object();
        private readonly List<TcpClient> _open = new List<TcpClient>();
        private volatile bool _running = true;
        private int _connections;
        private NetworkStream? _toDce;
        private NetworkStream? _toDte;

        public HdlcFaultProxy(ushort targetPort, Func<ProxiedFrame, FrameFate>? rule = null)
            : this("127.0.0.1", targetPort, IPAddress.Loopback, rule)
        {
        }

        /// <summary>
        /// A proxy that dials <paramref name="targetHost"/> (the DCE side) and listens on <paramref name="listenOn"/>
        /// for the DTE side. The cross tests use it between this machine and a WSL process, which 127.0.0.1 does not
        /// reach in either direction.
        /// </summary>
        public HdlcFaultProxy(string targetHost, ushort targetPort, IPAddress listenOn, Func<ProxiedFrame, FrameFate>? rule = null)
        {
            _targetHost = targetHost;
            _targetPort = targetPort;
            _rule = rule ?? (f => FrameFate.Pass);
            _listener = new TcpListener(listenOn, 0);
            _listener.Start();
            Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            Thread accepter = new Thread(AcceptLoop) { IsBackground = true, Name = "HdlcFaultProxy.accept" };
            accepter.Start();
        }

        /// <summary>The port the dialling end connects to.</summary>
        public ushort Port { get; }

        /// <summary>How many connections the proxy has carried.</summary>
        public int Connections => Volatile.Read(ref _connections);

        /// <summary>A copy of every frame seen, in the order they went by, both directions.</summary>
        public List<ProxiedFrame> Seen()
        {
            lock (_gate)
            {
                return new List<ProxiedFrame>(_seen);
            }
        }

        /// <summary>
        /// Cuts every open connection, both halves, as a network failure would.
        /// </summary>
        public void DropConnections()
        {
            TcpClient[] open;
            lock (_gate)
            {
                open = _open.ToArray();
                _open.Clear();
            }

            for (int i = 0; i < open.Length; i++)
            {
                try
                {
                    open[i].Close();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }
        }

        /// <summary>
        /// Puts a frame of our own on the line, as the far end would have sent it (FCS and framing added).
        /// </summary>
        /// <param name="direction">
        /// The way it goes.
        /// </param>
        /// <param name="body">
        /// Address, control and information, without FCS.
        /// </param>
        public void Inject(TrunkDirection direction, byte[] body)
        {
            byte[] wire = HdlcEncoder.Encode(body, HdlcFramerOptions.X25 with { SendAccm = HdlcFramerOptions.AccmNone });
            NetworkStream? target = direction == TrunkDirection.ToDce ? _toDce : _toDte;
            Assert_NotNull(target, "no open connection to inject into");
            lock (target!)
            {
                target.Write(wire, 0, wire.Length);
            }
        }

        private static void Assert_NotNull(object? o, string message)
        {
            if (o == null)
            {
                throw new InvalidOperationException(message);
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient dte;
                try
                {
                    dte = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    TcpClient dce = new TcpClient();
                    dce.Connect(_targetHost, _targetPort);
                    dte.NoDelay = true;
                    dce.NoDelay = true;
                    lock (_gate)
                    {
                        _open.Add(dte);
                        _open.Add(dce);
                    }

                    Interlocked.Increment(ref _connections);
                    _toDce = dce.GetStream();
                    _toDte = dte.GetStream();
                    NetworkStream fromDte = dte.GetStream();
                    NetworkStream fromDce = dce.GetStream();
                    Thread up = new Thread(() => Pump(fromDte, _toDce, TrunkDirection.ToDce)) { IsBackground = true };
                    Thread down = new Thread(() => Pump(fromDce, _toDte, TrunkDirection.ToDte)) { IsBackground = true };
                    up.Start();
                    down.Start();
                }
                catch (Exception)
                {
                    dte.Close();
                }
            }
        }

        private void Pump(NetworkStream from, NetworkStream to, TrunkDirection direction)
        {
            byte[] buffer = new byte[4096];
            List<byte> raw = new List<byte>();
            int index = 0;
            Dictionary<int, int> timesSeen = new Dictionary<int, int>();
            try
            {
                while (_running)
                {
                    int read = from.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < read; i++)
                    {
                        byte b = buffer[i];
                        if (b != 0x7E)
                        {
                            raw.Add(b);
                            continue;
                        }

                        if (raw.Count == 0)
                        {
                            continue;
                        }

                        byte[] frame = raw.ToArray();
                        raw.Clear();
                        Handle(frame, to, direction, index++, timesSeen);
                    }
                }
            }
            catch (Exception)
            {
                // The connection ended; close the other half so the far end notices too.
            }

            try
            {
                to.Close();
                from.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        private void Handle(byte[] raw, NetworkStream to, TrunkDirection direction, int index, Dictionary<int, int> timesSeen)
        {
            // Unstuff only as far as the first two octets.
            byte[] head = new byte[2];
            int got = 0;
            for (int i = 0; i < raw.Length && got < 2; i++)
            {
                byte v = raw[i];
                if (v == 0x7D && i + 1 < raw.Length)
                {
                    v = (byte)(raw[++i] ^ 0x20);
                }

                head[got++] = v;
            }

            byte[] body = new byte[raw.Length];
            int bodyLength = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                byte v = raw[i];
                if (v == 0x7D && i + 1 < raw.Length)
                {
                    v = (byte)(raw[++i] ^ 0x20);
                }

                body[bodyLength++] = v;
            }

            Array.Resize(ref body, bodyLength);
            ProxiedFrame info = new ProxiedFrame
            {
                Direction = direction,
                Index = index,
                Address = got > 0 ? head[0] : (byte)0,
                Control = got > 1 ? head[1] : (byte)0,
                Body = body,
            };

            if (info.IsInformation)
            {
                // N(S) plus 8 for each direction keeps one table per pump thread honest: this dictionary belongs to one pump.
                int key = info.Ns;
                timesSeen.TryGetValue(key, out int n);
                n++;
                timesSeen[key] = n;
                info.TimesSeen = n;
            }

            lock (_gate)
            {
                _seen.Add(info);
            }

            FrameFate fate = _rule(info);
            if (fate == FrameFate.Drop)
            {
                return;
            }

            byte[] onWire = raw;
            if (fate == FrameFate.Corrupt)
            {
                onWire = (byte[])raw.Clone();
                int at = onWire.Length / 2;
                while (at > 0 && (onWire[at] == 0x7D || onWire[at - 1] == 0x7D))
                {
                    at--;
                }

                byte flipped = (byte)(onWire[at] ^ 0x01);
                if (flipped == 0x7E || flipped == 0x7D)
                {
                    flipped = (byte)(onWire[at] ^ 0x02);
                }

                onWire[at] = flipped;
            }

            byte[] withFlags = new byte[onWire.Length + 2];
            withFlags[0] = 0x7E;
            Array.Copy(onWire, 0, withFlags, 1, onWire.Length);
            withFlags[withFlags.Length - 1] = 0x7E;

            try
            {
                lock (to)
                {
                    to.Write(withFlags, 0, withFlags.Length);
                    if (fate == FrameFate.Duplicate)
                    {
                        to.Write(withFlags, 0, withFlags.Length);
                    }
                }
            }
            catch (Exception)
            {
                // The far end is gone; the pump loop ends on its next read.
            }
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            DropConnections();
        }
    }

    /// <summary>
    /// A logger that keeps its lines so a test can look for a fragment.
    /// </summary>
    internal sealed class TrunkListLogger : ILogger
    {
        private readonly List<string> _lines = new List<string>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(logLevel + ": " + formatter(state, exception));
            }
        }

        public bool Contains(string fragment)
        {
            lock (_lines)
            {
                for (int i = 0; i < _lines.Count; i++)
                {
                    if (_lines[i].Contains(fragment, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public string All()
        {
            lock (_lines)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    /// <summary>
    /// One data packet as a port delivered it.
    /// </summary>
    internal sealed class ReceivedData
    {
        public ushort Lci;
        public byte[] Data = Array.Empty<byte>();
        public bool Q;
        public bool M;
        public bool D;
    }

    /// <summary>
    /// A DCE and a DTE HDLC-over-TCP port joined together (directly or through a fault proxy), with their taps and
    /// received data kept for the test to read.
    /// </summary>
    internal sealed class TrunkPair : IDisposable
    {
        public HdlcTcpSwitchPort Dce = null!;
        public HdlcTcpSwitchPort Dte = null!;
        public HdlcFaultProxy? Proxy;
        public readonly List<byte[]> DceIn = new List<byte[]>();
        public readonly List<byte[]> DceOut = new List<byte[]>();
        public readonly List<byte[]> DteIn = new List<byte[]>();
        public readonly List<byte[]> DteOut = new List<byte[]>();
        public readonly List<ReceivedData> AtDce = new List<ReceivedData>();
        public readonly List<ReceivedData> AtDte = new List<ReceivedData>();
        public readonly List<(ushort Lci, byte Cause)> ClearedAtDce = new List<(ushort, byte)>();
        public readonly List<(ushort Lci, byte Cause)> ClearedAtDte = new List<(ushort, byte)>();
        public readonly TrunkListLogger DceLog = new TrunkListLogger();
        public readonly TrunkListLogger DteLog = new TrunkListLogger();

        public static PortConfig Config(int id, PortMode mode, string host, ushort port, int t1 = 500)
        {
            return new PortConfig
            {
                Id = id,
                Type = PortType.Trunk,
                Transport = PortTransport.HdlcTcp,
                Mode = mode,
                Host = host,
                Port = port,
                LciBase = 1,
                LciCount = 16,
                DefaultWindowSize = 7,
                LapbT1Ms = t1,
            };
        }

        public static TrunkPair Create(Func<ProxiedFrame, FrameFate>? rule = null, int t1 = 500, bool startDte = true)
        {
            TrunkPair pair = new TrunkPair();
            pair.Dce = new HdlcTcpSwitchPort(Config(0, PortMode.Dce, "127.0.0.1", 0, t1), pair.DceLog);
            pair.Hook(pair.Dce, pair.DceIn, pair.DceOut, pair.AtDce, pair.ClearedAtDce);
            if (!pair.Dce.Start())
            {
                throw new InvalidOperationException("DCE did not start: " + pair.DceLog.All());
            }

            ushort dialPort = pair.Dce.BoundPort;
            if (rule != null)
            {
                pair.Proxy = new HdlcFaultProxy(dialPort, rule);
                dialPort = pair.Proxy.Port;
            }

            pair.Dte = new HdlcTcpSwitchPort(Config(1, PortMode.Dte, "127.0.0.1", dialPort, t1), pair.DteLog);
            pair.Hook(pair.Dte, pair.DteIn, pair.DteOut, pair.AtDte, pair.ClearedAtDte);
            if (startDte && !pair.Dte.Start())
            {
                throw new InvalidOperationException("DTE did not start: " + pair.DteLog.All());
            }

            return pair;
        }

        public void Hook(HdlcSwitchPortBase port, List<byte[]> inbound, List<byte[]> outbound, List<ReceivedData> data, List<(ushort, byte)> cleared)
        {
            port.PacketCaptured += (id, isIn, packet) =>
            {
                List<byte[]> list = isIn ? inbound : outbound;
                lock (list)
                {
                    list.Add(packet);
                }
            };
            port.DataReceived += (p, lci, bytes, length, q, m, d) =>
            {
                byte[] copy = new byte[length];
                Array.Copy(bytes, copy, length);
                lock (data)
                {
                    data.Add(new ReceivedData { Lci = lci, Data = copy, Q = q, M = m, D = d });
                }
            };
            port.CallCleared += (p, lci, cause, diag) =>
            {
                lock (cleared)
                {
                    cleared.Add((lci, cause));
                }
            };
        }

        public bool BothUp => Dce.PeerConnected && Dte.PeerConnected;

        public void WaitBothUp(int timeoutMs = 10000)
        {
            if (!SpinWait.SpinUntil(() => BothUp, timeoutMs))
            {
                throw new TimeoutException("the trunk did not come up. DCE log:\n" + DceLog.All() + "\nDTE log:\n" + DteLog.All());
            }
        }

        /// <summary>
        /// Places a call from <paramref name="caller"/> to the other port and accepts it there. Returns the caller's channel.
        /// </summary>
        public ushort Call(HdlcTcpSwitchPort caller, string called = "23420001", string calling = "23400001")
        {
            HdlcTcpSwitchPort callee = ReferenceEquals(caller, Dce) ? Dte : Dce;
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            PortIncomingCall onIncoming = (port, lci, c, g, cud) => callee.AcceptCall(lci);
            PortCallConnected onConnected = (port, lci) => connected.Set();
            callee.IncomingCall += onIncoming;
            caller.CallConnected += onConnected;
            try
            {
                ushort lci = caller.PlaceCall(called, calling, null);
                if (lci == 0)
                {
                    throw new InvalidOperationException("the call was not placed");
                }

                if (!connected.Wait(10000))
                {
                    throw new TimeoutException("the call was not accepted");
                }

                return lci;
            }
            finally
            {
                callee.IncomingCall -= onIncoming;
                caller.CallConnected -= onConnected;
            }
        }

        /// <summary>
        /// Sends a packet, waiting while the window is shut. Fails the test on a refusal that waiting cannot cure.
        /// </summary>
        public static void SendAll(HdlcSwitchPortBase port, ushort lci, byte[] data, bool q = false, bool m = false, bool d = false, int timeoutMs = 30000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                PortSendResult r = port.SendData(lci, data, q, m, d);
                if (r == PortSendResult.Sent)
                {
                    return;
                }

                if (r == PortSendResult.Failed)
                {
                    throw new InvalidOperationException("the port refused the packet for good");
                }

                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("the window never opened");
                }

                Thread.Sleep(2);
            }
        }

        public static int Count(List<byte[]> packets, X25PacketType type)
        {
            int n = 0;
            lock (packets)
            {
                for (int i = 0; i < packets.Count; i++)
                {
                    if (X25PacketParser.ParsePacket(packets[i]).PacketType == type)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        public void Dispose()
        {
            Dte?.Dispose();
            Dce?.Dispose();
            Proxy?.Dispose();
        }
    }
}
