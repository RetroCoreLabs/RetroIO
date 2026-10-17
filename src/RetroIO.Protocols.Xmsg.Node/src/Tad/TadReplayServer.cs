//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// A deliberately honest TAD server: it produces the VERIFIED secure delivery ACK for any
    /// data frame, and replays a recorded server response ONLY for an exact input it was
    /// seeded with from the capture. For any input it never saw it refuses to fabricate.
    /// </summary>
    /// <remarks>
    /// <para><b>Why replay, not synthesis</b></para>
    /// The whole TAD server behaviour is reconstructed from a SINGLE captured connect-to. There
    /// is not enough evidence to build a general, correct server: response rules vary by
    /// terminal type (only <c>0x0000</c> was captured), the echo/mode negotiation variants are
    /// unknown, and port/magic allocation is only partially observed (see TAD-MISSING.md). So
    /// this class does the only faithful thing possible - it replays exactly what the captured
    /// server sent for exactly the inputs the captured client sent, and returns an
    /// INFERRED/unhandled note otherwise. It does NOT pretend to be a correct general server.
    /// <para><b>What IS trustworthy here</b></para>
    /// The secure delivery ACK is VERIFIED (XMSG-PROTOCOL.md 6): opposite direction, Flags 1
    /// echoes the datagram sequence, Flags 2 = <c>0x0001</c>, one trailing per-direction
    /// counter byte. That path reuses <see cref="SecureDatagramReceiver"/>.
    /// </remarks>
    public sealed class TadReplayServer
    {
        private readonly SecureDatagramReceiver _receiver;
        private readonly List<RecordedExchange> _recorded;

        /// <summary>
        /// Initialises a replay server.
        /// </summary>
        /// <param name="ackCounter">
        /// The starting per-direction counter placed in the first secure ACK; it decrements
        /// per ACK (XMSG-PROTOCOL.md 6).
        /// </param>
        public TadReplayServer(byte ackCounter)
        {
            _receiver = new SecureDatagramReceiver(ackCounter);
            _recorded = new List<RecordedExchange>();
        }

        /// <summary>
        /// Gets the current secure-ACK counter value (the byte placed in the next ACK).
        /// </summary>
        public byte AckCounter
        {
            get { return _receiver.Counter; }
        }

        /// <summary>
        /// Records the server response the capture showed for one exact client input.
        /// </summary>
        /// <param name="clientInfoBytes">
        /// The client frame's information-field bytes (the exact key that must match).
        /// </param>
        /// <param name="responseInfoBytes">
        /// The server response frame's information-field bytes to replay verbatim.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="clientInfoBytes"/> or <paramref name="responseInfoBytes"/>
        /// is null.
        /// </exception>
        public void Record(byte[] clientInfoBytes, byte[] responseInfoBytes)
        {
            if (clientInfoBytes == null)
            {
                throw new ArgumentNullException(nameof(clientInfoBytes));
            }

            if (responseInfoBytes == null)
            {
                throw new ArgumentNullException(nameof(responseInfoBytes));
            }

            _recorded.Add(new RecordedExchange(clientInfoBytes, responseInfoBytes));
        }

        /// <summary>
        /// Handles a received client frame: builds its secure ACK and, if the exact input was
        /// recorded, replays the recorded server response.
        /// </summary>
        /// <param name="clientFrame">
        /// The decoded client frame.
        /// </param>
        /// <returns>
        /// A <see cref="TadReplayResult"/> holding the ACK, any recorded response, and a
        /// provenance-tagged note.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="clientFrame"/> or its header is null.
        /// </exception>
        public TadReplayResult Handle(XmsgFrame clientFrame)
        {
            if (clientFrame == null)
            {
                throw new ArgumentNullException(nameof(clientFrame));
            }

            if (clientFrame.Header == null)
            {
                throw new ArgumentNullException(nameof(clientFrame), "Frame header is null.");
            }

            byte[] key = clientFrame.ToArray();
            XmsgFrame? recorded = FindRecorded(key);

            // Only data frames are acknowledged; short control frames are not.
            XmsgFrame? ack = null;
            if (clientFrame.Header.Subtype == SintranPacketSubtype.Data)
            {
                // VERIFIED secure ACK mechanism (XMSG-PROTOCOL.md 6).
                ack = _receiver.ReceiveDataFrame(clientFrame);
            }

            if (recorded != null)
            {
                return new TadReplayResult(ack, recorded, "OBSERVED replay: recorded server response for this exact input.");
            }

            return new TadReplayResult(
                ack,
                null,
                "INFERRED/unhandled: no recorded server response for this input; the server refuses to fabricate one.");
        }

        /// <summary>
        /// Finds a recorded response whose key matches the given client info bytes.
        /// </summary>
        /// <param name="key">
        /// The client info bytes to look up.
        /// </param>
        /// <returns>
        /// The parsed recorded response frame, or <c>null</c> when no exact match exists.
        /// </returns>
        private XmsgFrame? FindRecorded(byte[] key)
        {
            for (int i = 0; i < _recorded.Count; i++)
            {
                RecordedExchange exchange = _recorded[i];
                if (BytesEqual(exchange.ClientKey, key))
                {
                    return XmsgFrame.Parse(exchange.ResponseBytes);
                }
            }

            return null;
        }

        /// <summary>
        /// Compares two byte arrays for exact equality.
        /// </summary>
        /// <param name="a">
        /// The first array.
        /// </param>
        /// <param name="b">
        /// The second array.
        /// </param>
        /// <returns>
        /// <c>true</c> when both have the same length and content.
        /// </returns>
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// One recorded client-input to server-response mapping.
        /// </summary>
        private sealed class RecordedExchange
        {
            /// <summary>
            /// Initialises a recorded exchange.
            /// </summary>
            /// <param name="clientKey">
            /// The client info bytes that key this exchange.
            /// </param>
            /// <param name="responseBytes">
            /// The server response info bytes to replay.
            /// </param>
            public RecordedExchange(byte[] clientKey, byte[] responseBytes)
            {
                ClientKey = clientKey;
                ResponseBytes = responseBytes;
            }

            /// <summary>
            /// Gets the client info bytes that key this exchange.
            /// </summary>
            public byte[] ClientKey { get; }

            /// <summary>
            /// Gets the server response info bytes to replay.
            /// </summary>
            public byte[] ResponseBytes { get; }
        }
    }
}
