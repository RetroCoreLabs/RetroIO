//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// An optional extra an <see cref="IEthernetBackend"/> implements when it passes only frames
    /// meant for the guest's cards and must know their addresses (the pcap backend).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A backend that learns the card addresses from the frames the guest sends cannot pass a
    /// frame addressed to a card before that card has sent anything; a host that remembers the
    /// card's address from an earlier run then gets no answers. The machine knows each card's
    /// address (its address PROM or its configured address) from the start and gives it here.
    /// Learning from sent frames stays as the fallback.
    /// </para>
    /// <para>
    /// When a guest gives a card a new address at run time, remove the old one and add the new
    /// one. Call these on the thread that calls <see cref="IEthernetBackend.Send"/>.
    /// </para>
    /// </remarks>
    public interface IEthernetGuestAddresses
    {
        /// <summary>
        /// Adds the address of one of the guest's cards.
        /// </summary>
        /// <param name="mac">
        /// The 6-byte address.
        /// </param>
        /// <returns>
        /// <c>true</c> when the address was new; <c>false</c> when it was already held or is not a
        /// station address (a group address or all zeros).
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="mac"/> is not 6 bytes.
        /// </exception>
        bool AddGuestAddress(ReadOnlySpan<byte> mac);

        /// <summary>
        /// Removes an address a card no longer uses.
        /// </summary>
        /// <param name="mac">
        /// The 6-byte address.
        /// </param>
        /// <returns>
        /// <c>true</c> when the address was held.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="mac"/> is not 6 bytes.
        /// </exception>
        bool RemoveGuestAddress(ReadOnlySpan<byte> mac);
    }
}
