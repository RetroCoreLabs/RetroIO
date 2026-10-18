//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO.Ports;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RetroIO.Link
{
    /// <summary>
    /// Serial-port implementation of <see cref="ILinkTransport"/>.
    /// </summary>
    /// <remarks>
    /// Moves raw bytes over a physical (or virtual) serial port so any codec-based link — SLIP or HDLC —
    /// can run over a real serial line, which is SLIP's original medium (RFC 1055). A dedicated reader
    /// thread polls the port and raises <see cref="DataReceived"/>; framing, FCS, and LAPB live above this
    /// transport in <see cref="CodecLink"/>.
    ///
    /// Note: this class requires a real serial device, so it is validated by integration/hardware testing
    /// rather than unit tests. The reader-thread pattern mirrors the existing serial handling in the codebase.
    /// </remarks>
    public sealed class SerialLinkTransport : ILinkTransport
    {
        private readonly ILogger _logger;
        private readonly string _name;
        private readonly string _portName;
        private readonly int _baudRate;
        private readonly int _dataBits;
        private readonly StopBits _stopBits;
        private readonly Parity _parity;
        private readonly bool _rtsCts;
        private readonly byte[] _readBuffer = new byte[4096];
        private readonly object _lock = new object();

        private SerialPort? _port;
        private Thread? _readerThread;
        private volatile bool _running;
        private bool _disposed;

        /// <summary>
        /// Raised on the reader thread when raw bytes arrive from the serial port.
        /// </summary>
        /// <remarks>
        /// The bytes are in a buffer this transport reuses; they are only valid for the duration of
        /// the callback.
        /// </remarks>
        public event LinkTransportDataReceived? DataReceived;

        /// <summary>
        /// Raised when <see cref="Open"/> has opened the serial port and started the reader thread.
        /// </summary>
        public event LinkTransportOpened? Opened;

        /// <summary>
        /// Raised by <see cref="Close"/> when it closes a port, with the reason "closed".
        /// </summary>
        /// <remarks>
        /// It is not raised when the reader thread stops on a read error.
        /// </remarks>
        public event LinkTransportClosed? Closed;

        /// <summary>
        /// Creates a serial transport (does not open the port until <see cref="Open"/> is called).
        /// </summary>
        /// <param name="logger">
        /// Logger for diagnostics.
        /// </param>
        /// <param name="config">
        /// Serial link configuration providing the port name and line settings.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/> or <paramref name="config"/> is null.
        /// </exception>
        public SerialLinkTransport(ILogger logger, SerialLinkConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _name = config.Name;
            _portName = config.PortName;
            _baudRate = config.BaudRate;
            _dataBits = config.DataBits;
            _stopBits = config.StopBits;
            _parity = config.Parity;
            _rtsCts = config.RtsCtsFlowControl;
        }

        /// <summary>
        /// Gets the name of this transport, taken from the link configuration.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets the link type this transport represents, which is always Serial.
        /// </summary>
        public LinkType LinkType => LinkType.Serial;

        /// <summary>
        /// Gets a value indicating whether the serial port exists and is open.
        /// </summary>
        public bool IsOpen => _port?.IsOpen == true;

        /// <summary>
        /// Opens the serial port with the configured line settings and starts the reader thread.
        /// </summary>
        /// <remarks>
        /// When the port is already open this returns <c>true</c> at once and does nothing else.
        /// </remarks>
        /// <returns>
        /// <c>true</c> if the port is open; <c>false</c> if opening it failed (the error is logged).
        /// </returns>
        public bool Open()
        {
            lock (_lock)
            {
                if (IsOpen)
                {
                    return true;
                }

                try
                {
                    _port = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
                    {
                        Handshake = _rtsCts ? Handshake.RequestToSend : Handshake.None,
                        ReadTimeout = 500,
                        WriteTimeout = 1000
                    };
                    _port.Open();

                    _running = true;
                    _readerThread = new Thread(ReaderLoop)
                    {
                        Name = $"SerialTransport-{_name}",
                        IsBackground = true
                    };
                    _readerThread.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SerialLinkTransport[{Name}]: failed to open {Port}", _name, _portName);
                    return false;
                }
            }

            Opened?.Invoke(_name);
            return true;
        }

        /// <summary>
        /// Closes and releases the serial port, waits up to 2 seconds for the reader thread, and
        /// raises <see cref="Closed"/>.
        /// </summary>
        /// <remarks>
        /// Does nothing when no port exists. An error while closing is logged at debug level only.
        /// </remarks>
        public void Close()
        {
            lock (_lock)
            {
                if (_port == null)
                {
                    return;
                }

                _running = false;
                try
                {
                    if (_port.IsOpen)
                    {
                        _port.Close();
                    }
                    _port.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SerialLinkTransport[{Name}]: error while closing", _name);
                }
                finally
                {
                    _port = null;
                }
            }

            _readerThread?.Join(2000);
            _readerThread = null;
            Closed?.Invoke(_name, "closed");
        }

        /// <summary>
        /// Writes raw bytes to the serial port.
        /// </summary>
        /// <remarks>
        /// When the port is not open the bytes are dropped without a message. A write error is logged
        /// and not thrown.
        /// </remarks>
        /// <param name="data">
        /// The bytes to write. Copied to an array before the write, so only referenced for the
        /// duration of the call.
        /// </param>
        public void Write(ReadOnlySpan<byte> data)
        {
            SerialPort? port = _port;
            if (port == null || !port.IsOpen)
            {
                return;
            }

            // SerialPort has no Span-based Write overload, so copy to a temporary array.
            byte[] buffer = data.ToArray();
            lock (_lock)
            {
                try
                {
                    port.Write(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SerialLinkTransport[{Name}]: write failed", _name);
                }
            }
        }

        /// <summary>
        /// Closes the transport. Calling it again does nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
        }

        private void ReaderLoop()
        {
            while (_running)
            {
                SerialPort? port = _port;
                if (port == null || !port.IsOpen)
                {
                    break;
                }

                try
                {
                    int available = port.BytesToRead;
                    if (available > 0)
                    {
                        int read = port.Read(_readBuffer, 0, Math.Min(available, _readBuffer.Length));
                        if (read > 0)
                        {
                            DataReceived?.Invoke(_name, _readBuffer.AsSpan(0, read));
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (TimeoutException)
                {
                    // Normal when no data arrives within ReadTimeout.
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        _logger.LogError(ex, "SerialLinkTransport[{Name}]: reader error", _name);
                    }
                    break;
                }
            }
        }
    }
}
