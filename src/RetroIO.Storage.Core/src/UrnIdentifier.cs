//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Utilities for generating and parsing URN-safe file identifiers.
    ///
    /// Retro filesystems often contain filenames with spaces, special characters (?, *, /),
    /// and non-ASCII encodings (PETSCII, etc.) that are invalid in URN/URL contexts.
    /// This class provides encoding/decoding to make such filenames URN-compatible while
    /// preserving the original display name for user presentation.
    ///
    /// Example transformations:
    ///   "CARMEN SANDIEGO?" → "CARMEN%20SANDIEGO%3F"
    ///   "WHERE IN THE"     → "WHERE%20IN%20THE"
    ///
    /// Supports three identification methods:
    /// 1. Original filename (for display)
    /// 2. URL-encoded identifier (for URN construction)
    /// 3. Numeric index (#1, #2, etc. for interactive use)
    /// </summary>
    public static class UrnIdentifier
    {
        /// <summary>
        /// Generates a URN-safe identifier from a filename.
        /// Uses percent-encoding per RFC 3986 for special characters while keeping
        /// common alphanumeric characters readable for easier debugging and logging.
        ///
        /// This method encodes:
        /// - Spaces as %20
        /// - Special characters (?, !, *, etc.) as their hex equivalents
        /// - Non-ASCII characters (PETSCII, etc.) as UTF-8 byte sequences
        ///
        /// This method preserves:
        /// - Letters A-Z, a-z
        /// - Digits 0-9
        /// - Unreserved symbols: - . _ ~
        /// </summary>
        /// <param name="fileName">Original filename (may contain spaces, special chars, PETSCII)</param>
        /// <returns>URN-safe identifier (e.g., "WHERE%20IN%20THE" or "CARMEN%20SANDIEGO%3F")</returns>
        public static string Encode(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder(fileName.Length * 2);

            // Process each character in the filename
            for (int i = 0; i < fileName.Length; i++)
            {
                char c = fileName[i];

                // Keep unreserved characters as-is per RFC 3986
                // This makes identifiers more readable while remaining URN-safe
                if (IsUnreserved(c))
                {
                    result.Append(c);
                }
                else
                {
                    // Percent-encode everything else
                    // Convert to UTF-8 bytes first to handle non-ASCII characters
                    byte[] bytes = Encoding.UTF8.GetBytes(new char[] { c });
                    for (int j = 0; j < bytes.Length; j++)
                    {
                        result.Append('%');
                        result.Append(bytes[j].ToString("X2"));
                    }
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Decodes a URN-safe identifier back to the original filename.
        /// Reverses the percent-encoding applied by Encode(), converting sequences like
        /// %20 back to spaces and %3F back to question marks.
        ///
        /// This enables URN identifiers to be used in file operations:
        ///   Input:  "CARMEN%20SANDIEGO%3F"
        ///   Output: "CARMEN SANDIEGO?"
        ///
        /// Note: Simple ASCII decoding is used here. For proper UTF-8 multi-byte
        /// sequence handling, consider using System.Web.HttpUtility.UrlDecode or
        /// implementing full UTF-8 reconstruction.
        /// </summary>
        /// <param name="identifier">URN-safe identifier with percent-encoding</param>
        /// <returns>Original filename with special characters restored</returns>
        public static string Decode(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder(identifier.Length);
            int i = 0;

            while (i < identifier.Length)
            {
                char c = identifier[i];

                if (c == '%' && i + 2 < identifier.Length)
                {
                    // Decode percent-encoded byte (e.g., %20 → space, %3F → ?)
                    string hex = identifier.Substring(i + 1, 2);
                    if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    {
                        result.Append((char)b);
                        i += 3; // Skip the %XX sequence
                        continue;
                    }
                }

                // Not a valid percent sequence, append as-is
                result.Append(c);
                i++;
            }

            return result.ToString();
        }

        /// <summary>
        /// Checks if a character is unreserved per RFC 3986 section 2.3.
        /// Unreserved characters can appear in URIs without percent-encoding.
        ///
        /// Unreserved set: A-Z a-z 0-9 - . _ ~
        ///
        /// All other characters (including space, ?, /, etc.) must be percent-encoded
        /// for URN/URI safety.
        /// </summary>
        /// <param name="c">Character to check</param>
        /// <returns>True if character is unreserved and doesn't need encoding</returns>
        private static bool IsUnreserved(char c)
        {
            return (c >= 'A' && c <= 'Z') ||
                   (c >= 'a' && c <= 'z') ||
                   (c >= '0' && c <= '9') ||
                   c == '-' || c == '.' || c == '_' || c == '~';
        }

        /// <summary>
        /// Generates a numeric identifier for a file based on its position in the directory.
        /// This provides a simple, user-friendly reference method for interactive sessions.
        ///
        /// Examples:
        ///   FromIndex(0) → "#1"
        ///   FromIndex(1) → "#2"
        ///   FromIndex(99) → "#100"
        ///
        /// These identifiers can be used as shortcuts in URNs:
        ///   urn:retrofs:local::volume:/#1
        ///   urn:retrofs:local::volume:/#2
        /// </summary>
        /// <param name="index">Zero-based index in directory listing</param>
        /// <returns>Numeric identifier string (e.g., "#1", "#2")</returns>
        public static string FromIndex(int index)
        {
            return $"#{index + 1}";
        }

        /// <summary>
        /// Parses a numeric identifier back to a zero-based array index.
        /// Validates that the identifier starts with '#' and contains a positive number.
        ///
        /// Examples:
        ///   "#1"  → index = 0 (first file)
        ///   "#2"  → index = 1 (second file)
        ///   "#99" → index = 98
        ///   "1"   → false (missing #)
        ///   "#0"  → false (must be positive)
        ///   "#-1" → false (must be positive)
        /// </summary>
        /// <param name="identifier">String identifier to parse (e.g., "#1", "#2")</param>
        /// <param name="index">Output: zero-based array index if successful</param>
        /// <returns>True if successfully parsed, false otherwise</returns>
        public static bool TryParseIndex(string identifier, out int index)
        {
            index = -1;

            // Must start with '#'
            if (string.IsNullOrEmpty(identifier) || identifier[0] != '#')
            {
                return false;
            }

            // Parse number after '#' (must be positive)
            if (int.TryParse(identifier.Substring(1), out int num) && num > 0)
            {
                index = num - 1; // Convert to zero-based
                return true;
            }

            return false;
        }
    }
}
