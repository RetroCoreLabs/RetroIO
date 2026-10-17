//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Modules
{
    /// <summary>
    /// The down interface a <see cref="PadClient"/> uses to place and tear down calls and to send data,
    /// without depending on any particular X.25 stack implementation.
    /// </summary>
    /// <remarks>
    /// This is the "interfaces down" half of the PAD seam: the PAD state machine calls these methods to
    /// drive the wire, while received events are delivered back to it through its public delivery methods
    /// (<see cref="PadClient.NotifyCallAccepted"/>, <see cref="PadClient.DeliverData"/>,
    /// <see cref="PadClient.DeliverClear"/>, <see cref="PadClient.DeliverInterrupt"/>). Two send methods
    /// are kept because the PAD forwards ordinary terminal data and qualified X.29 data through distinct
    /// paths in the runtime implementation.
    /// </remarks>
    public interface IPadTransport
    {
        /// <summary>
        /// Places an outgoing X.25 call.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Origin X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Call user data (PAD protocol identifier), or null.
        /// </param>
        /// <returns>
        /// True if the call was initiated.
        /// </returns>
        bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData);

        /// <summary>
        /// Sends a data packet on an established circuit.
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
        /// True if the data was accepted for transmission.
        /// </returns>
        bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit);

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
        void ForwardData(ushort lcn, byte[] data, int length);

        /// <summary>
        /// Clears (tears down) a circuit.
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
        void ClearCall(ushort lcn, byte cause, byte diagnostic);
    }
}
