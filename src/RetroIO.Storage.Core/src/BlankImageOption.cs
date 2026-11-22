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
    /// One value the user can set when creating a blank image, such as a disk name.
    /// </summary>
    public sealed class BlankImageOption
    {
        /// <summary>
        /// Gets the stable id of the option, the key in the options passed to
        /// <see cref="IBlankImageProvider.CreateBlank"/>.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Gets the label shown to the user.
        /// </summary>
        public required string Label { get; init; }

        /// <summary>
        /// Gets what kind of value the option takes.
        /// </summary>
        public required BlankImageOptionKind Kind { get; init; }

        /// <summary>
        /// Gets the default value, as text (<c>true</c> or <c>false</c> for a flag).
        /// </summary>
        public string Default { get; init; } = string.Empty;

        /// <summary>
        /// Gets the longest text allowed, or 0 for no limit.
        /// </summary>
        public int MaxLength { get; init; }

        /// <summary>
        /// Gets the allowed values of a <see cref="BlankImageOptionKind.Choice"/> option.
        /// </summary>
        public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The kind of value a <see cref="BlankImageOption"/> takes.
    /// </summary>
    public enum BlankImageOptionKind
    {
        /// <summary>
        /// Free text, up to <see cref="BlankImageOption.MaxLength"/> characters.
        /// </summary>
        Text,

        /// <summary>
        /// A whole number.
        /// </summary>
        Number,

        /// <summary>
        /// One of <see cref="BlankImageOption.Choices"/>.
        /// </summary>
        Choice,

        /// <summary>
        /// On or off.
        /// </summary>
        Flag,
    }
}
