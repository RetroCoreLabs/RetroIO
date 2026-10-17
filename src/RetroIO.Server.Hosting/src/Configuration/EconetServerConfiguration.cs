//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Server.Hosting.Configuration;

public sealed class EconetServerConfiguration
{
    public string RootDirectory { get; set; } = string.Empty;
    public string DiscName { get; set; } = "RetroFS";
    public string? VersionString { get; set; }
    public int UdpPort { get; set; } = 32768;
    public byte Net { get; set; } = 1;
    public byte Station { get; set; } = 254;
    public byte AunStationOctetIndex { get; set; } = 3;
    public bool EnableUdp { get; set; } = true;

    /// <summary>
    /// Server-level default wire-format profile (task 0.4). <see cref="WireProfile.LaterFS"/> (the
    /// default) is the verified format modern ANFS/DNFS clients expect; only set
    /// <see cref="WireProfile.Classic1983"/> globally if EVERY client is a strict-1983 machine.
    /// </summary>
    public WireProfile WireProfile { get; set; } = WireProfile.LaterFS;

    /// <summary>
    /// Optional per-machine wire-profile overrides. Pin a single old client to
    /// <see cref="WireProfile.Classic1983"/> while everyone else stays on the default. An exact
    /// (Net, Station) match wins over <see cref="WireProfile"/>.
    /// </summary>
    public List<EconetMachineWireProfile>? WireProfileOverrides { get; set; }

    /// <summary>
    /// Converts the JSON-bound override list to the resolver's override array. Returns null when there
    /// are none, so the server takes the byte-identical all-LaterFS fast path. No LINQ.
    /// </summary>
    public WireProfileResolver.MachineOverride[]? ToWireProfileOverrides()
    {
        if (WireProfileOverrides == null || WireProfileOverrides.Count == 0)
            return null;

        WireProfileResolver.MachineOverride[] result =
            new WireProfileResolver.MachineOverride[WireProfileOverrides.Count];
        for (int i = 0; i < WireProfileOverrides.Count; i++)
        {
            EconetMachineWireProfile o = WireProfileOverrides[i];
            result[i] = new WireProfileResolver.MachineOverride(o.Net, o.Station, o.Profile);
        }
        return result;
    }

    /// <summary>
    /// Optional VFS-backed discs. When present, each Econet disc is served by its own VFS mount (so a
    /// server can mix local / cloud / TNFS discs). When absent (null/empty), the server keeps the
    /// back-compat single-root model (discs are subdirectories of <see cref="RootDirectory"/>).
    /// </summary>
    public List<EconetDiscConfiguration>? Discs { get; set; }

    public static EconetServerConfiguration GetDefault()
    {
        return new EconetServerConfiguration
        {
            RootDirectory = Path.Combine(PathHelper.GetRetroFSPath(), "econet-root"),
            DiscName = "RetroFS",
            UdpPort = 32768,
            Net = 1,
            Station = 254,
            EnableUdp = true,
        };
    }
}

/// <summary>
/// One VFS-backed Econet disc. Either defines a mount inline (<see cref="Provider"/> + <see cref="Uri"/>)
/// or references an existing RetroVFS mount by id (<see cref="MountRef"/> — reserved; not wired yet).
/// </summary>
public sealed class EconetDiscConfiguration
{
    /// <summary>
    /// Disc index (0-based). Used to synthesise the on-root directory name when unset.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// The Acorn disc name shown to the BBC (e.g. "RetroFS").
    /// </summary>
    public string AcornName { get; set; } = "RetroFS";

    /// <summary>
    /// The on-root directory name for this disc (e.g. "0RetroFS"). When null it is synthesised as
    /// <c>Index + AcornName</c>, matching the existing disc-directory convention.
    /// </summary>
    public string? DirName { get; set; }

    /// <summary>
    /// Inline mount provider ("native" / "tnfs" / "ftp" / "sftp"). Null when using MountRef.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Inline mount URI (meaning depends on the provider). Null when using MountRef.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// Expose this disc read-only over Econet. Can only ADD restriction to a referenced mount (a read-only mount stays read-only).
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Reference to a mount in the shared RetroVFS registry (<c>mounts.json</c>) by its VolumeId. When set,
    /// the disc's provider/uri/read-only come from that registry entry instead of inline Provider/Uri
    /// (the two are mutually exclusive). Referenced mounts must be a supported provider (native/tnfs).
    /// </summary>
    public string? MountRef { get; set; }

    /// <summary>
    /// The effective on-root directory name.
    /// </summary>
    public string ResolveDirName() => string.IsNullOrEmpty(DirName) ? $"{Index}{AcornName}" : DirName!;
}

/// <summary>
/// A JSON-bindable per-machine wire-profile override (config surface for
/// <see cref="WireProfileResolver.MachineOverride"/>, which is a readonly struct not suited to binding).
/// </summary>
public sealed class EconetMachineWireProfile
{
    /// <summary>
    /// The client's Econet network number.
    /// </summary>
    public byte Net { get; set; }

    /// <summary>
    /// The client's Econet station number.
    /// </summary>
    public byte Station { get; set; }

    /// <summary>
    /// The wire profile to serve this exact machine.
    /// </summary>
    public WireProfile Profile { get; set; } = WireProfile.LaterFS;
}
