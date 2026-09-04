using System;

namespace RetroIO.Link
{
    /// <summary>
    /// Handler for raw bytes received from a link transport.
    /// </summary>
    /// <param name="transportName">
    /// Name of the transport that received the bytes.
    /// </param>
    /// <param name="data">
    /// The received bytes. Only valid for the duration of the callback; copy to retain.
    /// </param>
    public delegate void LinkTransportDataReceived(string transportName, ReadOnlySpan<byte> data);

    /// <summary>
    /// Handler for a transport becoming open (connected).
    /// </summary>
    /// <param name="transportName">
    /// Name of the transport that opened.
    /// </param>
    public delegate void LinkTransportOpened(string transportName);

    /// <summary>
    /// Handler for a transport closing (disconnected).
    /// </summary>
    /// <param name="transportName">
    /// Name of the transport that closed.
    /// </param>
    /// <param name="reason">
    /// Human-readable reason for the closure.
    /// </param>
    public delegate void LinkTransportClosed(string transportName, string reason);

    /// <summary>
    /// A raw byte-stream transport for a link (TCP socket or serial port).
    /// </summary>
    /// <remarks>
    /// This is the OS/medium abstraction below the framing codec.
    /// The X.25/LAPB stack does not care which medium carries the bytes, so a single codec-based link
    /// (see CodecLink) works over any implementation. It moves opaque bytes only; framing, FCS, and LAPB
    /// live above it.
    /// </remarks>
    public interface ILinkTransport : IDisposable
    {
        /// <summary>
        /// Unique name of this transport (usually the owning link's name).
        /// </summary>
        string Name { get; }

        /// <summary>
        /// The link type this transport represents (Serial or TcpLapb), for statistics and display.
        /// </summary>
        LinkType LinkType { get; }

        /// <summary>
        /// Whether the transport is currently open and able to move bytes.
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// Opens the transport (connect, listen/accept, or open the port).
        /// </summary>
        /// <returns>
        /// True if the open succeeded or was started; false on immediate failure.
        /// </returns>
        bool Open();

        /// <summary>
        /// Closes the transport.
        /// </summary>
        void Close();

        /// <summary>
        /// Writes raw bytes to the medium.
        /// </summary>
        /// <param name="data">
        /// The bytes to write. Only referenced for the duration of the call.
        /// </param>
        void Write(ReadOnlySpan<byte> data);

        /// <summary>
        /// Raised when raw bytes arrive from the medium.
        /// </summary>
        event LinkTransportDataReceived DataReceived;

        /// <summary>
        /// Raised when the transport becomes open.
        /// </summary>
        event LinkTransportOpened Opened;

        /// <summary>
        /// Raised when the transport closes.
        /// </summary>
        event LinkTransportClosed Closed;
    }
}
