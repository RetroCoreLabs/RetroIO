//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// One way a driver can change a file's bytes while it is copied to or from the host, such as
    /// the text rules of the machine.
    /// </summary>
    /// <remarks>
    /// Copying the exact bytes is always possible and is never listed as a conversion.
    /// </remarks>
    public sealed class TransferConversion
    {
        /// <summary>
        /// Gets the stable id, passed to <see cref="ITransferConversionProvider.ToHost"/> and
        /// <see cref="ITransferConversionProvider.FromHost"/>.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Gets the name shown to the user.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets what the conversion does, in words for the user.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Gets the directions the conversion works in.
        /// </summary>
        public TransferDirections Directions { get; init; } = TransferDirections.Both;

        /// <summary>
        /// Gets whether a program should offer this conversion first for a file that looks like
        /// text. When <c>false</c>, the exact bytes are the first choice.
        /// </summary>
        public bool DefaultForText { get; init; }

        /// <summary>
        /// Gets the values the user can set, checked the same way as a blank image's options.
        /// </summary>
        public IReadOnlyList<BlankImageOption> Options { get; init; } = Array.Empty<BlankImageOption>();
    }

    /// <summary>
    /// The directions a <see cref="TransferConversion"/> works in.
    /// </summary>
    [Flags]
    public enum TransferDirections
    {
        /// <summary>
        /// Neither direction.
        /// </summary>
        None = 0,

        /// <summary>
        /// From the disk image to the host (copy out).
        /// </summary>
        ToHost = 1,

        /// <summary>
        /// From the host to the disk image (copy in).
        /// </summary>
        FromHost = 2,

        /// <summary>
        /// Both directions.
        /// </summary>
        Both = ToHost | FromHost,
    }
}
