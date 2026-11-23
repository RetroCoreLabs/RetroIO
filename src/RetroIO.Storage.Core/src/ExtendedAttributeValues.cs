//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Reads values out of a property bag the way they come back from a <c>.xat</c> file: a number
    /// may be any integer type, a flag a bool or the text <c>true</c> or <c>false</c>.
    /// </summary>
    public static class ExtendedAttributeValues
    {
        /// <summary>
        /// Reads a flag.
        /// </summary>
        /// <param name="bag">
        /// The property bag.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="value">
        /// The flag, or <c>false</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the key is there.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the value is not a flag.
        /// </exception>
        public static bool TryGetBool(IDictionary<string, object>? bag, string key, out bool value)
        {
            value = false;
            if (bag == null || !bag.TryGetValue(key, out object? raw) || raw == null)
            {
                return false;
            }

            if (raw is bool b)
            {
                value = b;
                return true;
            }

            if (raw is string text && bool.TryParse(text, out bool parsed))
            {
                value = parsed;
                return true;
            }

            throw new ArgumentException(key + " must be true or false, not " + raw, nameof(bag));
        }

        /// <summary>
        /// Reads a whole number.
        /// </summary>
        /// <param name="bag">
        /// The property bag.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="value">
        /// The number, or 0.
        /// </param>
        /// <returns>
        /// <c>true</c> when the key is there.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the value is not a whole number.
        /// </exception>
        public static bool TryGetLong(IDictionary<string, object>? bag, string key, out long value)
        {
            value = 0;
            if (bag == null || !bag.TryGetValue(key, out object? raw) || raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case byte v: value = v; return true;
                case sbyte v: value = v; return true;
                case short v: value = v; return true;
                case ushort v: value = v; return true;
                case int v: value = v; return true;
                case uint v: value = v; return true;
                case long v: value = v; return true;
                case ulong v when v <= long.MaxValue: value = (long)v; return true;
                case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed):
                    value = parsed;
                    return true;
            }

            throw new ArgumentException(key + " must be a whole number, not " + raw, nameof(bag));
        }

        /// <summary>
        /// Reads a text value.
        /// </summary>
        /// <param name="bag">
        /// The property bag.
        /// </param>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <param name="value">
        /// The text, or empty.
        /// </param>
        /// <returns>
        /// <c>true</c> when the key is there.
        /// </returns>
        public static bool TryGetString(IDictionary<string, object>? bag, string key, out string value)
        {
            value = string.Empty;
            if (bag == null || !bag.TryGetValue(key, out object? raw) || raw == null)
            {
                return false;
            }

            value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            return true;
        }

        /// <summary>
        /// Copies every property of one bag into another.
        /// </summary>
        /// <param name="source">
        /// The properties, or <c>null</c> for none.
        /// </param>
        /// <param name="target">
        /// Where they go, or <c>null</c> to drop them.
        /// </param>
        public static void CopyInto(IDictionary<string, object>? source, IDictionary<string, object>? target)
        {
            if (source == null || target == null)
            {
                return;
            }

            using IEnumerator<KeyValuePair<string, object>> pairs = source.GetEnumerator();
            while (pairs.MoveNext())
            {
                target[pairs.Current.Key] = pairs.Current.Value;
            }
        }

        /// <summary>
        /// Picks properties a driver already put on a file's metadata.
        /// </summary>
        /// <param name="metadata">
        /// The file's metadata, or <c>null</c> when there is no such file.
        /// </param>
        /// <param name="keys">
        /// The keys to take.
        /// </param>
        /// <returns>
        /// The properties found, or <c>null</c> when <paramref name="metadata"/> is <c>null</c>.
        /// </returns>
        public static IDictionary<string, object>? FromMetadata(FileMetadata? metadata, string[] keys)
        {
            if (metadata == null)
            {
                return null;
            }

            Dictionary<string, object> bag = new Dictionary<string, object>();
            for (int i = 0; i < keys.Length; i++)
            {
                if (metadata.Properties.TryGetValue(keys[i], out object? value) && value != null)
                {
                    bag[keys[i]] = value;
                }
            }

            return bag;
        }

        /// <summary>
        /// Capabilities of a read-only driver: these keys are read, none is written.
        /// </summary>
        /// <param name="keys">
        /// The keys.
        /// </param>
        /// <returns>
        /// The capabilities.
        /// </returns>
        public static FileSystemCapabilities ReadOnly(string[] keys)
        {
            return new FileSystemCapabilities
            {
                SupportsExtendedAttributes = keys.Length > 0,
                SupportedPropertyKeys = (string[])keys.Clone(),
            };
        }
    }
}
