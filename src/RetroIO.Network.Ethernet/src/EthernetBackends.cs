//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// Builds an <see cref="IEthernetBackend"/> from a short text spec (e.g.
    /// <c>"pcap"</c>, <c>"pcap:Ethernet"</c>). Returns <c>null</c> when the factory does
    /// not recognise the spec, so multiple factories can be tried in turn.
    /// </summary>
    /// <param name="spec">
    /// The backend spec (a <c>[MachineOption]</c> value, lower-cased by convention).
    /// </param>
    /// <returns>
    /// The backend, or <c>null</c> when this factory does not handle <paramref name="spec"/>.
    /// </returns>
    public delegate IEthernetBackend? EthernetBackendFactory(string spec);

    /// <summary>
    /// The host-registered registry that turns a machine's networking
    /// <c>[MachineOption]</c> (e.g. <c>net=pcap</c>) into a concrete
    /// <see cref="IEthernetBackend"/> — WITHOUT the machine referencing the native pcap
    /// implementation.
    /// </summary>
    /// <remarks>
    /// This is the bridge that lets Ethernet be configured through the generic config
    /// system in BOTH hosts. A machine's <c>ConfigureFromArgs</c> calls
    /// <see cref="Create"/> with the chosen spec and attaches the result to its
    /// <c>EthernetManager</c>; the machine package depends only on this abstraction. The
    /// host (which owns the pcap dependency) registers a factory ONCE at startup — e.g.
    /// <c>PcapEthernetBackend.RegisterFactory()</c> — so the same option string resolves
    /// to a real backend in the SDL2 CLI host and the Avalonia host alike. The special
    /// spec <c>"none"</c> / empty resolves to <c>null</c> (no backend = drop), handled by
    /// the machine without any factory.
    /// </remarks>
    public static class EthernetBackends
    {
        // ── Fields ──

        /// <summary>
        /// The registered factories, tried in registration order.
        /// </summary>
        private static readonly List<EthernetBackendFactory> Factories = new List<EthernetBackendFactory>();

        /// <summary>
        /// Guards <see cref="Factories"/> (registration can race host startup vs a machine build).
        /// </summary>
        private static readonly object Gate = new object();

        // ── Public methods ──

        /// <summary>
        /// Registers a backend factory (idempotent for the same delegate instance).
        /// </summary>
        /// <param name="factory">
        /// The factory to add.
        /// </param>
        public static void Register(EthernetBackendFactory factory)
        {
            if (factory == null)
            {
                return;
            }

            lock (Gate)
            {
                if (!Factories.Contains(factory))
                {
                    Factories.Add(factory);
                }
            }
        }

        /// <summary>
        /// Builds a backend for <paramref name="spec"/> by trying each registered factory in
        /// turn, or returns <c>null</c> when the spec is empty / <c>"none"</c> / unrecognised.
        /// </summary>
        /// <param name="spec">
        /// The backend spec (e.g. <c>"pcap"</c>, <c>"pcap:Ethernet"</c>).
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c>.
        /// </returns>
        public static IEthernetBackend? Create(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec)
                || string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            EthernetBackendFactory[] snapshot;
            lock (Gate)
            {
                snapshot = Factories.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                IEthernetBackend? backend = snapshot[i](spec!);
                if (backend != null)
                {
                    return backend;
                }
            }
            return null;
        }

        /// <summary>
        /// Removes all registered factories. Intended for test isolation.
        /// </summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Factories.Clear();
            }
        }
    }
}
