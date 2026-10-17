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

        /// <inheritdoc/>
        public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            if (Module == null)
            {
                return false;
            }

            return _runtime.PlaceX25CallWithModule(calledAddress, callingAddress, callUserData, Module);
        }

        /// <inheritdoc/>
        public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
        {
            return _runtime.SendX25Data(lcn, data, mBit, qBit, dBit);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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
