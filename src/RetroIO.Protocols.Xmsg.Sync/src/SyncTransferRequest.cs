//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// One transfer for an <see cref="ISyncTransferAgent"/> to carry out.
    /// </summary>
    /// <remarks>
    /// Built from a <see cref="SyncAction"/>, which is why it carries the action kind rather than
    /// a bare direction: whether the remote file is being CREATED or REPLACED changes the request
    /// on the wire, because a SINTRAN filespec is quoted when a file is being made and bare when
    /// it already exists.
    /// </remarks>
    public sealed class SyncTransferRequest
    {
        /// <summary>
        /// Creates a transfer request.
        /// </summary>
        /// <param name="kind">
        /// What is being done: <see cref="SyncActionKind.Create"/>,
        /// <see cref="SyncActionKind.Overwrite"/> or <see cref="SyncActionKind.Pull"/>.
        /// </param>
        /// <param name="localPath">
        /// The file on this side - the source of a push, the destination of a pull.
        /// </param>
        /// <param name="fileSpec">
        /// The name on the machine, WITHOUT quotes. Quoting belongs to whoever builds the request
        /// on the wire, because only it knows whether the file is being created.
        /// </param>
        /// <param name="machine">
        /// The machine name the mapping addresses.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="localPath"/>, <paramref name="fileSpec"/> or
        /// <paramref name="machine"/> is null.
        /// </exception>
        public SyncTransferRequest(SyncActionKind kind, string localPath, string fileSpec, string machine)
        {
            Kind = kind;
            LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
            FileSpec = fileSpec ?? throw new ArgumentNullException(nameof(fileSpec));
            Machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        /// <summary>
        /// Gets what is being done.
        /// </summary>
        public SyncActionKind Kind { get; }

        /// <summary>
        /// Gets the file on this side.
        /// </summary>
        public string LocalPath { get; }

        /// <summary>
        /// Gets the unquoted name on the machine.
        /// </summary>
        public string FileSpec { get; }

        /// <summary>
        /// Gets the machine name.
        /// </summary>
        public string Machine { get; }

        /// <summary>
        /// Gets whether this request moves a file TO the machine.
        /// </summary>
        public bool IsPush
        {
            get { return Kind == SyncActionKind.Create || Kind == SyncActionKind.Overwrite; }
        }
    }
}
