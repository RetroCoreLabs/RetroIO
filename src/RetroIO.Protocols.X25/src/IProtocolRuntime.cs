using System.Collections.Generic;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.DeleteMe;

namespace RetroIO.Protocols.X25
{
    /// <summary>
    /// The result of offering data to the XOT send path.
    /// </summary>
    /// <remarks>
    /// Three outcomes, because two of them used to be the same <c>false</c> and the caller then
    /// treated a flow-control refusal as "this is not an XOT circuit" and sent the data down the
    /// packet layer anyway. See <see cref="IProtocolRuntime.SendDataViaXotIfMapped"/>.
    /// </remarks>
    public enum XotSendOutcome
    {
        /// <summary>The channel is not mapped to an XOT connection; try another path.</summary>
        NotXotMapped = 0,

        /// <summary>The packet went out on the XOT connection.</summary>
        Sent = 1,

        /// <summary>
        /// The channel IS an XOT circuit, but X.25 flow control would not let the packet out - the
        /// peer sent RNR, or our send window is full and no acknowledgement arrived in time. The
        /// data was not sent and no sequence number was consumed. Do NOT reroute it.
        /// </summary>
        RefusedByFlowControl = 2
    }

    /// <summary>
    /// The runtime services that the X.25 / SINTRAN layers, modules, and services depend on.
    /// </summary>
    /// <remarks>
    /// This abstraction lets those classes depend on the runtime without referencing the concrete
    /// <c>ProtocolRuntime</c> type, which breaks the X.25 &lt;-&gt; Runtime dependency cycle so the X.25 and
    /// SINTRAN code can be extracted into their own assemblies. <c>ProtocolRuntime</c> implements it, and
    /// this interface moves into the X.25 assembly with the layers that consume it.
    /// </remarks>
    public interface IProtocolRuntime
    {
        /// <summary>
        /// The X.25 packet layer.
        /// </summary>
        X25Layer X25Layer { get; }

        /// <summary>
        /// The packet history bus: announce received and transmitted packets here for the history
        /// and display code. Replaces the old layer's FirePacketReceived/FirePacketTransmitted role.
        /// </summary>
        Services.X25PacketHistory PacketHistory { get; }

        /// <summary>
        /// Returns the modules currently registered with the runtime, keyed by name.
        /// </summary>
        /// <returns>
        /// A dictionary of registered protocol modules.
        /// </returns>
        Dictionary<string, IProtocolModule> GetRegisteredModules();

        /// <summary>
        /// Places an outgoing X.25 call on behalf of a calling module.
        /// </summary>
        /// <param name="calledAddress">Destination X.121 address.</param>
        /// <param name="callingAddress">Source X.121 address.</param>
        /// <param name="userdata">Optional call user data.</param>
        /// <param name="callingModule">The module initiating the call.</param>
        /// <returns>True if the call was initiated.</returns>
        bool PlaceX25CallWithModule(string calledAddress, string callingAddress, byte[]? userdata, IProtocolModule callingModule);

        /// <summary>
        /// Sends X.25 data on a logical channel.
        /// </summary>
        /// <param name="lcn">Logical channel number.</param>
        /// <param name="data">Data to send.</param>
        /// <param name="mBit">More-data bit.</param>
        /// <param name="qBit">Qualifier bit.</param>
        /// <param name="dBit">Delivery-confirmation bit.</param>
        /// <returns>True if the data was accepted for transmission.</returns>
        bool SendX25Data(ushort lcn, byte[] data, bool mBit = false, bool qBit = false, bool dBit = false);

        /// <summary>
        /// Allocates a free logical channel number.
        /// </summary>
        /// <returns>An available LCN.</returns>
        ushort FindAvailableLCN();

        /// <summary>
        /// Registers an XOT connection against a local logical channel.
        /// </summary>
        /// <param name="lcn">Local logical channel number.</param>
        /// <param name="connection">The XOT connection object.</param>
        /// <param name="remoteLcn">The remote logical channel number.</param>
        void RegisterXotConnection(ushort lcn, object? connection, ushort remoteLcn);

        /// <summary>
        /// Removes the XOT connection registered for a logical channel.
        /// </summary>
        /// <param name="lcn">Local logical channel number.</param>
        void UnregisterXotConnection(ushort lcn);

        /// <summary>
        /// Unbinds any module bound to a logical channel.
        /// </summary>
        /// <param name="lcn">Logical channel number.</param>
        void UnbindModule(ushort lcn);

        /// <summary>
        /// Records a pending outbound XOT call mapping before the transport is finalized.
        /// </summary>
        /// <param name="localLcn">Local logical channel number.</param>
        /// <param name="remoteLcn">Remote logical channel number.</param>
        void RegisterOutboundXotCallDeferred(ushort localLcn, ushort remoteLcn);

        /// <summary>
        /// Clears a call on a local LCN carried by an XOT endpoint. False when the LCN is not
        /// XOT-mapped, so the caller can fall back to the link path.
        /// </summary>
        bool ClearXotCall(ushort lcn, byte cause, byte diagnostic);

        /// <summary>
        /// Finalizes a deferred outbound XOT call by attaching its transport.
        /// </summary>
        /// <param name="localLcn">Local logical channel number.</param>
        /// <param name="transport">The transport carrying the call.</param>
        void FinalizeOutboundXotCall(ushort localLcn, object transport);

        /// <summary>
        /// Sends data via XOT if the logical channel is mapped to an XOT connection.
        /// </summary>
        /// <param name="lcn">Logical channel number.</param>
        /// <param name="data">Data to send.</param>
        /// <param name="length">Number of valid bytes in <paramref name="data"/>.</param>
        /// <returns>What happened - see <see cref="XotSendOutcome"/>.</returns>
        /// <remarks>
        /// This used to return a bool, and callers read false as "not an XOT circuit, use the
        /// packet layer instead". That is wrong for the other reason it can fail: flow control.
        /// A circuit whose peer is busy, or whose window is full, would silently take the serial
        /// packet-layer path and be reported to the application as sent - the one thing flow
        /// control exists to prevent. The three outcomes are now distinct.
        /// </remarks>
        XotSendOutcome SendDataViaXotIfMapped(ushort lcn, byte[] data, int length);

        /// <summary>
        /// Binds a local protocol service to a logical channel.
        /// </summary>
        /// <param name="lcn">Logical channel number.</param>
        /// <param name="service">The protocol service to bind.</param>
        void BindServiceToLcn(ushort lcn, IProtocolService service);

        /// <summary>
        /// Binds a protocol module to a logical channel.
        /// </summary>
        /// <param name="lcn">Logical channel number.</param>
        /// <param name="module">The protocol module to bind.</param>
        void BindLcnToModule(ushort lcn, IProtocolModule module);

        /// <summary>
        /// Routes an X.25 packet event to the module bound to its logical channel.
        /// </summary>
        /// <param name="lcn">Logical channel number.</param>
        /// <param name="eventArgs">The X.25 packet event to route.</param>
        void RouteX25EventToBoundModule(ushort lcn, X25PacketEventArgs eventArgs);
    }
}
