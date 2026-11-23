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
    /// Line-end helpers shared by the text conversions of the drivers.
    /// </summary>
    public static class TransferText
    {
        /// <summary>
        /// Turns every CR LF pair into LF; a CR on its own stays.
        /// </summary>
        /// <param name="data">
        /// The text bytes.
        /// </param>
        /// <returns>
        /// A new array.
        /// </returns>
        public static byte[] CrLfToLf(ReadOnlySpan<byte> data)
        {
            List<byte> result = new List<byte>(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0x0D && i + 1 < data.Length && data[i + 1] == 0x0A)
                {
                    continue;
                }

                result.Add(data[i]);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Turns every LF that has no CR before it into CR LF; a CR LF pair stays as it is.
        /// </summary>
        /// <param name="data">
        /// The text bytes.
        /// </param>
        /// <returns>
        /// A new array.
        /// </returns>
        public static byte[] LfToCrLf(ReadOnlySpan<byte> data)
        {
            List<byte> result = new List<byte>(data.Length + (data.Length / 16));
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0x0A && (i == 0 || data[i - 1] != 0x0D))
                {
                    result.Add(0x0D);
                }

                result.Add(data[i]);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Builds the message for a byte a conversion has no answer for.
        /// </summary>
        /// <param name="conversionName">
        /// The conversion's name.
        /// </param>
        /// <param name="value">
        /// The byte.
        /// </param>
        /// <param name="position">
        /// Where it is in the input.
        /// </param>
        /// <returns>
        /// The message, which tells the user to copy the exact bytes instead.
        /// </returns>
        public static string NoMeaning(string conversionName, byte value, int position)
        {
            return conversionName + ": byte " + value.ToString("X2") + "h at position " + position
                + " has no meaning in this conversion; copy the exact bytes instead.";
        }

        /// <summary>
        /// Finds a conversion by id.
        /// </summary>
        /// <param name="conversions">
        /// The provider's conversions.
        /// </param>
        /// <param name="conversionId">
        /// The id asked for.
        /// </param>
        /// <returns>
        /// The conversion.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when no conversion has <paramref name="conversionId"/>.
        /// </exception>
        public static TransferConversion Find(IReadOnlyList<TransferConversion> conversions, string conversionId)
        {
            for (int i = 0; i < conversions.Count; i++)
            {
                if (conversions[i].Id == conversionId)
                {
                    return conversions[i];
                }
            }

            throw new ArgumentException("No copy conversion " + conversionId, nameof(conversionId));
        }
    }
}
