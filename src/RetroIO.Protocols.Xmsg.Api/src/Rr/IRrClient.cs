//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The client half of the request-response programming model (RR-LIB, chapter 4).
    /// </summary>
    /// <remarks>
    /// <para><b>Two levels, and which to use</b></para>
    /// The manual splits the client calls in two and recommends the HIGH-level pair
    /// (<see cref="Select"/> and <see cref="Transact"/>) whenever their restrictions are
    /// acceptable: each folds a whole low-level sequence into one call and blocks until it
    /// completes. Use the low-level calls when any of the following is true:
    ///  - you need several requests outstanding to different servers at once.
    ///  - the program is also a server.
    ///  - you cannot afford to block until the response comes back.
    /// <para><b>One request per connection</b></para>
    /// A client must receive the response before it may send another request on the SAME
    /// connection. With several connections it may have one request outstanding on each, but each
    /// response is signalled separately and must be waited for individually.
    /// </remarks>
    public interface IRrClient
    {
        /// <summary>
        /// Initialises the request-response layer for this client (RRPCINIT).
        /// </summary>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus Initialise();

        /// <summary>
        /// Waits for the next event (RRPBWAIT).
        /// </summary>
        /// <param name="wanted">
        /// The set of events to wait for.
        /// </param>
        /// <param name="timeout">
        /// How long to wait before reporting <see cref="RrEvent.Timeout"/>.
        /// </param>
        /// <param name="actual">
        /// On return, the single event that occurred.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus Wait(RrEvent wanted, TimeSpan timeout, out RrEvent actual);

        /// <summary>
        /// Asks a named server for a connection (RRPCCNRQ).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or null (or empty) for the local system. The implementation
        /// <see cref="RrClient"/> does not implement a named system and throws for it.
        /// </param>
        /// <param name="serverName">
        /// The server's name.
        /// </param>
        /// <param name="clientData">
        /// User data to send with the request, typically an identification the server will check.
        /// </param>
        /// <param name="connection">
        /// On return, the identifier of the pending connection.
        /// </param>
        /// <returns>
        /// The completion status. The connection is not usable until the confirmation event has
        /// been received and processed.
        /// </returns>
        RrStatus RequestConnection(
            string? systemName,
            string serverName,
            ReadOnlySpan<byte> clientData,
            out int connection);

        /// <summary>
        /// Completes connection establishment after the confirmation event (RRPCCNCF).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection that is now established.
        /// </param>
        /// <param name="serverData">
        /// On return, any user data the server returned with its answer.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus ConfirmConnection(out int connection, out byte[] serverData);

        /// <summary>
        /// Sends a request without waiting for the response (RRPCSNRQ).
        /// </summary>
        /// <param name="connection">
        /// The established connection to send on.
        /// </param>
        /// <param name="request">
        /// The request bytes.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus SendRequest(int connection, ReadOnlySpan<byte> request);

        /// <summary>
        /// Takes the response after a response-indication event (RRPCGTRS).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection the response arrived on.
        /// </param>
        /// <param name="response">
        /// On return, the response bytes.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus GetResponse(out int connection, out byte[] response);

        /// <summary>
        /// Connects to a server in one blocking call (the high-level select).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or null (or empty) for the local system. The implementation
        /// <see cref="RrClient"/> does not implement a named system and throws for it.
        /// </param>
        /// <param name="serverName">
        /// The server's name.
        /// </param>
        /// <param name="clientData">
        /// User data to send with the connection request.
        /// </param>
        /// <param name="serverData">
        /// On return, any user data the server returned.
        /// </param>
        /// <param name="connection">
        /// On return, the established connection.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        /// <remarks>
        /// Equivalent to a connection request, a wait, and a connection confirmation performed back
        /// to back.
        /// </remarks>
        RrStatus Select(
            string? systemName,
            string serverName,
            ReadOnlySpan<byte> clientData,
            out byte[] serverData,
            out int connection);

        /// <summary>
        /// Sends a request and returns its response in one blocking call (the high-level send).
        /// </summary>
        /// <param name="connection">
        /// The established connection.
        /// </param>
        /// <param name="request">
        /// The request bytes.
        /// </param>
        /// <param name="response">
        /// On return, the response bytes.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus Transact(int connection, ReadOnlySpan<byte> request, out byte[] response);

        /// <summary>
        /// Starts an orderly disconnect (RRPCDCRQ).
        /// </summary>
        /// <param name="connection">
        /// The connection to close.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus RequestDisconnect(int connection);

        /// <summary>
        /// Acknowledges a disconnect started by the server (RRPCDCIN).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection that was closed.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus AcceptDisconnectIndication(out int connection);

        /// <summary>
        /// Drops a connection immediately, without the orderly exchange (RRPCABRT).
        /// </summary>
        /// <param name="connection">
        /// The connection to abort.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus Abort(int connection);

        /// <summary>
        /// Releases everything the client holds (RRPCEND).
        /// </summary>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus End();
    }
}
