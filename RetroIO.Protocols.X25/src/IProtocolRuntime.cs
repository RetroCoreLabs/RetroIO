using System.Collections.Generic;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Services;

namespace RetroIO.Protocols.X25
{
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
        /// <returns>True if the data was sent via XOT.</returns>
        bool SendDataViaXotIfMapped(ushort lcn, byte[] data, int length);

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
