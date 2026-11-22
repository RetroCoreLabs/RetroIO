//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Storage.Core;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Turns a storage setting - an inline <c>Provider</c> and <c>Uri</c>, or a <c>MountRef</c> into the
/// shared <c>mounts.json</c> - into the provider, URI and read-only flag to mount.
/// </summary>
/// <remarks>
/// One copy for every protocol that serves files from configurable storage: the Econet discs
/// (<see cref="EconetVfsDiscs"/>) and the XMSG file server (<see cref="XmsgFileServerStore"/>). It was
/// written for the Econet discs first; the rules and the messages are theirs.
/// </remarks>
public static class StorageMountResolver
{
    /// <summary>
    /// Resolves one storage setting.
    /// </summary>
    /// <param name="what">
    /// What the setting belongs to, as the error messages start, for example <c>Econet disc 'X'</c>.
    /// </param>
    /// <param name="provider">
    /// The inline provider (<c>native</c>, <c>tnfs</c>), or null.
    /// </param>
    /// <param name="uri">
    /// The inline URI or path, or null.
    /// </param>
    /// <param name="mountRef">
    /// The volume id of an entry in <c>mounts.json</c>, or null.
    /// </param>
    /// <param name="readOnly">
    /// Whether the setting asks for read-only. It can only add restriction to a referenced mount.
    /// </param>
    /// <param name="registryMounts">
    /// The entries of <c>mounts.json</c>; needed only for a <c>MountRef</c>.
    /// </param>
    /// <returns>
    /// The provider (lower case), the URI and the read-only flag.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the setting is incomplete, mixes the two forms, or names a mount that does not exist.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when a referenced mount carries credentials, which cannot be passed on yet.
    /// </exception>
    public static (string Provider, string Uri, bool ReadOnly) Resolve(
        string what,
        string? provider,
        string? uri,
        string? mountRef,
        bool readOnly,
        IReadOnlyList<UnifiedMountEntry>? registryMounts)
    {
        if (!string.IsNullOrEmpty(mountRef))
        {
            // MountRef and inline Provider/Uri are mutually exclusive — mixing them is ambiguous config.
            if (!string.IsNullOrEmpty(provider) || !string.IsNullOrEmpty(uri))
                throw new InvalidOperationException(
                    $"{what} sets both MountRef ('{mountRef}') and inline Provider/Uri — use one or the other.");

            if (registryMounts == null)
                throw new InvalidOperationException(
                    $"{what} uses MountRef '{mountRef}' but no mount registry (mounts.json) was provided.");

            UnifiedMountEntry? entry = FindMountByVolumeId(registryMounts, mountRef);
            if (entry == null)
                throw new InvalidOperationException(
                    $"{what} references mount '{mountRef}', which is not in the mount registry (mounts.json). {DescribeKnownMounts(registryMounts)}");

            if (string.IsNullOrEmpty(entry.ProviderType) || string.IsNullOrEmpty(entry.SourceUri))
                throw new InvalidOperationException(
                    $"{what} references mount '{mountRef}', but that registry entry has no ProviderType/SourceUri.");

            // Credentials on a referenced mount are not propagated through the mount path yet
            // (VfsCore.Mount takes no credential args). Fail loud rather than silently authenticate as
            // anonymous — embed creds in the URI or use an inline mount until credential plumbing lands.
            if (!string.IsNullOrEmpty(entry.Username) || !string.IsNullOrEmpty(entry.Password))
                throw new NotSupportedException(
                    $"{what} references mount '{mountRef}', which carries credentials — credential propagation for referenced mounts is a follow-up. Embed them in the URI or use inline Provider/Uri.");

            // A read-only registry mount can only be exposed read-only; the setting's own flag can add
            // (never remove) restriction.
            return (entry.ProviderType.ToLowerInvariant(), entry.SourceUri, entry.IsReadOnly || readOnly);
        }

        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(uri))
            throw new InvalidOperationException(
                $"{what} needs both Provider and Uri (inline mount), or a MountRef.");

        return (provider.ToLowerInvariant(), uri, readOnly);
    }

    /// <summary>
    /// Finds a registry entry by volume id (case-insensitive, matching the registry's own keying). No LINQ.
    /// </summary>
    private static UnifiedMountEntry? FindMountByVolumeId(IReadOnlyList<UnifiedMountEntry> mounts, string volumeId)
    {
        for (int i = 0; i < mounts.Count; i++)
        {
            if (string.Equals(mounts[i].VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))
                return mounts[i];
        }
        return null;
    }

    /// <summary>
    /// Builds a "known mounts: a, b, c" hint for the not-found error (or notes the registry is empty). No LINQ.
    /// </summary>
    private static string DescribeKnownMounts(IReadOnlyList<UnifiedMountEntry> mounts)
    {
        if (mounts.Count == 0)
            return "The mount registry is empty.";

        System.Text.StringBuilder known = new System.Text.StringBuilder("Known mounts: ");
        for (int i = 0; i < mounts.Count; i++)
        {
            if (i > 0)
                known.Append(", ");
            known.Append(mounts[i].VolumeId);
        }
        known.Append('.');
        return known.ToString();
    }
}
