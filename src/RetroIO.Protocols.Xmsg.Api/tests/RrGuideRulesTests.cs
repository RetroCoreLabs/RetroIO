//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Proves two rules of the RR-LIB chapter of the COSMOS Programmer Guide (ND-60.164.3) that the
    /// request-response endpoints used to skip: what RRPBWAIT does with its timeout and its
    /// requested events, and that RRPCCNRQ hands back its connection reference when it is called.
    /// </summary>
    public sealed class RrGuideRulesTests
    {
        // ---------------------------------------------------------------------------------
        // RRPBWAIT - RR-LIB/PLANC reference, routine RRPBWAIT.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// "RRTMlength is the length of the waiting time." With nothing to report, the wait lasts
        /// the whole time and then reports the timeout event.
        /// </summary>
        [Fact]
        public void Wait_WithNothingPending_LastsTheTimeout()
        {
            Harness harness = new Harness();

            Stopwatch clock = Stopwatch.StartNew();
            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.FromMilliseconds(200), out actual).IsOk);
            clock.Stop();

            Assert.Equal(RrEvent.Timeout, actual);
            Assert.True(clock.ElapsedMilliseconds >= 190, "waited only " + clock.ElapsedMilliseconds + " ms");
        }

        /// <summary>
        /// "A value of 0 for RRTMlength is equivalent to a poll": it returns at once.
        /// </summary>
        [Fact]
        public void Wait_WithZeroTimeout_ReturnsAtOnce()
        {
            Harness harness = new Harness();

            Stopwatch clock = Stopwatch.StartNew();
            RrEvent actual;
            harness.Client.Wait(RrEvent.ResponseIndication, TimeSpan.Zero, out actual);
            clock.Stop();

            Assert.Equal(RrEvent.Timeout, actual);
            Assert.True(clock.ElapsedMilliseconds < 150, "polled for " + clock.ElapsedMilliseconds + " ms");
        }

        /// <summary>
        /// A message that arrives while the wait is running ends the wait early, with its event.
        /// </summary>
        [Fact]
        public void Wait_ReturnsAsSoonAsTheEventArrives()
        {
            Harness harness = new Harness();

            Thread sender = new Thread(() =>
            {
                Thread.Sleep(100);
                int connection;
                harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection);
            });
            sender.Start();

            Stopwatch clock = Stopwatch.StartNew();
            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.FromSeconds(10), out actual).IsOk);
            clock.Stop();
            sender.Join();

            Assert.Equal(RrEvent.ConnectionIndication, actual);
            Assert.True(clock.ElapsedMilliseconds < 5000, "took " + clock.ElapsedMilliseconds + " ms");
        }

        /// <summary>
        /// "With the exception of RREVunkn and RREVothr, you will only receive events permitted by
        /// requestedEvent." An event that is not asked for is not reported, and it is kept for a
        /// later wait that does ask for it.
        /// </summary>
        [Fact]
        public void Wait_ReportsOnlyTheEventsAskedFor_AndKeepsTheRest()
        {
            Harness harness = new Harness();
            int connection;
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection);

            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.Timeout, actual);

            Assert.True(harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual).IsOk);
            Assert.Equal(RrEvent.ConnectionIndication, actual);
        }

        /// <summary>
        /// Events that were set aside keep the order they arrived in.
        /// </summary>
        [Fact]
        public void Wait_KeepsSetAsideEventsInArrivalOrder()
        {
            Harness harness = new Harness();
            RrClient second = new RrClient(harness.Kernel, harness.Directory);
            second.Initialise();
            int connection;
            harness.Client.RequestConnection(null, "*DEMO", Encoding.ASCII.GetBytes("FIRST"), out connection);
            second.RequestConnection(null, "*DEMO", Encoding.ASCII.GetBytes("SECOND"), out connection);

            RrEvent actual;
            harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.Zero, out actual);
            Assert.Equal(RrEvent.Timeout, actual);

            int serverConnection;
            byte[] data;
            harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual);
            harness.Server.AcceptConnectionIndication(out serverConnection, out data);
            Assert.Equal("FIRST", Encoding.ASCII.GetString(data));

            harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual);
            harness.Server.AcceptConnectionIndication(out serverConnection, out data);
            Assert.Equal("SECOND", Encoding.ASCII.GetString(data));
        }

        /// <summary>
        /// "RRanyEvent may be used to accept any event." The constant is -1, all bits set.
        /// </summary>
        [Fact]
        public void Wait_WithAnyEvent_AcceptsWhateverArrives()
        {
            Harness harness = new Harness();
            int connection;
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection);

            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.Any, TimeSpan.Zero, out actual).IsOk);

            Assert.Equal(-1, (int)RrEvent.Any);
            Assert.Equal(RrEvent.ConnectionIndication, actual);
        }

        /// <summary>
        /// RREVunkn is exempt from the requested set: a message on a port RR-LIB does not own is
        /// reported even when it was not asked for.
        /// </summary>
        [Fact]
        public void Wait_ReportsAnUnknownMessageEvenWhenNotAskedFor()
        {
            Harness harness = new Harness();
            XmsgPortNumber stranger;
            harness.Kernel.OpenPort(out stranger);

            // A message whose first byte is no RR message kind.
            XmsgMagicNumber serverMagic = harness.ServerMagic();
            XmsgMessageIdentifier message;
            harness.Kernel.ReserveBuffer(8, XmsgBufferOptions.None, out message);
            harness.Kernel.WriteHeader(message, new byte[] { 0xEE, 0, 0, 0, 0, 0 });
            Assert.True(harness.Kernel.Send(serverMagic, stranger, XmsgSendFlags.None).IsSuccess);

            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.Zero, out actual).IsOk);

            Assert.Equal(RrEvent.UnknownPort, actual);
        }

        /// <summary>
        /// The high-level select now waits for the server for as long as it is given.
        /// </summary>
        [Fact]
        public void Select_WaitsForTheServersAnswer()
        {
            Harness harness = new Harness();

            Thread server = new Thread(() =>
            {
                RrEvent actual;
                harness.Server.Wait(RrEvent.ConnectionIndication, TimeSpan.FromSeconds(10), out actual);
                int connection;
                byte[] data;
                harness.Server.AcceptConnectionIndication(out connection, out data);
                harness.Server.SendConnectionResponse(connection, true, Encoding.ASCII.GetBytes("HI"));
            });
            server.Start();

            byte[] serverData;
            int clientConnection;
            RrStatus selected = harness.Client.Select(
                null, "*DEMO", Array.Empty<byte>(), TimeSpan.FromSeconds(10), out serverData, out clientConnection);
            server.Join();

            Assert.True(selected.IsOk);
            Assert.Equal("HI", Encoding.ASCII.GetString(serverData));
            Assert.NotEqual(0, clientConnection);
        }

        /// <summary>
        /// "RRERnttm - not terminated (ie. timeout)" is the status of RRPCslct and RRPCcall when
        /// the time runs out.
        /// </summary>
        [Fact]
        public void Select_WithNoServerAnswer_TimesOutWithRRERnttm()
        {
            Harness harness = new Harness();

            byte[] serverData;
            int connection;
            RrStatus selected = harness.Client.Select(
                null, "*DEMO", Array.Empty<byte>(), TimeSpan.FromMilliseconds(100), out serverData, out connection);

            Assert.Equal((int)RrError.RRErnttm, selected.Value);
        }

        /// <summary>
        /// The same for a transaction: no response in time is RRERnttm.
        /// </summary>
        [Fact]
        public void Transact_WithNoResponse_TimesOutWithRRERnttm()
        {
            Harness harness = new Harness();
            int connection = harness.Connect();

            byte[] response;
            RrStatus transacted = harness.Client.Transact(
                connection, Encoding.ASCII.GetBytes("PING"), TimeSpan.FromMilliseconds(100), out response);

            Assert.Equal((int)RrError.RRErnttm, transacted.Value);
        }

        /// <summary>
        /// "RRERdscn - disconnected (by server or RR-Lib)" is the status of RRPCcall when the
        /// server hangs up instead of answering.
        /// </summary>
        [Fact]
        public void Transact_WhenTheServerDisconnects_ReportsRRERdscn()
        {
            Harness harness = new Harness();
            int connection = harness.Connect();

            Thread server = new Thread(() =>
            {
                RrEvent actual;
                harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.FromSeconds(10), out actual);
                int on;
                byte[] request;
                harness.Server.GetRequest(out on, out request);
                harness.Server.RequestDisconnect(on);
            });
            server.Start();

            byte[] response;
            RrStatus transacted = harness.Client.Transact(
                connection, Encoding.ASCII.GetBytes("PING"), TimeSpan.FromSeconds(10), out response);
            server.Join();

            Assert.Equal((int)RrError.RRErdscn, transacted.Value);
        }

        /// <summary>
        /// "RRERunev - unexpected event while waiting for reply (ie. RREVunkn or RREVothr)": a
        /// message RR-LIB does not own, arriving in the middle of RRPCcall, is that status.
        /// </summary>
        [Fact]
        public void Transact_WhenAStrangeMessageArrives_ReportsRRERunev()
        {
            Harness harness = new Harness();
            int connection = harness.Connect();

            XmsgPortNumber stranger;
            harness.Kernel.OpenPort(out stranger);
            XmsgMagicNumber clientMagic;
            harness.Kernel.ConvertPortToMagic(new XmsgPortNumber(2), out clientMagic);
            XmsgMessageIdentifier message;
            harness.Kernel.ReserveBuffer(8, XmsgBufferOptions.None, out message);
            harness.Kernel.WriteHeader(message, new byte[] { 0xEE, 0, 0, 0, 0, 0 });
            Assert.True(harness.Kernel.Send(clientMagic, stranger, XmsgSendFlags.None).IsSuccess);

            byte[] response;
            RrStatus transacted = harness.Client.Transact(
                connection, Encoding.ASCII.GetBytes("PING"), TimeSpan.FromMilliseconds(500), out response);

            Assert.Equal((int)RrError.RRErunev, transacted.Value);
        }

        /// <summary>
        /// An infinite timeout waits until something happens, however long that takes.
        /// </summary>
        [Fact]
        public void Wait_WithAnInfiniteTimeout_WaitsForTheEvent()
        {
            Harness harness = new Harness();

            Thread sender = new Thread(() =>
            {
                Thread.Sleep(300);
                int connection;
                harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection);
            });
            sender.Start();

            RrEvent actual;
            Assert.True(harness.Server.Wait(RrEvent.ConnectionIndication, Timeout.InfiniteTimeSpan, out actual).IsOk);
            sender.Join();

            Assert.Equal(RrEvent.ConnectionIndication, actual);
        }

        /// <summary>
        /// A transaction waits for a response that comes while it is waiting.
        /// </summary>
        [Fact]
        public void Transact_WaitsForTheResponse()
        {
            Harness harness = new Harness();
            int connection = harness.Connect();

            Thread server = new Thread(() =>
            {
                RrEvent actual;
                harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.FromSeconds(10), out actual);
                int on;
                byte[] request;
                harness.Server.GetRequest(out on, out request);
                harness.Server.SendResponse(on, Encoding.ASCII.GetBytes("PONG"));
            });
            server.Start();

            byte[] response;
            RrStatus transacted = harness.Client.Transact(
                connection, Encoding.ASCII.GetBytes("PING"), TimeSpan.FromSeconds(10), out response);
            server.Join();

            Assert.True(transacted.IsOk);
            Assert.Equal("PONG", Encoding.ASCII.GetString(response));
        }

        // ---------------------------------------------------------------------------------
        // RRPCCNRQ - RR-LIB/PLANC reference, routine RRPCcnrq, and chapter 4 section 4.3.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// RRPCcnrq's remoteID is "Server reference number to be used in subsequent calls", and it
        /// is returned by the call itself, before the server has done anything (section 4.3 step 1
        /// is the call; the server only comes in at step 2). RRanyRemote is 0, so a real
        /// reference is never 0.
        /// </summary>
        [Fact]
        public void RequestConnection_ReturnsAReferenceAtOnce()
        {
            Harness harness = new Harness();

            int connection;
            Assert.True(harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out connection).IsOk);

            Assert.NotEqual(0, connection);
        }

        /// <summary>
        /// Two requests outstanding at once get different references.
        /// </summary>
        [Fact]
        public void RequestConnection_GivesEachRequestItsOwnReference()
        {
            Harness harness = new Harness();

            int first;
            int second;
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out first);
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out second);

            Assert.NotEqual(first, second);
        }

        /// <summary>
        /// The reference from the request is the one the connection confirmation reports and the
        /// one every later call uses, even when the server numbers its side differently.
        /// </summary>
        [Fact]
        public void TheReferenceFromTheRequest_IsTheOneUsedAfterwards()
        {
            Harness harness = new Harness();

            // Another client takes the server's first connection number, so the server's number
            // for ours is 2 while ours is 1.
            RrClient other = new RrClient(harness.Kernel, harness.Directory);
            other.Initialise();
            int otherConnection;
            other.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out otherConnection);
            int serverFirst = harness.AcceptNext(true);
            Assert.Equal(1, serverFirst);

            int requested;
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out requested);
            int serverSecond = harness.AcceptNext(true);
            Assert.NotEqual(requested, serverSecond);

            RrEvent actual;
            harness.Client.Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual);
            int confirmed;
            byte[] data;
            Assert.True(harness.Client.ConfirmConnection(out confirmed, out data).IsOk);
            Assert.Equal(requested, confirmed);

            // A request on the client's reference reaches the server under the server's number...
            Assert.True(harness.Client.SendRequest(requested, Encoding.ASCII.GetBytes("PING")).IsOk);
            harness.Server.Wait(RrEvent.RequestIndication, TimeSpan.Zero, out actual);
            int onServer;
            byte[] request;
            Assert.True(harness.Server.GetRequest(out onServer, out request).IsOk);
            Assert.Equal(serverSecond, onServer);

            // ...and the response comes back under the client's reference.
            Assert.True(harness.Server.SendResponse(onServer, Encoding.ASCII.GetBytes("PONG")).IsOk);
            harness.Client.Wait(RrEvent.ResponseIndication, TimeSpan.Zero, out actual);
            int onClient;
            byte[] response;
            Assert.True(harness.Client.GetResponse(out onClient, out response).IsOk);
            Assert.Equal(requested, onClient);
        }

        /// <summary>
        /// A disconnect started by the client names the client's reference; the server sees its own
        /// number, and the confirmation comes back under the client's.
        /// </summary>
        [Fact]
        public void Disconnect_UsesEachSidesOwnReference()
        {
            Harness harness = new Harness();
            RrClient other = new RrClient(harness.Kernel, harness.Directory);
            other.Initialise();
            int otherConnection;
            other.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out otherConnection);
            harness.AcceptNext(true);

            int requested = harness.Connect();

            Assert.True(harness.Client.RequestDisconnect(requested).IsOk);

            RrEvent actual;
            harness.Server.Wait(RrEvent.DisconnectIndication, TimeSpan.Zero, out actual);
            Assert.Equal(RrEvent.DisconnectIndication, actual);
            int closedOnServer;
            Assert.True(harness.Server.AcceptDisconnectIndication(out closedOnServer).IsOk);
            Assert.Equal(2, closedOnServer);
            Assert.Equal(1, harness.Server.ConnectionCount);
        }

        /// <summary>
        /// A disconnect started by the server reaches the client under the client's reference.
        /// </summary>
        [Fact]
        public void ServerDisconnect_ReachesTheClientUnderItsOwnReference()
        {
            Harness harness = new Harness();
            RrClient other = new RrClient(harness.Kernel, harness.Directory);
            other.Initialise();
            int otherConnection;
            other.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out otherConnection);
            harness.AcceptNext(true);

            int requested = harness.Connect();

            Assert.True(harness.Server.RequestDisconnect(2).IsOk);

            RrEvent actual;
            harness.Client.Wait(RrEvent.DisconnectIndication, TimeSpan.Zero, out actual);
            Assert.Equal(RrEvent.DisconnectIndication, actual);
            int closed;
            Assert.True(harness.Client.AcceptDisconnectIndication(out closed).IsOk);
            Assert.Equal(requested, closed);
            Assert.Equal(0, harness.Client.ConnectionCount);
        }

        /// <summary>
        /// A request that cannot be delivered comes back as a disconnect indication, and the
        /// client hears it under its own reference, not the server's.
        /// </summary>
        [Fact]
        public void UndeliverableRequest_ComesBackUnderTheClientsOwnReference()
        {
            Harness harness = new Harness();
            RrClient other = new RrClient(harness.Kernel, harness.Directory);
            other.Initialise();
            int otherConnection;
            other.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out otherConnection);
            harness.AcceptNext(true);
            int requested = harness.Connect();

            // The server goes away; the next request has nowhere to go.
            Assert.True(harness.Server.End().IsOk);
            Assert.True(harness.Client.SendRequest(requested, Encoding.ASCII.GetBytes("PING")).IsOk);

            RrEvent actual;
            harness.Client.Wait(RrEvent.DisconnectIndication, TimeSpan.Zero, out actual);
            Assert.Equal(RrEvent.DisconnectIndication, actual);
            int closed;
            Assert.True(harness.Client.AcceptDisconnectIndication(out closed).IsOk);
            Assert.Equal(requested, closed);
        }

        /// <summary>
        /// A connection the server refuses leaves the client with no connection and no reference
        /// in use.
        /// </summary>
        [Fact]
        public void RefusedConnection_ReportsNoConnection()
        {
            Harness harness = new Harness();
            int requested;
            harness.Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out requested);
            harness.AcceptNext(false);

            RrEvent actual;
            harness.Client.Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual);
            int confirmed;
            byte[] data;
            RrStatus status = harness.Client.ConfirmConnection(out confirmed, out data);

            Assert.False(status.IsOk);
            Assert.Equal(0, confirmed);
            Assert.Equal(0, harness.Client.ConnectionCount);
        }

        private sealed class Harness
        {
            internal Harness()
            {
                Directory = new XroutDirectory();
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

            internal XmsgMagicNumber ServerMagic()
            {
                XmsgMagicNumber magic;
                Kernel.ConvertPortToMagic(new XmsgPortNumber(1), out magic);
                return magic;
            }

            // The server takes the next connection request and answers it. Returns the server's
            // number for the connection.
            internal int AcceptNext(bool accept)
            {
                RrEvent actual;
                Server.Wait(RrEvent.ConnectionIndication, TimeSpan.Zero, out actual);
                Assert.Equal(RrEvent.ConnectionIndication, actual);
                int connection;
                byte[] data;
                Server.AcceptConnectionIndication(out connection, out data);
                Server.SendConnectionResponse(connection, accept, Array.Empty<byte>());
                return connection;
            }

            // Runs the connection set-up for the main client and returns its reference.
            internal int Connect()
            {
                int requested;
                Client.RequestConnection(null, "*DEMO", Array.Empty<byte>(), out requested);
                AcceptNext(true);
                RrEvent actual;
                Client.Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual);
                int confirmed;
                byte[] data;
                Client.ConfirmConnection(out confirmed, out data);
                return confirmed;
            }
        }
    }
}
