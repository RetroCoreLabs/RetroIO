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

        /// <summary>
        /// Gives this option's value from the values the user set, or its default.
        /// </summary>
        /// <param name="options">
        /// Option values by id, or <c>null</c> for all defaults.
        /// </param>
        /// <returns>
        /// The value, checked against the kind, length and choices of the option.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the value set in <paramref name="options"/> is not allowed.
        /// </exception>
        public string ValueIn(IReadOnlyDictionary<string, string>? options)
        {
            if (options == null || !options.TryGetValue(Id, out string? value) || value == null)
            {
                return Default;
            }

            if (MaxLength > 0 && value.Length > MaxLength)
            {
                throw new ArgumentException(Label + ": at most " + MaxLength + " characters", nameof(options));
            }

            if (Kind == BlankImageOptionKind.Number && !long.TryParse(value, out _))
            {
                throw new ArgumentException(Label + ": not a whole number: " + value, nameof(options));
            }

            if (Kind == BlankImageOptionKind.Flag && value != "true" && value != "false")
            {
                throw new ArgumentException(Label + ": must be true or false", nameof(options));
            }

            if (Kind == BlankImageOptionKind.Choice)
            {
                bool found = false;
                for (int i = 0; i < Choices.Count; i++)
                {
                    found |= Choices[i] == value;
                }

                if (!found)
                {
                    throw new ArgumentException(Label + ": not one of the choices: " + value, nameof(options));
                }
            }

            return value;
        }
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
