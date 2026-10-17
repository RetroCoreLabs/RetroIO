//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Fluent builder for an XROUT standard message (the "letter").
    /// </summary>
    /// <remarks>
    /// Conceptually mirrors the XMSG send-side call sequence (XFGET a buffer, XFWHD
    /// the header, XFWRI the parameter data, XFSND) but as an in-memory builder rather
    /// than real SINTRAN monitor calls. See XMSG-API.md section 4.
    /// </remarks>
    public sealed class XroutMessageBuilder
    {
        private readonly XroutMessage _message;

        /// <summary>
        /// Initialises a new builder with an empty message.
        /// </summary>
        public XroutMessageBuilder()
        {
            _message = new XroutMessage();
        }

        /// <summary>
        /// Sets the serial number echoed by XROUT so replies can be matched.
        /// </summary>
        /// <param name="serial">
        /// The serial number; the high bit should be 0.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XroutMessageBuilder WithSerial(byte serial)
        {
            _message.Serial = serial;
            return this;
        }

        /// <summary>
        /// Sets the XROUT service code (byte 1 of the message).
        /// </summary>
        /// <param name="service">
        /// The service to request.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XroutMessageBuilder WithService(XroutService service)
        {
            _message.Service = (byte)service;
            return this;
        }

        /// <summary>
        /// Sets the raw service/status byte (byte 1 of the message).
        /// </summary>
        /// <param name="serviceByte">
        /// The raw service or status byte.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XroutMessageBuilder WithServiceByte(byte serviceByte)
        {
            _message.Service = serviceByte;
            return this;
        }

        /// <summary>
        /// Appends an integer parameter carrying raw bytes.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="data">
        /// The integer parameter data bytes.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XroutMessageBuilder AddInteger(int parameterNumber, byte[] data)
        {
            _message.AddParameter(XroutParameter.Integer(parameterNumber, data));
            return this;
        }

        /// <summary>
        /// Appends an integer parameter carrying a single big-endian 16-bit value.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="value">
        /// The 16-bit value to store.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XroutMessageBuilder AddInteger16(int parameterNumber, ushort value)
        {
            _message.AddParameter(XroutParameter.Integer16(parameterNumber, value));
            return this;
        }

        /// <summary>
        /// Appends a string parameter from ASCII text.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="text">
        /// The ASCII text to store.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XroutMessageBuilder AddString(int parameterNumber, string text)
        {
            _message.AddParameter(XroutParameter.Text(parameterNumber, text));
            return this;
        }

        /// <summary>
        /// Builds the XROUT message, updating its declared length from the parameters.
        /// </summary>
        /// <returns>
        /// The constructed <see cref="XroutMessage"/>.
        /// </returns>
        public XroutMessage Build()
        {
            // ToArray() recomputes and stores the remainder length so the message is
            // self-consistent even before it is serialised into a frame.
            _message.ToArray();
            return _message;
        }
    }
}
