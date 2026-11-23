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
    /// A provider that says, as data, how its files can be changed while they are copied to or from
    /// the host, and does the change.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule</b></para>
    /// A test over every provider that implements this interface checks, for each conversion and
    /// each choice of its options, that a plain text sample of capital letters, digits, common
    /// punctuation and line ends comes back unchanged through <see cref="FromHost"/> and then
    /// <see cref="ToHost"/>, and that the stored bytes come back unchanged the other way.
    /// <para>
    /// A conversion follows a real rule of the machine (a manual, a ROM, or measured on real disks).
    /// A byte the rule has no answer for is refused with an <see cref="ArgumentException"/> that
    /// names it, never replaced by a guess; the user can then copy the exact bytes.
    /// </para>
    /// </remarks>
    public interface ITransferConversionProvider
    {
        /// <summary>
        /// Gets the conversions this provider offers.
        /// </summary>
        IReadOnlyList<TransferConversion> Conversions { get; }

        /// <summary>
        /// Converts a file's stored bytes for the host (copy out).
        /// </summary>
        /// <param name="conversionId">
        /// The <see cref="TransferConversion.Id"/>.
        /// </param>
        /// <param name="stored">
        /// The bytes as read from the image.
        /// </param>
        /// <param name="options">
        /// Option values by <see cref="BlankImageOption.Id"/>, or <c>null</c>; a missing option takes
        /// its default.
        /// </param>
        /// <returns>
        /// The bytes for the host file.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the id is unknown, an option value is not allowed, or a byte has no meaning
        /// under the conversion.
        /// </exception>
        byte[] ToHost(string conversionId, ReadOnlySpan<byte> stored, IReadOnlyDictionary<string, string>? options);

        /// <summary>
        /// Converts a host file's bytes for the image (copy in).
        /// </summary>
        /// <param name="conversionId">
        /// The <see cref="TransferConversion.Id"/>.
        /// </param>
        /// <param name="host">
        /// The bytes of the host file.
        /// </param>
        /// <param name="options">
        /// Option values by <see cref="BlankImageOption.Id"/>, or <c>null</c>; a missing option takes
        /// its default.
        /// </param>
        /// <returns>
        /// The bytes to write to the image.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the id is unknown, an option value is not allowed, or a byte has no meaning
        /// under the conversion.
        /// </exception>
        byte[] FromHost(string conversionId, ReadOnlySpan<byte> host, IReadOnlyDictionary<string, string>? options);
    }
}
