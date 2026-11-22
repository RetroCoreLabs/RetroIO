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
        /// Gets the shortest text allowed, or 0 for none.
        /// </summary>
        public int MinLength { get; init; }

        /// <summary>
        /// Gets whether text may only hold printable ASCII characters (20h to 7Eh).
        /// </summary>
        public bool PrintableAsciiOnly { get; init; }

        /// <summary>
        /// Gets characters text must not hold, for example the end mark of a name on disk.
        /// </summary>
        public string ForbiddenCharacters { get; init; } = string.Empty;

        /// <summary>
        /// Gets the smallest value of a <see cref="BlankImageOptionKind.Number"/> option.
        /// </summary>
        public long Minimum { get; init; } = long.MinValue;

        /// <summary>
        /// Gets the largest value of a <see cref="BlankImageOptionKind.Number"/> option.
        /// </summary>
        public long Maximum { get; init; } = long.MaxValue;

        /// <summary>
        /// Checks a value the user typed or picked.
        /// </summary>
        /// <param name="value">
        /// The value.
        /// </param>
        /// <returns>
        /// <c>null</c> when the value is allowed, otherwise the message to show.
        /// </returns>
        public string? Check(string value)
        {
            if (value == null)
            {
                return Label + ": no value";
            }

            switch (Kind)
            {
                case BlankImageOptionKind.Number:
                    if (!long.TryParse(value, out long number))
                    {
                        return Label + ": must be a whole number";
                    }

                    if (number < Minimum || number > Maximum)
                    {
                        return Label + ": must be from " + Minimum + " to " + Maximum;
                    }

                    return null;
                case BlankImageOptionKind.Flag:
                    return value == "true" || value == "false" ? null : Label + ": must be true or false";
                case BlankImageOptionKind.Choice:
                    for (int i = 0; i < Choices.Count; i++)
                    {
                        if (Choices[i] == value)
                        {
                            return null;
                        }
                    }

                    return Label + ": not one of the choices";
            }

            if (MinLength > 0 && MinLength == MaxLength && value.Length != MinLength)
            {
                return Label + ": must be exactly " + MinLength + " characters";
            }

            if (value.Length < MinLength)
            {
                return Label + ": at least " + MinLength + " characters";
            }

            if (MaxLength > 0 && value.Length > MaxLength)
            {
                return Label + ": at most " + MaxLength + " characters";
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (PrintableAsciiOnly && (value[i] < ' ' || value[i] > '~'))
                {
                    return Label + ": printable ASCII only";
                }

                if (ForbiddenCharacters.IndexOf(value[i]) >= 0)
                {
                    return Label + ": must not contain " + value[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Gives this option's value from the values the user set, or its default.
        /// </summary>
        /// <param name="options">
        /// Option values by id, or <c>null</c> for all defaults.
        /// </param>
        /// <returns>
        /// The value, checked with <see cref="Check"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the value set in <paramref name="options"/> is not allowed.
        /// </exception>
        public string ValueIn(IReadOnlyDictionary<string, string>? options)
        {
            string value = options != null && options.TryGetValue(Id, out string? set) && set != null ? set : Default;
            string? problem = Check(value);
            if (problem != null)
            {
                throw new ArgumentException(problem, nameof(options));
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
