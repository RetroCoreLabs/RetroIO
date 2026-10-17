//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Composes the Econet file server's backend from <c>econet-config.json</c> <c>Discs[]</c>: one VFS mount
/// per disc, wrapped in a <see cref="CompositeVfsFileSystemBackend"/> so a single server can mix discs
/// on different mounts. When no discs are configured this is a no-op and the Econet server keeps the
/// shared single-root backend (back-compat).
/// </summary>
public static class EconetVfsDiscs
{
    /// <summary>
    /// Registers a VFS-disc-composite <see cref="EconetFileSystem"/> when the config declares
    /// <c>Discs[]</c>; otherwise does nothing. MUST be called BEFORE <c>AddEconetFileServer</c> so its
    /// back-compat default does not clobber this override.
    /// </summary>
    /// <param name="services">The DI services.</param>
    /// <param name="config">The loaded Econet configuration.</param>
    /// <param name="tempCacheDir">Directory for the adapters' materialized temp files.</param>
    /// <param name="mountsConfigPath">
    /// Path to the shared unified <c>mounts.json</c> registry (null = the default RetroFS location).
    /// Only read when a disc uses <c>MountRef</c>; ignored otherwise.
    /// </param>
    public static IServiceCollection AddEconetVfsDiscs(
        this IServiceCollection services,
        EconetServerConfiguration config,
        string tempCacheDir,
        string? mountsConfigPath = null)
    {
        if (config.Discs == null || config.Discs.Count == 0)
            return services; // back-compat: no VFS discs, keep the shared single-root backend

        // Only touch the shared mount registry (mounts.json) when at least one disc references it by
        // MountRef — a pure inline (Provider/Uri) config never needs the file to exist.
        IReadOnlyList<UnifiedMountEntry>? registryMounts = null;
        if (AnyDiscUsesMountRef(config.Discs))
        {
            UnifiedMountConfig registry = UnifiedMountConfigStore.Load(mountsConfigPath);
            registryMounts = registry.Mounts;
        }

        (CompositeVfsFileSystemBackend composite, VfsCore vfs) = BuildComposite(config, tempCacheDir, registryMounts);

        services.AddSingleton(vfs); // DI owns the VfsCore so it disposes on shutdown
        services.AddSingleton(new EconetFileSystem(composite));
        return services;
    }

    /// <summary>Returns true when any disc defines a <c>MountRef</c> (so the registry must be loaded).</summary>
    private static bool AnyDiscUsesMountRef(List<EconetDiscConfiguration> discs)
    {
        for (int i = 0; i < discs.Count; i++)
        {
            if (!string.IsNullOrEmpty(discs[i].MountRef))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the composite backend and its shared <see cref="VfsCore"/> from the configured discs.
    /// Pure (no DI) so it can be unit-tested directly. Caller owns/dispose the returned <see cref="VfsCore"/>.
    /// </summary>
    /// <param name="config">The loaded Econet configuration (its <c>Discs[]</c> drive the composite).</param>
    /// <param name="tempCacheDir">Directory for the adapters' materialized temp files.</param>
    /// <param name="registryMounts">
    /// The shared RetroVFS mount registry (the entries from <c>mounts.json</c>), used to resolve a disc's
    /// <c>MountRef</c> into a concrete provider/uri/read-only definition. May be null when no disc uses a
    /// <c>MountRef</c>; a <c>MountRef</c> with a null or non-matching registry throws.
    /// </param>
    public static (CompositeVfsFileSystemBackend Composite, VfsCore Vfs) BuildComposite(
        EconetServerConfiguration config,
        string tempCacheDir,
        IReadOnlyList<UnifiedMountEntry>? registryMounts = null)
    {
        // One shared VfsCore holds every disc's mount. Register every provider we can serve; a provider
        // does no work until a disc actually mounts through it (TNFS connects eagerly at mount time).
        VfsCore vfs = new VfsCore(enablePersistence: false);
        vfs.RegisterProvider(new NativeVolumeProvider());
        vfs.RegisterProvider(new TnfsVolumeProvider());

        List<CompositeVfsFileSystemBackend.DiscMount> mounts = new List<CompositeVfsFileSystemBackend.DiscMount>();
        List<EconetDiscConfiguration> discs = config.Discs!;
        for (int i = 0; i < discs.Count; i++)
        {
            EconetDiscConfiguration disc = discs[i];

            // Resolve the disc's mount definition: either MountRef → the shared registry, or inline
            // Provider/Uri. This yields the (provider, uri, readOnly) triple that the rest of the loop
            // treats uniformly, so a referenced mount and an inline mount go through the same support gate.
            (string provider, string uri, bool readOnly) = ResolveDiscMount(disc, registryMounts);

            if (provider != "native" && provider != "tnfs")
                throw new NotSupportedException(
                    $"Econet disc provider '{provider}' is not wired yet (native and tnfs supported; ftp/sftp are a follow-up).");

            string dirName = disc.ResolveDirName();
            string volumeId = "econet-disc-" + disc.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Mounting a tnfs disc CONNECTS eagerly (TnfsVolumeProvider opens a TnfsClient here). A dead
            // or unreachable server therefore fails BuildComposite — and thus server startup — loudly,
            // rather than a disc silently vanishing. Graceful per-disc degradation is a documented
            // follow-up (needs a logger threaded into this pure builder).
            vfs.Mount(provider, uri, volumeId, readOnly);

            // Each disc's adapter is rooted at that disc's on-root path under the logical Econet root.
            // Pass ReadOnly through so the adapter rejects writes up front (matches the mount's own
            // read-only enforcement, but fails fast before materializing a temp).
            string hostRoot = config.RootDirectory.Replace('\\', '/').TrimEnd('/') + "/" + dirName;
            VfsFileSystemBackend backend = new VfsFileSystemBackend(
                vfs, provider, volumeId, hostRoot, tempCacheDir, readOnly: readOnly);
            mounts.Add(new CompositeVfsFileSystemBackend.DiscMount(dirName, backend));
        }

        CompositeVfsFileSystemBackend composite =
            new CompositeVfsFileSystemBackend(config.RootDirectory, mounts.ToArray());
        return (composite, vfs);
    }

    /// <summary>
    /// Resolves a disc's effective mount definition. A <c>MountRef</c> disc looks its provider/uri/read-only
    /// up in the shared registry (and may NOT also carry inline Provider/Uri); an inline disc uses its own
    /// Provider/Uri. A referenced read-only mount stays read-only even if the disc doesn't ask for it — the
    /// disc can only ADD restriction, never remove it.
    /// </summary>
    private static (string Provider, string Uri, bool ReadOnly) ResolveDiscMount(
        EconetDiscConfiguration disc,
        IReadOnlyList<UnifiedMountEntry>? registryMounts)
    {
        if (!string.IsNullOrEmpty(disc.MountRef))
        {
            // MountRef and inline Provider/Uri are mutually exclusive — mixing them is ambiguous config.
            if (!string.IsNullOrEmpty(disc.Provider) || !string.IsNullOrEmpty(disc.Uri))
                throw new InvalidOperationException(
                    $"Econet disc '{disc.AcornName}' sets both MountRef ('{disc.MountRef}') and inline Provider/Uri — use one or the other.");

            if (registryMounts == null)
                throw new InvalidOperationException(
                    $"Econet disc '{disc.AcornName}' uses MountRef '{disc.MountRef}' but no mount registry (mounts.json) was provided.");

            UnifiedMountEntry? entry = FindMountByVolumeId(registryMounts, disc.MountRef!);
            if (entry == null)
                throw new InvalidOperationException(
                    $"Econet disc '{disc.AcornName}' references mount '{disc.MountRef}', which is not in the mount registry (mounts.json). {DescribeKnownMounts(registryMounts)}");

            if (string.IsNullOrEmpty(entry.ProviderType) || string.IsNullOrEmpty(entry.SourceUri))
                throw new InvalidOperationException(
                    $"Econet disc '{disc.AcornName}' references mount '{disc.MountRef}', but that registry entry has no ProviderType/SourceUri.");

            // Credentials on a referenced mount are not propagated through the disc-mount path yet
            // (VfsCore.Mount takes no credential args). Fail loud rather than silently authenticate as
            // anonymous — embed creds in the URI or use an inline disc until credential plumbing lands.
            if (!string.IsNullOrEmpty(entry.Username) || !string.IsNullOrEmpty(entry.Password))
                throw new NotSupportedException(
                    $"Econet disc '{disc.AcornName}' references mount '{disc.MountRef}', which carries credentials — credential propagation for referenced mounts is a follow-up. Embed them in the URI or use inline Provider/Uri.");

            // A read-only registry mount can only be exposed read-only; the disc's own flag can add (never
            // remove) restriction.
            bool readOnly = entry.IsReadOnly || disc.ReadOnly;
            return (entry.ProviderType.ToLowerInvariant(), entry.SourceUri, readOnly);
        }

        if (string.IsNullOrEmpty(disc.Provider) || string.IsNullOrEmpty(disc.Uri))
            throw new InvalidOperationException(
                $"Econet disc '{disc.AcornName}' needs both Provider and Uri (inline mount), or a MountRef.");

        return (disc.Provider!.ToLowerInvariant(), disc.Uri!, disc.ReadOnly);
    }

    /// <summary>Finds a registry entry by volume id (case-insensitive, matching the registry's own keying). No LINQ.</summary>
    private static UnifiedMountEntry? FindMountByVolumeId(IReadOnlyList<UnifiedMountEntry> mounts, string volumeId)
    {
        for (int i = 0; i < mounts.Count; i++)
        {
            if (string.Equals(mounts[i].VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))
                return mounts[i];
        }
        return null;
    }

    /// <summary>Builds a "known mounts: a, b, c" hint for the not-found error (or notes the registry is empty). No LINQ.</summary>
    private static string DescribeKnownMounts(IReadOnlyList<UnifiedMountEntry> mounts)
    {
        if (mounts.Count == 0)
            return "The mount registry is empty.";

        System.Text.StringBuilder sb = new System.Text.StringBuilder("Known mounts: ");
        for (int i = 0; i < mounts.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(mounts[i].VolumeId);
        }
        sb.Append('.');
        return sb.ToString();
    }
}
