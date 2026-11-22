//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Builds the store the XMSG node's file server (<c>*FA-SERVER</c>) serves, from the node settings: a
/// local folder, a TNFS server, or a <c>mounts.json</c> volume (plan, Phase 6).
/// </summary>
/// <remarks>
/// <para>
/// A plain local folder is served straight from disk (<see cref="FolderFileStore(string)"/>), so it keeps
/// the file sharing rules of an open <c>FileStream</c>; the storage layer has no share mode. Everything
/// else - TNFS, a read-only folder, a referenced mount - goes through one <see cref="VfsCore"/> mount, the
/// way the Econet discs do (<see cref="EconetVfsDiscs"/>).
/// </para>
/// <para>
/// A TNFS mount connects at once, so an unreachable server fails here, at start-up, with its reason.
/// </para>
/// </remarks>
public static class XmsgFileServerStore
{
    /// <summary>
    /// The volume id of the file server's mount inside its own <see cref="VfsCore"/>.
    /// </summary>
    public const string VolumeId = "xmsg-fa-server";

    /// <summary>
    /// The path the mount is seen at by the store; any fixed path works, the store is rooted there.
    /// </summary>
    private const string MountPath = "/fa-server";

    /// <summary>
    /// Builds the store, or returns null when the file server is switched off.
    /// </summary>
    /// <param name="config">
    /// The node settings.
    /// </param>
    /// <param name="tempCacheDir">
    /// The folder for the storage layer's temporary copies of open files.
    /// </param>
    /// <param name="registryMounts">
    /// The entries of <c>mounts.json</c>; needed only when <see cref="XmsgNodeServerConfiguration.FileServerMountRef"/>
    /// is set.
    /// </param>
    /// <param name="vfs">
    /// Receives the storage layer the store reads through, which the caller must dispose at shutdown;
    /// null for a plain local folder.
    /// </param>
    /// <returns>
    /// The store, or null when <see cref="XmsgNodeServerConfiguration.FileServerEnabled"/> is false.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the setting is incomplete or names a mount that does not exist.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown for a kind of storage other than <c>native</c> and <c>tnfs</c>.
    /// </exception>
    public static IFaFileStore? Build(
        XmsgNodeServerConfiguration config,
        string tempCacheDir,
        IReadOnlyList<UnifiedMountEntry>? registryMounts,
        out VfsCore? vfs)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        vfs = null;
        if (!config.FileServerEnabled)
        {
            return null;
        }

        (string provider, string uri, bool readOnly) = StorageMountResolver.Resolve(
            "The XMSG file server", config.FileServerProvider, config.FileServerUri, config.FileServerMountRef, false, registryMounts);

        if (provider != "native" && provider != "tnfs")
        {
            throw new NotSupportedException(
                $"The XMSG file server's storage '{provider}' is not wired yet (native and tnfs are).");
        }

        if (provider == "native" && !readOnly)
        {
            return new FolderFileStore(uri);
        }

        VfsCore core = new VfsCore(enablePersistence: false);
        try
        {
            core.RegisterProvider(new NativeVolumeProvider());
            core.RegisterProvider(new TnfsVolumeProvider());
            core.Mount(provider, uri, VolumeId, readOnly);
            VfsFileSystemBackend backend = new VfsFileSystemBackend(
                core, provider, VolumeId, MountPath, tempCacheDir, readOnly: readOnly);
            FolderFileStore store = new FolderFileStore(backend, MountPath, provider + ":" + uri);
            vfs = core;
            return store;
        }
        catch
        {
            core.Dispose();
            throw;
        }
    }
}
