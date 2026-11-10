//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Hub
{
    /// <summary>
    /// Writes every frame the hub repeats into a classic pcap file.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the hub captures at all</b></para>
    /// <para>
    /// Host-local traffic cannot be captured with Wireshark on this machine - npcap has no
    /// loopback adapter installed - and the hub is the one place every frame on the segment
    /// passes through exactly once. So the capture belongs here rather than in a machine.
    /// </para>
    /// <para><b>Why pcap and not a text log</b></para>
    /// <para>
    /// A text log records what the writer already understood. The whole reason to capture is to
    /// see the thing nobody thought to print. A pcap keeps every byte, and Wireshark plus the
    /// dissectors in this repository already read it.
    /// </para>
    /// <para><b>Format</b></para>
    /// <para>
    /// Classic pcap, little-endian, microsecond timestamps, link type 1 (Ethernet). The members
    /// speak raw Ethernet frames, so the frame bytes go in with nothing added and nothing
    /// stripped.
    /// </para>
    /// <para><b>Threading</b></para>
    /// <para>
    /// Frames arrive on one receive thread per member, so every write is taken under a lock and
    /// flushed immediately. Flushing on every frame costs speed and buys the thing that matters
    /// here: a capture that is complete up to the instant of a hang, rather than one missing the
    /// last buffer - which is always the part being looked for.
    /// </para>
    /// </remarks>
    public sealed class PcapWriter : IDisposable
    {
        /// <summary>
        /// Classic pcap magic for little-endian, microsecond timestamps.
        /// </summary>
        private const uint MagicMicroseconds = 0xA1B2C3D4;

        /// <summary>
        /// Link type 1: Ethernet.
        /// </summary>
        private const uint LinkTypeEthernet = 1;

        /// <summary>
        /// Longest frame stored. Anything longer is written truncated, and the pcap record says
        /// so through its captured-versus-original length pair.
        /// </summary>
        private const int SnapLength = 65535;

        /// <summary>
        /// Guards the stream: one receive thread per member writes into it.
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// The open stream, or null once disposed.
        /// </summary>
        private Stream? _file;

        /// <summary>
        /// Whether disposing this writer leaves the stream open for its owner.
        /// </summary>
        private readonly bool _leaveOpen;

        /// <summary>
        /// Scratch for one 16-byte record header, reused to keep the write path allocation free.
        /// </summary>
        private readonly byte[] _header = new byte[16];

        /// <summary>
        /// Frames written so far.
        /// </summary>
        private long _framesWritten;

        /// <summary>
        /// Creates the file and writes the pcap file header.
        /// </summary>
        /// <param name="path">
        /// Where to write. An existing file is replaced.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the file cannot be created.
        /// </exception>
        public PcapWriter(string path)
            : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), path, leaveOpen: false)
        {
        }

        /// <summary>
        /// Writes the pcap file header to a stream the caller owns.
        /// </summary>
        /// <param name="stream">
        /// The stream to write. It is left open when this writer is disposed.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="stream"/> is null.
        /// </exception>
        /// <remarks>
        /// Added 2026-11-10 for the management API, which builds a pcap of the hub's recent frames
        /// straight into an HTTP response rather than into a file.
        /// </remarks>
        public PcapWriter(Stream stream)
            : this(stream ?? throw new ArgumentNullException(nameof(stream)), string.Empty, leaveOpen: true)
        {
        }

        /// <summary>
        /// Writes the pcap file header and keeps the stream.
        /// </summary>
        /// <param name="stream">
        /// The stream to write.
        /// </param>
        /// <param name="path">
        /// The file name, or empty for a stream that is not a file.
        /// </param>
        /// <param name="leaveOpen">
        /// Whether disposing leaves the stream open.
        /// </param>
        private PcapWriter(Stream stream, string path, bool leaveOpen)
        {
            Path = path;
            _file = stream;
            _leaveOpen = leaveOpen;

            byte[] fileHeader = new byte[24];
            WriteUInt32(fileHeader, 0, MagicMicroseconds);
            WriteUInt16(fileHeader, 4, 2);                 // major version
            WriteUInt16(fileHeader, 6, 4);                 // minor version
            WriteUInt32(fileHeader, 8, 0);                 // this zone: UTC
            WriteUInt32(fileHeader, 12, 0);                // timestamp accuracy, always 0
            WriteUInt32(fileHeader, 16, SnapLength);
            WriteUInt32(fileHeader, 20, LinkTypeEthernet);

            _file.Write(fileHeader, 0, fileHeader.Length);
            _file.Flush();
        }

        /// <summary>
        /// The file being written, or empty when writing to a stream.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// How many frames have been stored.
        /// </summary>
        public long FramesWritten
        {
            get { return System.Threading.Interlocked.Read(ref _framesWritten); }
        }

        /// <summary>
        /// Stores one frame, stamped with the current time.
        /// </summary>
        /// <param name="frame">
        /// The buffer holding the frame.
        /// </param>
        /// <param name="length">
        /// How many bytes of <paramref name="frame"/> are the frame.
        /// </param>
        public void Write(byte[] frame, int length)
        {
            Write(frame, length, DateTime.UtcNow);
        }

        /// <summary>
        /// Stores one frame with the time it was seen.
        /// </summary>
        /// <param name="frame">
        /// The buffer holding the frame.
        /// </param>
        /// <param name="length">
        /// How many bytes of <paramref name="frame"/> are the frame.
        /// </param>
        /// <param name="timeUtc">
        /// When the frame arrived, in UTC. A frame taken from the hub's buffer keeps the time it
        /// arrived, not the time it was exported.
        /// </param>
        public void Write(byte[] frame, int length, DateTime timeUtc)
        {
            if (frame == null || length <= 0)
            {
                return;
            }

            int captured = length;
            if (captured > SnapLength)
            {
                captured = SnapLength;
            }

            long ticks = timeUtc.Ticks - DateTime.UnixEpoch.Ticks;
            uint seconds = (uint)(ticks / TimeSpan.TicksPerSecond);
            uint microseconds = (uint)((ticks % TimeSpan.TicksPerSecond) / 10);

            lock (_lock)
            {
                Stream? file = _file;
                if (file == null)
                {
                    return;
                }

                WriteUInt32(_header, 0, seconds);
                WriteUInt32(_header, 4, microseconds);
                WriteUInt32(_header, 8, (uint)captured);     // bytes stored here
                WriteUInt32(_header, 12, (uint)length);      // bytes on the wire

                file.Write(_header, 0, _header.Length);
                file.Write(frame, 0, captured);
                file.Flush();
            }

            System.Threading.Interlocked.Increment(ref _framesWritten);
        }

        /// <summary>
        /// Closes the file. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_file != null)
                {
                    _file.Flush();
                    if (!_leaveOpen)
                    {
                        _file.Dispose();
                    }

                    _file = null;
                }
            }
        }

        /// <summary>
        /// Stores a 32-bit value little-endian.
        /// </summary>
        /// <param name="buffer">
        /// Destination.
        /// </param>
        /// <param name="offset">
        /// Where to start.
        /// </param>
        /// <param name="value">
        /// The value.
        /// </param>
        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// Stores a 16-bit value little-endian.
        /// </summary>
        /// <param name="buffer">
        /// Destination.
        /// </param>
        /// <param name="offset">
        /// Where to start.
        /// </param>
        /// <param name="value">
        /// The value.
        /// </param>
        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }
}
