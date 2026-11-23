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
    /// The PETSCII text conversion for Commodore SEQ files: PETSCII letters to ASCII and back, and
    /// the PETSCII line end (CR, 0Dh) to LF and back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Commodore machine shows text in one of two character sets:
    ///  - lower and upper case: 41h to 5Ah are a to z, and C1h to DAh (and their copies 61h to 7Ah)
    ///    are A to Z.
    ///  - upper case and graphics, the set at power-on: 41h to 5Ah are A to Z, and the higher codes
    ///    are graphics.
    /// </para>
    /// <para>
    /// Space, digits and the punctuation from 20h to 40h, and the brackets 5Bh and 5Dh, are the same
    /// in PETSCII and ASCII. Every other byte (colour and cursor codes, graphics, the pound sign, the
    /// arrows) has no ASCII meaning and is refused, as is a host character with no PETSCII code, so
    /// nothing is replaced by a guess. In the upper-case set a host lower-case letter is written as
    /// its capital, the way that set shows it.
    /// </para>
    /// </remarks>
    public static class PetsciiTextConversion
    {
        /// <summary>
        /// The conversion's id.
        /// </summary>
        public const string Id = "petscii-text";

        private const string LowerUpper = "lower and upper case";
        private const string UpperGraphics = "upper case and graphics";

        /// <summary>
        /// Gets the conversions Commodore disks offer.
        /// </summary>
        public static IReadOnlyList<TransferConversion> Conversions { get; } = new TransferConversion[]
        {
            new TransferConversion
            {
                Id = Id,
                Name = "PETSCII text",
                Description = "For SEQ text files: PETSCII letters to ASCII and back, and the PETSCII line end CR to the host line end. Colour codes, graphics and other bytes with no ASCII meaning are refused.",
                DefaultForText = true,
                Options = new BlankImageOption[]
                {
                    new BlankImageOption
                    {
                        Id = "charset",
                        Label = "Character set",
                        Kind = BlankImageOptionKind.Choice,
                        Choices = new[] { LowerUpper, UpperGraphics },
                        Default = LowerUpper,
                    },
                    new BlankImageOption
                    {
                        Id = "hostLineEnds",
                        Label = "Line ends on the host",
                        Kind = BlankImageOptionKind.Choice,
                        Choices = new[] { "LF", "CR LF" },
                        Default = "LF",
                    },
                },
            },
        };

        /// <summary>
        /// Converts a stored PETSCII text file for the host.
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
        /// ASCII text.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when a byte has no ASCII meaning in the chosen character set.
        /// </exception>
        public static byte[] ToHost(string conversionId, ReadOnlySpan<byte> stored, IReadOnlyDictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(Conversions, conversionId);
            bool lower = conversion.Options[0].ValueIn(options) == LowerUpper;
            bool crlf = conversion.Options[1].ValueIn(options) == "CR LF";
            List<byte> result = new List<byte>(stored.Length);
            for (int i = 0; i < stored.Length; i++)
            {
                byte p = stored[i];
                if (p == 0x0D)
                {
                    if (crlf)
                    {
                        result.Add(0x0D);
                    }

                    result.Add(0x0A);
                }
                else if (IsShared(p))
                {
                    result.Add(p);
                }
                else if (p >= 0x41 && p <= 0x5A)
                {
                    result.Add(lower ? (byte)(p + 0x20) : p);
                }
                else if (lower && p >= 0xC1 && p <= 0xDA)
                {
                    result.Add((byte)(p - 0x80));
                }
                else if (lower && p >= 0x61 && p <= 0x7A)
                {
                    result.Add((byte)(p - 0x20));
                }
                else
                {
                    throw new ArgumentException(TransferText.NoMeaning(conversion.Name, p, i), nameof(stored));
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Converts host ASCII text to a PETSCII text file.
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
        /// The PETSCII bytes to write.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when a host character has no PETSCII code.
        /// </exception>
        public static byte[] FromHost(string conversionId, ReadOnlySpan<byte> host, IReadOnlyDictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(Conversions, conversionId);
            bool lower = conversion.Options[0].ValueIn(options) == LowerUpper;
            conversion.Options[1].ValueIn(options);
            List<byte> result = new List<byte>(host.Length);
            for (int i = 0; i < host.Length; i++)
            {
                byte a = host[i];
                if (a == 0x0D && i + 1 < host.Length && host[i + 1] == 0x0A)
                {
                    // CR LF is one line end; the LF writes it.
                    continue;
                }

                if (a == 0x0A)
                {
                    result.Add(0x0D);
                }
                else if (IsShared(a))
                {
                    result.Add(a);
                }
                else if (a >= (byte)'A' && a <= (byte)'Z')
                {
                    result.Add(lower ? (byte)(a + 0x80) : a);
                }
                else if (a >= (byte)'a' && a <= (byte)'z')
                {
                    result.Add((byte)(a - 0x20));
                }
                else
                {
                    throw new ArgumentException(TransferText.NoMeaning(conversion.Name, a, i), nameof(host));
                }
            }

            return result.ToArray();
        }

        // The codes PETSCII and ASCII share in both character sets.
        private static bool IsShared(byte value)
        {
            return (value >= 0x20 && value <= 0x40) || value == 0x5B || value == 0x5D;
        }
    }
}
