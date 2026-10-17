//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Protocols.X25.Common
{
    /// <summary>
    /// A small INI-style configuration reader.
    /// </summary>
    /// <remarks>
    /// Parsing rules:
    ///  - lines are trimmed; empty lines and lines beginning <c>#</c> or <c>;</c> are ignored.
    ///  - <c>[section]</c> switches the current section (text up to the first <c>]</c>).
    ///  - <c>key = value</c> splits on the FIRST <c>=</c>; key and value are trimmed; the value keeps any
    ///    interior spaces.
    ///  - entries are stored as a flat, ordered list; lookups are case-sensitive exact and return the FIRST
    ///    match (so a duplicated key keeps its first value).
    ///  - at most <see cref="MaxKeys"/> entries are kept (default 128); further key/value lines are dropped,
    ///    while section headers still advance the current section.
    /// Integer reads use lenient parsing (leading optional sign + digits, 0 when non-numeric).
    /// </remarks>
    public sealed class IniConfig
    {
        /// <summary>
        /// One parsed configuration entry.
        /// </summary>
        private readonly struct Entry
        {
            public readonly string Section;
            public readonly string Key;
            public readonly string Value;

            public Entry(string section, string key, string value)
            {
                Section = section;
                Key = key;
                Value = value;
            }
        }

        /// <summary>
        /// The default cap on stored entries.
        /// </summary>
        public const int DefaultMaxKeys = 128;

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>
        /// Maximum number of key/value entries retained. Entries beyond this are silently dropped.
        /// </summary>
        public int MaxKeys { get; }

        /// <summary>
        /// Creates an empty configuration.
        /// </summary>
        /// <param name="maxKeys">
        /// Cap on stored entries; defaults to <see cref="DefaultMaxKeys"/>.
        /// </param>
        public IniConfig(int maxKeys = DefaultMaxKeys)
        {
            MaxKeys = maxKeys > 0 ? maxKeys : DefaultMaxKeys;
        }

        /// <summary>
        /// Number of entries retained.
        /// </summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Loads and parses an INI file.
        /// </summary>
        /// <param name="path">
        /// Path to the configuration file.
        /// </param>
        /// <param name="maxKeys">
        /// Cap on stored entries.
        /// </param>
        /// <returns>
        /// The parsed configuration.
        /// </returns>
        /// <exception cref="IOException">
        /// Thrown when the file cannot be read.
        /// </exception>
        public static IniConfig Load(string path, int maxKeys = DefaultMaxKeys)
        {
            string[] lines = File.ReadAllLines(path);
            return Parse(lines, maxKeys);
        }

        /// <summary>
        /// Parses INI text supplied as individual lines.
        /// </summary>
        /// <param name="lines">
        /// The lines to parse.
        /// </param>
        /// <param name="maxKeys">
        /// Cap on stored entries.
        /// </param>
        /// <returns>
        /// The parsed configuration.
        /// </returns>
        public static IniConfig Parse(IReadOnlyList<string> lines, int maxKeys = DefaultMaxKeys)
        {
            IniConfig cfg = new IniConfig(maxKeys);
            string currentSection = string.Empty;

            for (int i = 0; i < lines.Count; i++)
            {
                string p = Trim(lines[i]);

                if (p.Length == 0 || p[0] == '#' || p[0] == ';')
                {
                    continue;
                }

                if (p[0] == '[')
                {
                    int end = p.IndexOf(']');
                    if (end >= 0)
                    {
                        currentSection = p.Substring(1, end - 1);
                    }
                    continue;
                }

                int eq = p.IndexOf('=');
                if (eq >= 0 && cfg._entries.Count < cfg.MaxKeys)
                {
                    string key = Trim(p.Substring(0, eq));
                    string val = Trim(p.Substring(eq + 1));
                    cfg._entries.Add(new Entry(currentSection, key, val));
                }
            }

            return cfg;
        }

        /// <summary>
        /// Returns whether any entry exists in the given section.
        /// </summary>
        /// <param name="section">
        /// Section name.
        /// </param>
        /// <returns>
        /// True if the section has at least one entry.
        /// </returns>
        public bool HasSection(string section)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Section == section)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the value for a section/key, or null when absent (first match wins).
        /// </summary>
        /// <param name="section">
        /// Section name.
        /// </param>
        /// <param name="key">
        /// Key name.
        /// </param>
        /// <returns>
        /// The value, or null if not present.
        /// </returns>
        public string? Get(string section, string key)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Section == section && _entries[i].Key == key)
                {
                    return _entries[i].Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the value for a section/key, or a default when absent.
        /// </summary>
        /// <param name="section">
        /// Section name.
        /// </param>
        /// <param name="key">
        /// Key name.
        /// </param>
        /// <param name="defaultValue">
        /// Value returned when the key is absent.
        /// </param>
        /// <returns>
        /// The value or <paramref name="defaultValue"/>.
        /// </returns>
        public string GetString(string section, string key, string defaultValue)
        {
            return Get(section, key) ?? defaultValue;
        }

        /// <summary>
        /// Returns an integer value for a section/key using lenient integer parsing, or a default when absent.
        /// </summary>
        /// <param name="section">
        /// Section name.
        /// </param>
        /// <param name="key">
        /// Key name.
        /// </param>
        /// <param name="defaultValue">
        /// Value returned when the key is absent.
        /// </param>
        /// <returns>
        /// The parsed integer, <paramref name="defaultValue"/> when absent, or 0 when present but non-numeric.
        /// </returns>
        public int GetInt(string section, string key, int defaultValue)
        {
            string? val = Get(section, key);
            if (val == null)
            {
                return defaultValue;
            }

            return Atoi(val);
        }

        /// <summary>
        /// Enumerates the entries in a section, in file order, as key/value pairs.
        /// </summary>
        /// <param name="section">
        /// Section name.
        /// </param>
        /// <returns>
        /// The key/value pairs of the section.
        /// </returns>
        public IEnumerable<KeyValuePair<string, string>> GetSection(string section)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Section == section)
                {
                    yield return new KeyValuePair<string, string>(_entries[i].Key, _entries[i].Value);
                }
            }
        }

        /// <summary>
        /// Parses a leading integer leniently: skip leading whitespace, take an optional sign and the run of
        /// digits, ignore the rest; return 0 when there is no number.
        /// </summary>
        /// <param name="s">
        /// The string to parse.
        /// </param>
        /// <returns>
        /// The parsed integer, or 0.
        /// </returns>
        public static int Atoi(string s)
        {
            int i = 0;
            int n = s.Length;

            while (i < n && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            bool negative = false;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                negative = s[i] == '-';
                i++;
            }

            long value = 0;
            bool any = false;
            while (i < n && s[i] >= '0' && s[i] <= '9')
            {
                value = value * 10 + (s[i] - '0');
                any = true;
                i++;
                // Saturate rather than overflow; real config values are small.
                if (value > int.MaxValue)
                {
                    value = int.MaxValue;
                }
            }

            if (!any)
            {
                return 0;
            }

            return negative ? (int)(-value) : (int)value;
        }

        /// <summary>
        /// Trims leading and trailing whitespace.
        /// </summary>
        /// <param name="s">
        /// The string to trim.
        /// </param>
        /// <returns>
        /// The trimmed string.
        /// </returns>
        private static string Trim(string s)
        {
            return s.Trim();
        }
    }
}
