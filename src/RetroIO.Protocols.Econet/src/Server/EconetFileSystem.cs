//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// The <see cref="IFileSystemBackend"/> the Econet file server uses — which may differ from the globally
/// shared backend (TNFS uses the shared one too, so it must not be swapped globally).
/// </summary>
/// <remarks>
/// By default this wraps the shared backend (back-compat: a single local/Azure root with discs as
/// subdirectories). When <c>econet-config.json</c> declares a <c>Discs[]</c> array, the composition root
/// registers an override wrapping a VFS-disc composite instead, so each Econet disc can live on its own
/// mount (local / cloud / TNFS). Only this Econet wrapper is redirected; TNFS keeps the shared backend.
/// </remarks>
public sealed class EconetFileSystem
{
    /// <summary>
    /// The backend the Econet file server should read/write through.
    /// </summary>
    public IFileSystemBackend Backend { get; }

    /// <summary>
    /// Creates the wrapper.
    /// </summary>
    /// <param name="backend">
    /// The file system backend the Econet file server uses.
    /// </param>
    public EconetFileSystem(IFileSystemBackend backend) => Backend = backend;
}
