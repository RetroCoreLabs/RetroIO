//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Shared builders for the LAPB state-machine tests: constructing links, driving frames in, and
    /// bringing a link up. Centralised so every LAPB test file uses one set of helpers (DRY).
    /// </summary>
    internal static class LapbTestKit
    {
        // Node numbers as 16-bit big-endian info bytes (spec 2.3.1).
        public const byte Node100Hi = 0x00;
        public const byte Node100Lo = 0x64;
        public const byte Node102Hi = 0x00;
        public const byte Node102Lo = 0x66;
        public const byte Node103Hi = 0x00;
        public const byte Node103Lo = 0x67;

        /// <summary>
        /// Builds a disconnected link that captures every transmitted frame body.
        /// </summary>
        /// <param name="ownNode">
        /// This node's number.
        /// </param>
        /// <param name="sent">
        /// The list receiving every transmitted LAPB body.
        /// </param>
        /// <param name="options">
        /// Optional timer/window configuration; defaults to the spec defaults.
        /// </param>
        /// <returns>
        /// A new, disconnected <see cref="LapbLayer"/>.
        /// </returns>
        public static LapbLayer NewLink(ushort ownNode, List<byte[]> sent, LapbOptions? options = null)
        {
            LapbLayer link = new LapbLayer(ownNode, options);
            link.OnTransmit += delegate (byte[] body) { sent.Add(body); };
            return link;
        }

        /// <summary>
        /// Builds a link already CONNECTED as the passive answerer to a peer (node 100) SABM.
        /// </summary>
        /// <param name="ownNode">
        /// This node's number.
        /// </param>
        /// <param name="sent">
        /// The list receiving every transmitted LAPB body.
        /// </param>
        /// <param name="got">
        /// An optional list receiving every delivered information field.
        /// </param>
        /// <param name="options">
        /// Optional timer/window configuration; defaults to the spec defaults.
        /// </param>
        /// <returns>
        /// A connected <see cref="LapbLayer"/> with V(S) = V(R) = V(A) = 0 and neighbour id 100.
        /// </returns>
        public static LapbLayer NewConnected(ushort ownNode, List<byte[]> sent, List<byte[]>? got, LapbOptions? options = null)
        {
            LapbLayer link = NewLink(ownNode, sent, options);
            if (got != null)
            {
                link.OnInformation += delegate (ReadOnlyMemory<byte> info) { got.Add(info.ToArray()); };
            }

            Deliver(link, 0x01, 0x3F, Node100Hi, Node100Lo);   // peer (node 100) SABM -> Connected
            return link;
        }

        /// <summary>
        /// Builds a link already CONNECTED as the initiator: it sends SABM and the peer answers UA.
        /// </summary>
        /// <param name="ownNode">
        /// This node's number.
        /// </param>
        /// <param name="peerHi">
        /// The high byte of the peer's node number, carried in the peer's UA.
        /// </param>
        /// <param name="peerLo">
        /// The low byte of the peer's node number.
        /// </param>
        /// <param name="sent">
        /// The list receiving every transmitted LAPB body.
        /// </param>
        /// <param name="got">
        /// An optional list receiving every delivered information field.
        /// </param>
        /// <param name="options">
        /// Optional timer/window configuration; defaults to the spec defaults.
        /// </param>
        /// <returns>
        /// A connected <see cref="LapbLayer"/> with V(S) = V(R) = V(A) = 0.
        /// </returns>
        public static LapbLayer NewConnectedInitiator(ushort ownNode, byte peerHi, byte peerLo, List<byte[]> sent, List<byte[]>? got, LapbOptions? options = null)
        {
            LapbLayer link = NewLink(ownNode, sent, options);
            if (got != null)
            {
                link.OnInformation += delegate (ReadOnlyMemory<byte> info) { got.Add(info.ToArray()); };
            }

            link.Connect(currentTicks: 0);               // our SABM
            Deliver(link, 0x01, 0x73, peerHi, peerLo);   // peer UA -> Connected
            return link;
        }

        /// <summary>
        /// Delivers a frame built from an address, control byte and info bytes at tick 0.
        /// </summary>
        /// <param name="link">
        /// The link under test.
        /// </param>
        /// <param name="address">
        /// The LAPB address byte.
        /// </param>
        /// <param name="control">
        /// The LAPB control byte.
        /// </param>
        /// <param name="info">
        /// The information field bytes.
        /// </param>
        public static void Deliver(LapbLayer link, byte address, byte control, params byte[] info)
        {
            DeliverAt(link, currentTicks: 0, address, control, info);
        }

        /// <summary>
        /// Delivers a frame built from an address, control byte and info bytes at a given clock value.
        /// </summary>
        /// <param name="link">
        /// The link under test.
        /// </param>
        /// <param name="currentTicks">
        /// The injected clock value at which the frame arrives.
        /// </param>
        /// <param name="address">
        /// The LAPB address byte.
        /// </param>
        /// <param name="control">
        /// The LAPB control byte.
        /// </param>
        /// <param name="info">
        /// The information field bytes.
        /// </param>
        public static void DeliverAt(LapbLayer link, long currentTicks, byte address, byte control, params byte[] info)
        {
            byte[] frameBytes = new byte[2 + info.Length + 2];
            frameBytes[0] = address;
            frameBytes[1] = control;
            Array.Copy(info, 0, frameBytes, 2, info.Length);
            link.OnFrameReceived(LapbFrame.FromCapture(frameBytes), currentTicks);
        }
    }
}
