//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// What an XOT responder answers to a PVC setup packet (RFC 1613 6.5), decided from the packet and this port's own
    /// PVCs. No sockets: the port sends the reply this gives and closes the connection on a refusal.
    /// </summary>
    public sealed class XotPvcResponder
    {
        private readonly string _interfaceName;
        private readonly byte _window;
        private readonly int _packetSize;
        private readonly int _maxPacketSize;
        private readonly HashSet<ushort> _pvcs = new HashSet<ushort>();
        private readonly HashSet<ushort> _connected = new HashSet<ushort>();

        /// <param name="interfaceName">This port's interface name; empty accepts any name asked for.</param>
        /// <param name="window">The window on this port's PVCs.</param>
        /// <param name="packetSize">The packet size on this port's PVCs.</param>
        /// <param name="maxPacketSize">The largest packet this port can carry.</param>
        public XotPvcResponder(string interfaceName, byte window, int packetSize, int maxPacketSize)
        {
            _interfaceName = interfaceName;
            _window = window;
            _packetSize = packetSize;
            _maxPacketSize = maxPacketSize;
        }

        /// <summary>
        /// Tells whether the destination of a PVC is up; null means always. For a switch the destination interface is
        /// the port the PVC's other end is on, so the switch supplies this.
        /// </summary>
        public Func<ushort, bool>? InterfaceUp { get; set; }

        /// <summary>Adds one of this port's PVC channels.</summary>
        public void AddPvc(ushort lci)
        {
            _pvcs.Add(lci);
        }

        /// <summary>Frees a channel when its XOT connection closes ("broken by simply closing the TCP connection").</summary>
        public void Disconnected(ushort lci)
        {
            _connected.Remove(lci);
        }

        /// <summary>
        /// Decides the answer. A packet that cannot be read gets 0x1A; otherwise the checks run in the order a far end can
        /// act on: interface (0x13), PVC (0x16), destination down (0x14), already joined (0x17), flow control (0x19, 0x18).
        /// </summary>
        /// <param name="packet">The packet as it arrived, without the XOT header.</param>
        /// <param name="reply">The setup packet to send back: the received one with the status changed, as the RFC
        /// designed it ("the responder could simply modify a few fields of the received packet").</param>
        /// <returns>The status in the reply; 0x00 means the PVC is joined and now counts as connected.</returns>
        public byte Answer(ReadOnlySpan<byte> packet, out XotPvcSetup reply)
        {
            if (!XotPvcSetup.TryParse(packet, out reply) || reply.Status != XotPvcSetup.StatusOk)
            {
                // "After establishing the TCP connection the initiator sends a PVC setup packet, the status value MUST be 0x00".
                reply.Status = XotPvcSetup.StatusProtocolError;
                return reply.Status;
            }

            byte status;
            if (_interfaceName.Length > 0 && !string.Equals(_interfaceName, reply.ResponderInterface, StringComparison.OrdinalIgnoreCase))
            {
                status = XotPvcSetup.StatusNoSuchInterface;
            }
            else if (!_pvcs.Contains(reply.ResponderLci))
            {
                status = XotPvcSetup.StatusNoSuchPvc;
            }
            else if (InterfaceUp != null && !InterfaceUp(reply.ResponderLci))
            {
                // 0x14 "Destination interface is not up".
                status = XotPvcSetup.StatusInterfaceNotUp;
            }
            else if (_connected.Contains(reply.ResponderLci))
            {
                // 0x17 "may returned in the case that the targeted PVC already has an XOT PVC connection active".
                status = XotPvcSetup.StatusConfigurationMismatch;
            }
            else
            {
                status = reply.CheckFlowControl(_window, _packetSize, 7, _maxPacketSize);
            }

            if (status == XotPvcSetup.StatusOk)
            {
                _connected.Add(reply.ResponderLci);
            }

            reply.Status = status;
            return status;
        }
    }
}
