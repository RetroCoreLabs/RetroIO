//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// The CP/M text conversion: CP/M keeps a file in whole 128-byte records, and a text file ends
    /// at its first Ctrl-Z (1Ah), with the rest of the last record filled with Ctrl-Z.
    /// </summary>
    /// <remarks>
    /// Copy out cuts the file at its first Ctrl-Z. Copy in leaves the padding to the driver, which
    /// fills the last record with Ctrl-Z when it writes. CP/M ends a line with CR LF; the line-end
    /// option turns that into LF for the host and back.
    /// </remarks>
    public static class CpmTextConversion
    {
        /// <summary>
        /// The conversion's id.
        /// </summary>
        public const string Id = "cpm-text";

        /// <summary>
        /// The Ctrl-Z byte that ends a CP/M text file.
        /// </summary>
        public const byte EndOfText = 0x1A;

        /// <summary>
        /// Gets the conversions CP/M offers.
        /// </summary>
        public static IReadOnlyList<TransferConversion> Conversions { get; } = new TransferConversion[]
        {
            new TransferConversion
            {
                Id = Id,
                Name = "CP/M text",
                Description = "Copy out stops at the first Ctrl-Z (1Ah), the end of a CP/M text file. Copy in refuses a Ctrl-Z inside the text; the driver fills the last 128-byte record with Ctrl-Z.",
                DefaultForText = true,
                Options = new BlankImageOption[]
                {
                    new BlankImageOption
                    {
                        Id = "hostLineEnds",
                        Label = "Line ends on the host",
                        Kind = BlankImageOptionKind.Choice,
                        Choices = new[] { "CR LF", "LF" },
                        Default = "CR LF",
                    },
                },
            },
        };

        /// <summary>
        /// Converts stored CP/M bytes for the host.
        /// </summary>
        /// <param name="conversionId">
        /// Must be <see cref="Id"/>.
        /// </param>
        /// <param name="stored">
        /// The file as read from the disk.
        /// </param>
        /// <param name="options">
        /// Option values, or <c>null</c>.
        /// </param>
        /// <returns>
        /// The text up to its first Ctrl-Z.
        /// </returns>
        public static byte[] ToHost(string conversionId, ReadOnlySpan<byte> stored, IReadOnlyDictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(Conversions, conversionId);
            bool lf = conversion.Options[0].ValueIn(options) == "LF";
            int end = stored.IndexOf(EndOfText);
            ReadOnlySpan<byte> text = end < 0 ? stored : stored.Slice(0, end);
            return lf ? TransferText.CrLfToLf(text) : text.ToArray();
        }

        /// <summary>
        /// Converts host text for a CP/M disk.
        /// </summary>
        /// <param name="conversionId">
        /// Must be <see cref="Id"/>.
        /// </param>
        /// <param name="host">
        /// The host file.
        /// </param>
        /// <param name="options">
        /// Option values, or <c>null</c>.
        /// </param>
        /// <returns>
        /// The bytes to write.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the text holds a Ctrl-Z, which would end it early on CP/M.
        /// </exception>
        public static byte[] FromHost(string conversionId, ReadOnlySpan<byte> host, IReadOnlyDictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(Conversions, conversionId);
            bool lf = conversion.Options[0].ValueIn(options) == "LF";
            int end = host.IndexOf(EndOfText);
            if (end >= 0)
            {
                throw new ArgumentException(TransferText.NoMeaning(conversion.Name, EndOfText, end), nameof(host));
            }

            return lf ? TransferText.LfToCrLf(host) : host.ToArray();
        }
    }
}
