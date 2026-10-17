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

        /// <inheritdoc/>
        public event LinkTransportDataReceived? DataReceived;

        /// <inheritdoc/>
        public event LinkTransportOpened? Opened;

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public string Name => _name;

        /// <inheritdoc/>
        public LinkType LinkType => LinkType.Serial;

        /// <inheritdoc/>
        public bool IsOpen => _port?.IsOpen == true;

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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
