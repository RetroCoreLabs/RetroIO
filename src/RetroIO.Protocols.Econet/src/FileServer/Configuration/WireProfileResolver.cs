//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Resolves the effective <see cref="WireProfile"/> for a given client machine (net.station).
/// </summary>
/// <remarks>
/// Two tiers, per the design in <c>docs/econet/notes/econet-remaining-roadmap.md</c> (task 0.4):
/// a <b>server-level default</b>, plus optional <b>per-machine overrides</b> keyed by (net, station).
/// Resolution: an exact per-machine override wins; otherwise the server default; the built-in
/// <see cref="AllLaterFs"/> singleton keeps every existing caller on the verified LaterFS bytes.
/// </remarks>
public sealed class WireProfileResolver
{
    /// <summary>
    /// A single per-machine override: pin one client station to a specific wire profile.
    /// </summary>
    public readonly struct MachineOverride
    {
        public MachineOverride(byte net, byte station, WireProfile profile)
        {
            Net = net;
            Station = station;
            Profile = profile;
        }

        /// <summary>The client's Econet network number.</summary>
        public readonly byte Net;

        /// <summary>The client's Econet station number.</summary>
        public readonly byte Station;

        /// <summary>The wire profile to use for this exact machine.</summary>
        public readonly WireProfile Profile;
    }

    private readonly WireProfile _default;
    private readonly MachineOverride[] _overrides;

    /// <summary>
    /// Creates a resolver with a server-level default and an optional per-machine override table.
    /// </summary>
    /// <param name="defaultProfile">The profile applied when no per-machine override matches.</param>
    /// <param name="overrides">Per-machine overrides; null/empty means "default for everyone".</param>
    public WireProfileResolver(WireProfile defaultProfile, MachineOverride[]? overrides)
    {
        _default = defaultProfile;
        _overrides = overrides ?? Array.Empty<MachineOverride>();
    }

    /// <summary>
    /// The shared resolver that returns <see cref="WireProfile.LaterFS"/> for every machine. Used as the
    /// back-compat default so an unconfigured server keeps emitting the verified later-FS bytes.
    /// </summary>
    public static WireProfileResolver AllLaterFs { get; } =
        new WireProfileResolver(WireProfile.LaterFS, null);

    /// <summary>The server-level default profile (used when no per-machine override matches).</summary>
    public WireProfile DefaultProfile => _default;

    /// <summary>Number of configured per-machine overrides.</summary>
    public int OverrideCount => _overrides.Length;

    /// <summary>
    /// Resolves the effective profile for a client. An exact (net, station) override wins; otherwise
    /// the server default. No LINQ — a plain indexed scan over the (small) override table.
    /// </summary>
    public WireProfile Resolve(byte net, byte station)
    {
        for (int i = 0; i < _overrides.Length; i++)
        {
            if (_overrides[i].Net == net && _overrides[i].Station == station)
                return _overrides[i].Profile;
        }

        return _default;
    }
}
