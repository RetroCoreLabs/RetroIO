//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// ExtFS plugin interface for archive and container format support.
    /// Inspired by Midnight Commander's extfs.d but using .NET assemblies instead of shell scripts.
    /// </summary>
    /// <remarks>
    /// Plugins provide VFS access to archive formats (ZIP, TAR, 7z, RAR) and disk images (ISO, VHD).
    /// They implement the same IVolumeProvider interface as network and local providers, enabling
    /// uniform access regardless of storage type.
    ///
    /// Plugin Discovery:
    /// - Plugins are loaded from the plugins/ directory at runtime
    /// - Each plugin DLL exports one or more IExtFsPlugin implementations
    /// - Plugins register their supported file extensions and magic numbers
    ///
    /// Usage Example:
    /// <code>
    /// var plugin = new ZipFsPlugin();
    /// using var stream = File.OpenRead("archive.zip");
    /// await plugin.MountAsync(stream);
    ///
    /// var files = await plugin.ListDirectoryAsync(context, "/");
    /// var fileStream = await plugin.OpenReadAsync(context, "/readme.txt");
    /// </code>
    /// </remarks>
    public interface IExtFsPlugin : IVolumeProvider
    {
        /// <summary>
        /// Handler name used in nested URNs (e.g., "zip", "tar", "7z", "iso9660").
        /// Must be lowercase and contain only alphanumeric characters and hyphens.
        /// </summary>
        /// <example>
        /// ZIP plugin: "zip"
        /// TAR plugin: "tar"
        /// ISO 9660 plugin: "iso9660"
        /// </example>
        string HandlerName { get; }

        /// <summary>
        /// File extensions this plugin supports (e.g., [".zip", ".jar", ".apk"]).
        /// Used for automatic plugin selection based on file extension.
        /// Extensions should include the leading dot and be lowercase.
        /// </summary>
        /// <example>
        /// ZIP plugin: [".zip", ".jar", ".apk", ".xpi"]
        /// TAR plugin: [".tar", ".tar.gz", ".tgz", ".tar.bz2"]
        /// </example>
        string[] SupportedExtensions { get; }

        /// <summary>
        /// Checks if this plugin can handle the given file path based on extension.
        /// </summary>
        /// <param name="path">File path to check</param>
        /// <returns>True if plugin supports this file extension</returns>
        bool CanHandle(string path);

        /// <summary>
        /// Checks if this plugin can handle a stream based on magic number detection.
        /// The stream position will be reset to 0 after checking.
        /// </summary>
        /// <param name="stream">Stream to inspect (must be seekable)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if plugin recognizes the stream format</returns>
        /// <exception cref="ArgumentException">Stream is not seekable</exception>
        Task<bool> CanHandleAsync(Stream stream, CancellationToken cancellationToken = default);

        /// <summary>
        /// Mounts an archive or container from a stream.
        /// The plugin takes ownership of the stream and must dispose it when unmounted.
        /// </summary>
        /// <param name="containerStream">Stream containing the archive/container data</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <exception cref="ArgumentNullException">Stream is null</exception>
        /// <exception cref="InvalidDataException">Stream does not contain valid archive data</exception>
        /// <exception cref="NotSupportedException">Archive format version not supported</exception>
        Task MountAsync(Stream containerStream, CancellationToken cancellationToken = default);
    }
}
