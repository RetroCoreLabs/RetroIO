using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The server half of the request-response programming model (RR-LIB, chapter 4).
    /// </summary>
    /// <remarks>
    /// <para><b>Lifecycle</b></para>
    /// A server identifies itself with a name - which IS an XMSG port name - and then loops:
    ///  - wait for an event.
    ///  - on a connection indication, take the connection details and answer with a connection
    ///    response, at which point the client's confirmation event fires.
    ///  - on a request indication, take the request bytes, do the work, send the response.
    ///  - on a disconnect indication, acknowledge it to complete the teardown.
    /// Either party may start a disconnect, and it is not complete until the other side has
    /// performed its disconnect-indication call.
    /// <para><b>Why the connection phase carries data</b></para>
    /// The connection request may carry user data, so a server can demand identification before it
    /// accepts. That is the same mechanism the XROUT letter provides one level down: vet the caller
    /// before disclosing anything about yourself.
    /// </remarks>
    public interface IRrServer
    {
        /// <summary>
        /// Initialises the request-response layer and registers the server name (RRPBINIT).
        /// </summary>
        /// <param name="serverName">
        /// The name clients will address, equivalent to an XMSG port name. No client can connect
        /// before this has succeeded.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus Initialise(string serverName);

        /// <summary>
        /// Waits for the next event (RRPBWAIT).
        /// </summary>
        /// <param name="wanted">
        /// The set of events to wait for, formed by OR-ing the flags.
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
        /// <remarks>
        /// Exactly one event is reported per call even when several are pending, so a server must
        /// keep calling until the queue drains.
        /// </remarks>
        RrStatus Wait(RrEvent wanted, TimeSpan timeout, out RrEvent actual);

        /// <summary>
        /// Takes the details of an arrived connection request (RRPSCNI).
        /// </summary>
        /// <param name="connection">
        /// On return, the identifier of the new connection, used by every later call about it.
        /// </param>
        /// <param name="clientData">
        /// On return, any user data the client sent with its connection request; empty when none.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus AcceptConnectionIndication(out int connection, out byte[] clientData);

        /// <summary>
        /// Answers a connection request (RRPSCNR).
        /// </summary>
        /// <param name="connection">
        /// The connection being answered.
        /// </param>
        /// <param name="accept">
        /// True to establish the connection, false to refuse it.
        /// </param>
        /// <param name="serverData">
        /// User data to return to the client with the answer; may be empty.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus SendConnectionResponse(int connection, bool accept, ReadOnlySpan<byte> serverData);

        /// <summary>
        /// Takes the bytes of an arrived request (RRPSGTRQ).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection the request arrived on.
        /// </param>
        /// <param name="request">
        /// On return, the request bytes.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus GetRequest(out int connection, out byte[] request);

        /// <summary>
        /// Sends the response to a request (RRPSSNRS).
        /// </summary>
        /// <param name="connection">
        /// The connection to answer on.
        /// </param>
        /// <param name="response">
        /// The response bytes.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        /// <remarks>
        /// A client may have only one outstanding request per connection, so this always answers
        /// the request most recently taken from that connection.
        /// </remarks>
        RrStatus SendResponse(int connection, ReadOnlySpan<byte> response);

        /// <summary>
        /// Starts an orderly disconnect (RRPSDCRQ).
        /// </summary>
        /// <param name="connection">
        /// The connection to close.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus RequestDisconnect(int connection);

        /// <summary>
        /// Acknowledges a disconnect started by the client (RRPSDCIN).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection that was closed.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus AcceptDisconnectIndication(out int connection);

        /// <summary>
        /// Drops a connection immediately, without the orderly exchange (RRPSABRT).
        /// </summary>
        /// <param name="connection">
        /// The connection to abort.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus Abort(int connection);

        /// <summary>
        /// Releases everything the server holds (RRPSEND).
        /// </summary>
        /// <returns>
        /// The completion status.
        /// </returns>
        RrStatus End();
    }
}
