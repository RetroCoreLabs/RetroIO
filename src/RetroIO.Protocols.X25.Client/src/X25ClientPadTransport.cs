//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// An <see cref="IPadTransport"/> that drives a <see cref="PadClient"/> over the clean
    /// <see cref="X25Client"/> (transport + XOT framing + packet layer), so the console PAD runs on the
    /// same clean X.25 stack as the other tools rather than on the legacy runtime.
    /// </summary>
    /// <remarks>
    /// The PAD is a single-circuit terminal, so the adapter tracks just the one virtual circuit it opened.
    /// The owner is responsible for feeding received events back to the PAD's delivery methods by wiring the
    /// client callbacks (see the console PAD program).
    /// </remarks>
    public sealed class X25ClientPadTransport : IPadTransport
    {
        private readonly X25Client _client;
        private X25Vc? _vc;

        /// <summary>
        /// Creates the adapter over an open <see cref="X25Client"/>.
        /// </summary>
        /// <param name="client">
        /// The client that carries the call.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="client"/> is null.
        /// </exception>
        public X25ClientPadTransport(X25Client client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// The virtual circuit the PAD is currently using, or null when disconnected.
        /// </summary>
        public X25Vc? Circuit => _vc;

        /// <summary>
        /// Places an outgoing X.25 call through the client and remembers the circuit it opened.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Origin X.121 address. It is not read: the client uses its own configured local address.
        /// </param>
        /// <param name="callUserData">
        /// Call user data (PAD protocol identifier), or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the client returned a circuit for the call.
        /// </returns>
        public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            // The client uses its configured local address as the calling address, which matches the PAD's.
            _vc = _client.Call(calledAddress, callUserData);
            return _vc != null;
        }

        /// <summary>
        /// Sends a data packet on the PAD's one circuit.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number. It is not read: the data always goes on the circuit this
        /// adapter opened.
        /// </param>
        /// <param name="data">
        /// Payload bytes; all of them are sent.
        /// </param>
        /// <param name="mBit">
        /// More-data bit. It is not read.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit.
        /// </param>
        /// <param name="dBit">
        /// Delivery-confirmation bit. It is not read.
        /// </param>
        /// <returns>
        /// The client's answer, <c>true</c> if the data was accepted for transmission;
        /// <c>false</c> when no circuit is open.
        /// </returns>
        public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
        {
            X25Vc? vc = _vc;
            if (vc == null)
            {
                return false;
            }

            return _client.Send(vc, data, data.Length, qBit);
        }

        /// <summary>
        /// Forwards ordinary terminal data assembled by the X.28 layer on the PAD's one circuit,
        /// with the Q bit clear.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number. It is not read.
        /// </param>
        /// <param name="data">
        /// Payload bytes.
        /// </param>
        /// <param name="length">
        /// Number of valid bytes in <paramref name="data"/>.
        /// </param>
        /// <remarks>
        /// Does nothing when no circuit is open. The client's send result is not reported.
        /// </remarks>
        public void ForwardData(ushort lcn, byte[] data, int length)
        {
            X25Vc? vc = _vc;
            if (vc != null)
            {
                _client.Send(vc, data, length, false);
            }
        }

        /// <summary>
        /// Clears the PAD's one circuit through the client and forgets it.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number. It is not read.
        /// </param>
        /// <param name="cause">
        /// X.25 clear cause code.
        /// </param>
        /// <param name="diagnostic">
        /// X.25 diagnostic code.
        /// </param>
        /// <remarks>
        /// Does nothing when no circuit is open.
        /// </remarks>
        public void ClearCall(ushort lcn, byte cause, byte diagnostic)
        {
            X25Vc? vc = _vc;
            if (vc != null)
            {
                _client.Clear(vc, cause, diagnostic);
                _vc = null;
            }
        }
    }
}
