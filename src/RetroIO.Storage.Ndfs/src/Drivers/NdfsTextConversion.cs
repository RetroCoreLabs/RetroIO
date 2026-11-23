//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Storage.Ndfs.Drivers
{
    /// <summary>
    /// The SINTRAN text conversion: seven-bit characters whose bit 7 is either clear or holds ND
    /// even parity, with CR LF line ends.
    /// </summary>
    /// <remarks>
    /// Copy out clears bit 7 of every byte. Copy in either leaves bit 7 clear or sets ND even
    /// parity (bit 7 set when the low seven bits hold an odd number of ones), the rule
    /// <see cref="SintranCpuIdentity.SetParity"/> uses for the symbol lists it rewrites. The text
    /// files on the SMD test disk in this repository end their lines with CR LF; the line-end option
    /// turns that into LF for the host and back.
    /// </remarks>
    public static class NdfsTextConversion
    {
        /// <summary>
        /// The conversion's id.
        /// </summary>
        public const string Id = "sintran-text";

        /// <summary>
        /// Gets the conversions NDFS offers.
        /// </summary>
        public static IReadOnlyList<TransferConversion> Conversions { get; } = new TransferConversion[]
        {
            new TransferConversion
            {
                Id = Id,
                Name = "SINTRAN text",
                Description = "Copy out clears bit 7, the ND parity bit. Copy in writes seven-bit text, with bit 7 clear or with ND even parity, and refuses a byte above 7Fh.",
                DefaultForText = true,
                Options = new BlankImageOption[]
                {
                    new BlankImageOption
                    {
                        Id = "parity",
                        Label = "Bit 7 on copy in",
                        Kind = BlankImageOptionKind.Choice,
                        Choices = new[] { "clear", "even parity" },
                        Default = "clear",
                    },
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
        /// Converts stored SINTRAN text for the host.
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
        /// The text with bit 7 cleared.
        /// </returns>
        public static byte[] ToHost(string conversionId, ReadOnlySpan<byte> stored, IReadOnlyDictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(Conversions, conversionId);
            conversion.Options[0].ValueIn(options);
            bool lf = conversion.Options[1].ValueIn(options) == "LF";
            byte[] text = SintranCpuIdentity.StripParity(stored);
            return lf ? TransferText.CrLfToLf(text) : text;
        }

        /// <summary>
        /// Converts host text for an NDFS disk.
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
        /// Thrown when a byte is above 7Fh, which seven-bit text cannot hold.
        /// </exception>
        public static byte[] FromHost(string conversionId, ReadOnlySpan<byte> host, IReadOnlyDictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(Conversions, conversionId);
            bool parity = conversion.Options[0].ValueIn(options) == "even parity";
            bool lf = conversion.Options[1].ValueIn(options) == "LF";
            for (int i = 0; i < host.Length; i++)
            {
                if (host[i] > 0x7F)
                {
                    throw new ArgumentException(TransferText.NoMeaning(conversion.Name, host[i], i), nameof(host));
                }
            }

            byte[] text = lf ? TransferText.LfToCrLf(host) : host.ToArray();
            if (parity)
            {
                SintranCpuIdentity.SetParity(text);
            }

            return text;
        }
    }
}
