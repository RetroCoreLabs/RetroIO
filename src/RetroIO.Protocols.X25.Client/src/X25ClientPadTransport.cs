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
        /// Origin X.121 address. The client always uses its own configured local address, so this must be
        /// empty or equal to <see cref="X25Client.LocalAddress"/>.
        /// </param>
        /// <param name="callUserData">
        /// Call user data (PAD protocol identifier), or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the client returned a circuit for the call.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="callingAddress"/> is not empty and differs from the client's own address:
        /// placing a call with another calling address is not implemented, and no call is placed.
        /// </exception>
        public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            if (!string.IsNullOrEmpty(callingAddress) && !string.Equals(callingAddress, _client.LocalAddress, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "X25ClientPadTransport.PlaceCall with a calling address other than the client's own is not implemented: the client always calls with its configured local address.");
            }

            _vc = _client.Call(calledAddress, callUserData);
            return _vc != null;
        }

        /// <summary>
        /// Sends a data packet on the PAD's one circuit.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number. When a circuit is open it must be that circuit's number.
        /// </param>
        /// <param name="data">
        /// Payload bytes; all of them are sent.
        /// </param>
        /// <param name="mBit">
        /// More-data bit. It must be <c>false</c>: sending a packet with the M bit set is not implemented.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit.
        /// </param>
        /// <param name="dBit">
        /// Delivery-confirmation bit. It must be <c>false</c>: sending a packet with the D bit set is not implemented.
        /// </param>
        /// <returns>
        /// The client's answer, <c>true</c> if the data was accepted for transmission;
        /// <c>false</c> when no circuit is open.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="mBit"/> or <paramref name="dBit"/> is <c>true</c>, or when
        /// <paramref name="lcn"/> names a channel other than the open circuit. Nothing is sent.
        /// </exception>
        public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
        {
            if (mBit)
            {
                throw new NotSupportedException("X25ClientPadTransport.SendData with the M bit set is not implemented.");
            }

            if (dBit)
            {
                throw new NotSupportedException("X25ClientPadTransport.SendData with the D bit set is not implemented.");
            }

            X25Vc? vc = _vc;
            if (vc == null)
            {
                return false;
            }

            RequireOwnChannel(vc, lcn, "SendData");
            return _client.Send(vc, data, data.Length, qBit);
        }

        /// <summary>
        /// Forwards ordinary terminal data assembled by the X.28 layer on the PAD's one circuit,
        /// with the Q bit clear.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number. When a circuit is open it must be that circuit's number.
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
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="lcn"/> names a channel other than the open circuit. Nothing is sent.
        /// </exception>
        public void ForwardData(ushort lcn, byte[] data, int length)
        {
            X25Vc? vc = _vc;
            if (vc != null)
            {
                RequireOwnChannel(vc, lcn, "ForwardData");
                _client.Send(vc, data, length, false);
            }
        }

        /// <summary>
        /// Clears the PAD's one circuit through the client and forgets it.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number. When a circuit is open it must be that circuit's number.
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
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="lcn"/> names a channel other than the open circuit. Nothing is cleared.
        /// </exception>
        public void ClearCall(ushort lcn, byte cause, byte diagnostic)
        {
            X25Vc? vc = _vc;
            if (vc != null)
            {
                RequireOwnChannel(vc, lcn, "ClearCall");
                _client.Clear(vc, cause, diagnostic);
                _vc = null;
            }
        }

        /// <summary>
        /// Refuses a request that names a channel other than the one circuit this adapter carries.
        /// </summary>
        /// <param name="vc">
        /// The open circuit.
        /// </param>
        /// <param name="lcn">
        /// The channel number the caller asked for.
        /// </param>
        /// <param name="operation">
        /// The operation name, used in the message.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="lcn"/> differs from the number of <paramref name="vc"/>.
        /// </exception>
        private static void RequireOwnChannel(X25Vc vc, ushort lcn, string operation)
        {
            if (lcn != vc.Lcn)
            {
                throw new NotSupportedException(
                    "X25ClientPadTransport." + operation + " on a channel other than the open circuit is not implemented: the adapter carries one circuit only.");
            }
        }
    }
}
