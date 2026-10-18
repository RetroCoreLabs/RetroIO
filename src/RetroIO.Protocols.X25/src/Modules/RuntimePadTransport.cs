//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Modules
{
    /// <summary>
    /// An <see cref="IPadTransport"/> that drives a <see cref="PadClient"/> over the concrete
    /// <see cref="IProtocolRuntime"/>: outgoing calls, data and clears delegate to the runtime, while
    /// received packets reach the PAD through its <see cref="IProtocolModule"/> registration.
    /// </summary>
    /// <remarks>
    /// This preserves the historical runtime behaviour exactly: ordinary terminal data goes through the
    /// XOT-mapped path when the channel is XOT-mapped and falls back to the packet layer otherwise, while
    /// qualified/X.29 data goes through the runtime's data-send entry point.
    /// </remarks>
    public sealed class RuntimePadTransport : IPadTransport
    {
        private readonly IProtocolRuntime _runtime;

        /// <summary>
        /// The PAD module the runtime binds outgoing calls to. Set by the owning <see cref="PadClient"/>
        /// once it is constructed.
        /// </summary>
        public IProtocolModule? Module { get; set; }

        /// <summary>
        /// Creates the adapter over a protocol runtime.
        /// </summary>
        /// <param name="runtime">
        /// The runtime that carries the calls.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="runtime"/> is null.
        /// </exception>
        public RuntimePadTransport(IProtocolRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <summary>
        /// Places an outgoing X.25 call through the runtime, bound to <see cref="Module"/>.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Origin X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Call user data (PAD protocol identifier), or <c>null</c>.
        /// </param>
        /// <returns>
        /// The runtime's answer to <c>PlaceX25CallWithModule</c>; <c>false</c> without calling
        /// the runtime when <see cref="Module"/> is not set.
        /// </returns>
        public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            if (Module == null)
            {
                return false;
            }

            return _runtime.PlaceX25CallWithModule(calledAddress, callingAddress, callUserData, Module);
        }

        /// <summary>
        /// Sends a data packet on an established circuit through the runtime's
        /// <c>SendX25Data</c>.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number.
        /// </param>
        /// <param name="data">
        /// Payload bytes.
        /// </param>
        /// <param name="mBit">
        /// More-data bit.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit.
        /// </param>
        /// <param name="dBit">
        /// Delivery-confirmation bit.
        /// </param>
        /// <returns>
        /// The runtime's answer: <c>true</c> if the data was accepted for transmission.
        /// </returns>
        public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
        {
            return _runtime.SendX25Data(lcn, data, mBit, qBit, dBit);
        }

        /// <summary>
        /// Forwards ordinary terminal data assembled by the X.28 layer on an established circuit.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number.
        /// </param>
        /// <param name="data">
        /// Payload bytes.
        /// </param>
        /// <param name="length">
        /// Number of valid bytes in <paramref name="data"/>.
        /// </param>
        /// <remarks>
        /// The data goes first to the runtime's XOT-mapped path. Only when the channel is not
        /// XOT-mapped at all is it sent through <c>SendX25Data</c> with all three bits clear. A
        /// flow-control refusal on the mapped path is not retried on the other path. Neither result
        /// is reported to the caller.
        /// </remarks>
        public void ForwardData(ushort lcn, byte[] data, int length)
        {
            // Prefer the XOT-mapped path; fall back to the packet layer ONLY when the channel is
            // not XOT-mapped at all. A flow-control refusal must not fall back: the peer sent RNR
            // or our window is full, and pushing the same data down the packet layer would put it
            // on the wire anyway - back-pressure obeyed in one place and ignored in the next.
            if (_runtime.SendMappedData(lcn, data, length) == MappedSendOutcome.NotMapped)
            {
                // Not carried by a link: the runtime finds the bound service, or delivers it in
                // process. Until 2026-09-06 this went straight to the old packet-layer engine and
                // skipped the service lookup that SendX25Data does.
                byte[] payload = data.Length == length ? data : data.AsSpan(0, length).ToArray();
                _runtime.SendX25Data(lcn, payload, false, false, false);
            }
        }

        /// <summary>
        /// Clears (tears down) a circuit through the runtime's <c>ClearCallByLocalLcn</c>.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number.
        /// </param>
        /// <param name="cause">
        /// X.25 clear cause code.
        /// </param>
        /// <param name="diagnostic">
        /// X.25 diagnostic code.
        /// </param>
        public void ClearCall(ushort lcn, byte cause, byte diagnostic)
        {
            // A channel carried by a connection clears through that connection's engine; one that
            // runs in process just tells its owner. Until 2026-09-06 every clear went to the old
            // process-wide layer, which had no XOT channels at all, so a PAD disconnect over XOT put
            // nothing on the wire (pinned by ProtocolRuntimeWirePinTests).
            _runtime.ClearCallByLocalLcn(lcn, cause, diagnostic);
        }
    }
}
