//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// A trunk port that carries X.25 over LAPB over 0x7E HDLC framing on a serial line (RS-422 or a plain
    /// rx/tx cable, full duplex). The configuration name is <c>hdlc-serial</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framing, LAPB and X.25 are the same as for <see cref="HdlcTcpSwitchPort"/>, and live in
    /// <see cref="HdlcSwitchPortBase"/>. Only the line differs: here it is the serial device named by
    /// <c>device</c> in the configuration (<c>COM3</c> on Windows, <c>/dev/ttyUSB0</c> on Linux), opened with
    /// <c>baud</c>, <c>databits</c>, <c>parity</c>, <c>stopbits</c> and <c>flow</c>.
    /// </para>
    /// <para>
    /// A serial line has no listening end and no dialling end, so both ends send a SABM when the device is
    /// open and the LAPB layer settles two that cross. <c>mode</c> still says DCE or DTE, which decides the LAPB
    /// address pair as for TCP, so one end of a cable is <c>mode = dce</c> and the other <c>mode = dte</c>.
    /// </para>
    /// <para>
    /// A device that cannot be opened when the port starts stops the port: <see cref="HdlcSwitchPortBase.Start"/>
    /// returns false with an error that names the port and the device. A device that goes away while the port
    /// runs (a USB adapter pulled out) ends the link, clears the calls on it, and the port tries to open it again
    /// every <see cref="HdlcSwitchPortBase.RedialDelay"/> milliseconds.
    /// </para>
    /// <para>
    /// The device is opened through a function so a test can give the port the two ends of an in-memory line instead
    /// of a UART (see the second constructor). The default function opens a real <see cref="SerialPort"/>.
    /// </para>
    /// </remarks>
    public sealed class HdlcSerialSwitchPort : HdlcSwitchPortBase
    {
        private readonly Func<PortConfig, Stream> _openDevice;
        private Stream? _opened;

        /// <summary>
        /// Creates the port. The device is opened by <see cref="HdlcSwitchPortBase.Start"/>.
        /// </summary>
        /// <param name="config">
        /// The port's configuration: <c>SerialDevice</c>, <c>SerialBaud</c> and the line settings.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its link. Null reports nothing.
        /// </param>
        /// <exception cref="InvalidDataException">
        /// Thrown when a serial setting cannot work (for example 9 data bits). The message names the port and the key.
        /// </exception>
        public HdlcSerialSwitchPort(PortConfig config, ILogger? logger = null)
            : this(config, logger, OpenRealDevice)
        {
        }

        /// <summary>
        /// Creates the port with its own way of opening the line. For tests: the function returns the stream the
        /// port reads and writes, in place of a serial device.
        /// </summary>
        /// <param name="config">
        /// The port's configuration.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its link. Null reports nothing.
        /// </param>
        /// <param name="openDevice">
        /// Opens the line for the configuration, or throws if it cannot. Called when the port starts and again each
        /// time the line has been lost.
        /// </param>
        public HdlcSerialSwitchPort(PortConfig config, ILogger? logger, Func<PortConfig, Stream> openDevice)
            : base(config, logger, "serial")
        {
            _openDevice = openDevice ?? throw new ArgumentNullException(nameof(openDevice));
            Validate(config);
        }

        /// <inheritdoc/>
        protected override bool SendsSabmOnConnect => true;

        /// <inheritdoc/>
        protected override void OpenLine()
        {
            _opened = _openDevice(PortSettings);
            Logger.LogInformation("Port {Id} ({Mode} {Type}, HDLC over serial): device {Device} open at {Baud} baud",
                Id, PortSettings.Mode, PortSettings.Type, PortSettings.SerialDevice, PortSettings.SerialBaud);
        }

        /// <inheritdoc/>
        protected override Stream? TakeOpenedStream()
        {
            Stream? opened = _opened;
            _opened = null;
            return opened;
        }

        /// <inheritdoc/>
        protected override void CloseLine()
        {
            // The stream of an attached line is closed with its connection. One that was opened but never attached
            // is closed here.
            Stream? opened = _opened;
            _opened = null;
            opened?.Dispose();
        }

        /// <inheritdoc/>
        protected override string RetryText()
        {
            return "; opening the device again in " + RedialDelay / 1000 + " seconds";
        }

        /// <inheritdoc/>
        protected override void RunSupervisor()
        {
            while (IsRunning)
            {
                if (HasLine)
                {
                    Thread.Sleep(100);
                    continue;
                }

                string what = "open of " + PortSettings.SerialDevice;
                Stream stream;
                try
                {
                    NoteConnectAttempt();
                    stream = _openDevice(PortSettings);
                }
                catch (Exception ex)
                {
                    NoteConnectFailure(what, ex.Message);
                    WaitRedialDelay();
                    continue;
                }

                if (!IsRunning)
                {
                    stream.Dispose();
                    return;
                }

                AttachStream(stream, "device opened again");
            }
        }

        /// <summary>
        /// Checks the serial settings of a configuration. A value that cannot work is refused with the key named, not
        /// changed to something else.
        /// </summary>
        private static void Validate(PortConfig c)
        {
            string who = "Port " + c.Id + ": ";
            if (string.IsNullOrWhiteSpace(c.SerialDevice))
            {
                throw new InvalidDataException(who + "an hdlc-serial port needs 'device =' (for example COM3 or /dev/ttyUSB0)");
            }

            if (c.SerialBaud < 300 || c.SerialBaud > 921600)
            {
                throw new InvalidDataException(who + "baud = " + c.SerialBaud + " is not usable (300 to 921600)");
            }

            if (c.SerialDataBits != 7 && c.SerialDataBits != 8)
            {
                throw new InvalidDataException(who + "databits = " + c.SerialDataBits + " is not usable (7 or 8)");
            }

            if (ParseParity(c.SerialParity) == null)
            {
                throw new InvalidDataException(who + "parity = " + c.SerialParity + " is not usable (none, odd or even)");
            }

            if (c.SerialStopBits != 1 && c.SerialStopBits != 2)
            {
                throw new InvalidDataException(who + "stopbits = " + c.SerialStopBits + " is not usable (1 or 2)");
            }

            if (c.SerialFlow != "rtscts" && c.SerialFlow != "none")
            {
                throw new InvalidDataException(who + "flow = " + c.SerialFlow + " is not usable (rtscts or none)");
            }
        }

        private static Parity? ParseParity(string text)
        {
            switch (text)
            {
                case "none": return Parity.None;
                case "odd": return Parity.Odd;
                case "even": return Parity.Even;
                default: return null;
            }
        }

        /// <summary>
        /// Opens the real UART.
        /// </summary>
        private static Stream OpenRealDevice(PortConfig c)
        {
            SerialPort port = new SerialPort
            {
                PortName = c.SerialDevice,
                BaudRate = (int)c.SerialBaud,
                DataBits = c.SerialDataBits,
                Parity = ParseParity(c.SerialParity) ?? Parity.None,
                StopBits = c.SerialStopBits == 2 ? StopBits.Two : StopBits.One,
                Handshake = c.SerialFlow == "rtscts" ? Handshake.RequestToSend : Handshake.None,
                ReadTimeout = Timeout.Infinite,
                WriteTimeout = 5000,
                DtrEnable = true,
            };

            try
            {
                port.Open();
            }
            catch (Exception)
            {
                port.Dispose();
                throw;
            }

            return new SerialPortStream(port);
        }

        /// <summary>
        /// A stream over an open serial port that closes the port when it is disposed.
        /// </summary>
        private sealed class SerialPortStream : Stream
        {
            private readonly SerialPort _port;

            public SerialPortStream(SerialPort port)
            {
                _port = port;
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
                return _port.BaseStream.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _port.BaseStream.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                _port.BaseStream.Flush();
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
                if (disposing)
                {
                    try
                    {
                        _port.Dispose();
                    }
                    catch (Exception)
                    {
                        // Already closed, or the adapter is gone.
                    }
                }

                base.Dispose(disposing);
            }
        }
    }
}
