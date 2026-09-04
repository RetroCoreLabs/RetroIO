using System;
using System.Text;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Drives a request-response server and client through the phases chapter 4 of the COSMOS
    /// Programmer Guide describes: connect, transfer, disconnect.
    /// </summary>
    public sealed class RrSessionTests
    {
        /// <summary>
        /// The whole low-level sequence: connection request, indication, response, confirmation,
        /// request, response, orderly disconnect.
        /// </summary>
        [Fact]
        public void LowLevelSequence_RunsEndToEnd()
        {
            Harness harness = new Harness();

            // 1. The client asks the NAME for a connection, carrying an identification.
            int clientConnection;
            Assert.True(harness.Client.RequestConnection(
                null, "*DEMO", Encoding.ASCII.GetBytes("SYSTEM"), out clientConnection).IsOk);

            // 2. The server sees a connection indication and takes it.
            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.ConnectionIndication, actual);

            int serverConnection;
            byte[] identification;
            Assert.True(harness.Server.AcceptConnectionIndication(out serverConnection, out identification).IsOk);
            Assert.Equal("SYSTEM", Encoding.ASCII.GetString(identification));

            // 3. The server accepts, and the client confirms.
            Assert.True(harness.Server.SendConnectionResponse(
                serverConnection, true, Encoding.ASCII.GetBytes("READY")).IsOk);

            Assert.True(harness.Client.Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.ConnectionConfirmation, actual);

            byte[] greeting;
            Assert.True(harness.Client.ConfirmConnection(out clientConnection, out greeting).IsOk);
            Assert.Equal("READY", Encoding.ASCII.GetString(greeting));
            Assert.Equal(serverConnection, clientConnection);

            // 4. Data transfer.
            Assert.True(harness.Client.SendRequest(clientConnection, Encoding.ASCII.GetBytes("LIST-FILES")).IsOk);

            Assert.True(harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.RequestIndication, actual);

            int onConnection;
            byte[] request;
            Assert.True(harness.Server.GetRequest(out onConnection, out request).IsOk);
            Assert.Equal("LIST-FILES", Encoding.ASCII.GetString(request));
            Assert.True(harness.Server.SendResponse(onConnection, Encoding.ASCII.GetBytes("FILE 0")).IsOk);

            Assert.True(harness.Client.Wait(RrEvent.ResponseIndication, TimeSpan.Zero, out actual).IsOk);
            byte[] response;
            Assert.True(harness.Client.GetResponse(out onConnection, out response).IsOk);
            Assert.Equal("FILE 0", Encoding.ASCII.GetString(response));

            // 5. Orderly disconnect, initiated by the client.
            Assert.True(harness.Client.RequestDisconnect(clientConnection).IsOk);
            Assert.True(harness.Server.Wait(RrEvent.DisconnectIndication, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.DisconnectIndication, actual);

            int closed;
            Assert.True(harness.Server.AcceptDisconnectIndication(out closed).IsOk);
            Assert.Equal(serverConnection, closed);
            Assert.Equal(0, harness.Server.ConnectionCount);
        }

        /// <summary>
        /// The high-level calls fold the same sequence into two blocking calls.
        /// </summary>
        [Fact]
        public void HighLevelCalls_SelectAndTransact()
        {
            Harness harness = new Harness();

            // Select needs the server to answer in between, so drive the server's side after the
            // letter has been forwarded.
            int connection;
            Assert.True(harness.Client.RequestConnection(
                null, "*DEMO", Array.Empty<byte>(), out connection).IsOk);
            harness.ServerAcceptsPendingConnection(out connection);

            byte[] serverData;
            RrEvent actual;
            harness.Client.Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual);
            harness.Client.ConfirmConnection(out connection, out serverData);

            // Transact: send and read the response in one call, with the server answering between.
            Assert.True(harness.Client.SendRequest(connection, Encoding.ASCII.GetBytes("PING")).IsOk);
            harness.ServerAnswers(Encoding.ASCII.GetBytes("PONG"));

            harness.Client.Wait(RrEvent.ResponseIndication, TimeSpan.Zero, out actual);
            int on;
            byte[] response;
            harness.Client.GetResponse(out on, out response);
            Assert.Equal("PONG", Encoding.ASCII.GetString(response));
        }

        /// <summary>
        /// A refused connection reports failure and leaves no connection behind.
        /// </summary>
        [Fact]
        public void RejectedConnection_LeavesNothingOpen()
        {
            Harness harness = new Harness();

            int connection;
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection);

            RrEvent actual;
            harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual);
            int serverConnection;
            byte[] data;
            harness.Server.AcceptConnectionIndication(out serverConnection, out data);
            harness.Server.SendConnectionResponse(serverConnection, false, Array.Empty<byte>());

            harness.Client.Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual);
            byte[] serverData;
            RrStatus confirmed = harness.Client.ConfirmConnection(out connection, out serverData);

            Assert.False(confirmed.IsOk);
            Assert.Equal(0, harness.Client.ConnectionCount);
            Assert.Equal(0, harness.Server.ConnectionCount);
        }

        /// <summary>
        /// A letter to a name nobody registered fails the way XROUT fails it.
        /// </summary>
        [Fact]
        public void UnknownName_FailsWithXrunn()
        {
            Harness harness = new Harness();

            int connection;
            RrStatus status = harness.Client.RequestConnection(
                null, "*NOSUCHSERVER", Array.Empty<byte>(), out connection);

            Assert.False(status.IsOk);
            Assert.Equal((int)XroutError.XRUNN, status.Value);
        }

        /// <summary>
        /// A connection port stops accepting letters once its capacity is used up, and accepts
        /// again once a session ends.
        /// </summary>
        [Fact]
        public void ConnectionPortCapacity_GatesAndRecovers()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XroutDirectory directory = new XroutDirectory();
            RrServer server = new RrServer(kernel, directory);
            Assert.True(server.Initialise("*ONESEAT", 1).IsOk);

            RrClient first = new RrClient(kernel, directory);
            RrClient second = new RrClient(kernel, directory);
            first.Initialise();
            second.Initialise();

            int connection;
            Assert.True(first.RequestConnection(null, "*ONESEAT", Array.Empty<byte>(), out connection).IsOk);

            // The single seat is taken, so the next letter is refused rather than queued.
            RrStatus blocked = second.RequestConnection(null, "*ONESEAT", Array.Empty<byte>(), out connection);
            Assert.False(blocked.IsOk);
            Assert.Equal((int)XroutError.XRNSP, blocked.Value);

            // Accept and then end the first session; the seat comes back.
            RrEvent actual;
            server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual);
            int serverConnection;
            byte[] data;
            server.AcceptConnectionIndication(out serverConnection, out data);
            server.RequestDisconnect(serverConnection);

            Assert.True(second.RequestConnection(null, "*ONESEAT", Array.Empty<byte>(), out connection).IsOk);
        }

        /// <summary>
        /// A name can only be registered once, which is what XSNAM guarantees.
        /// </summary>
        [Fact]
        public void DuplicateName_IsRefused()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XroutDirectory directory = new XroutDirectory();

            RrServer first = new RrServer(kernel, directory);
            RrServer second = new RrServer(kernel, directory);

            Assert.True(first.Initialise("*DEMO").IsOk);
            RrStatus duplicate = second.Initialise("*DEMO");

            Assert.False(duplicate.IsOk);
            Assert.Equal((int)XroutError.XRDDF, duplicate.Value);
        }

        /// <summary>
        /// Ending a server clears its name, so the name can be taken again afterwards.
        /// </summary>
        [Fact]
        public void End_ClearsTheName()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XroutDirectory directory = new XroutDirectory();

            RrServer server = new RrServer(kernel, directory);
            Assert.True(server.Initialise("*DEMO").IsOk);
            Assert.True(server.End().IsOk);

            RrServer replacement = new RrServer(kernel, directory);
            Assert.True(replacement.Initialise("*DEMO").IsOk);
        }

        /// <summary>
        /// Waiting when nothing is pending reports a timeout rather than an error.
        /// </summary>
        [Fact]
        public void Wait_WithNothingPending_ReportsTimeout()
        {
            Harness harness = new Harness();

            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.Timeout, actual);
        }

        private sealed class Harness
        {
            internal Harness()
            {
                Directory = new XroutDirectory();
                // ONE kernel: server and client are two tasks on the SAME system, which is the
                // case the manual says transfers the buffer rather than copying it.
                Kernel = new XmsgKernel(102, 0x1111, null);

                Server = new RrServer(Kernel, Directory);
                Client = new RrClient(Kernel, Directory);

                Server.Initialise("*DEMO");
                Client.Initialise();
            }

            internal XroutDirectory Directory { get; }

            internal XmsgKernel Kernel { get; }

            internal RrServer Server { get; }

            internal RrClient Client { get; }

            internal void ServerAcceptsPendingConnection(out int connection)
            {
                RrEvent actual;
                Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual);
                byte[] data;
                Server.AcceptConnectionIndication(out connection, out data);
                Server.SendConnectionResponse(connection, true, Array.Empty<byte>());
            }

            internal void ServerAnswers(byte[] response)
            {
                RrEvent actual;
                Server.Wait(RrEvent.RequestIndication, TimeSpan.Zero, out actual);
                int on;
                byte[] request;
                Server.GetRequest(out on, out request);
                Server.SendResponse(on, response);
            }
        }
    }
}
