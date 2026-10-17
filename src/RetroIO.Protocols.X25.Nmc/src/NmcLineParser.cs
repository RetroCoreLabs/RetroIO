//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;

namespace RetroIO.Protocols.X25.Nmc
{
    /// <summary>
    /// The kind of management line recognised by <see cref="NmcLineParser"/>.
    /// </summary>
    public enum NmcLineKind
    {
        /// <summary>
        /// Not a recognised management line.
        /// </summary>
        Unknown,

        /// <summary>
        /// An <c>INFO</c> identity line.
        /// </summary>
        Info,

        /// <summary>
        /// A <c>STATS</c> counters line.
        /// </summary>
        Stats,

        /// <summary>
        /// A <c>NEIGHBOUR</c> topology line.
        /// </summary>
        Neighbour,

        /// <summary>
        /// A <c>ROUTE</c> routing-table line.
        /// </summary>
        Route,

        /// <summary>
        /// An <c>EVENT</c> notification line.
        /// </summary>
        Event,

        /// <summary>
        /// An <c>OK</c> acknowledgement line.
        /// </summary>
        Ok,

        /// <summary>
        /// A <c>PONG</c> reply.
        /// </summary>
        Pong
    }

    /// <summary>
    /// The result of parsing one management line: a discriminator plus whichever typed record applies.
    /// </summary>
    public sealed class NmcParsedLine
    {
        /// <summary>
        /// The recognised line kind.
        /// </summary>
        public NmcLineKind Kind { get; set; } = NmcLineKind.Unknown;

        /// <summary>
        /// The identity record for an <see cref="NmcLineKind.Info"/> line, else null.
        /// </summary>
        public NmcNodeInfo? Info { get; set; }

        /// <summary>
        /// The counters record for an <see cref="NmcLineKind.Stats"/> line, else null.
        /// </summary>
        public NmcStats? Stats { get; set; }

        /// <summary>
        /// The neighbour record for an <see cref="NmcLineKind.Neighbour"/> line, else null.
        /// </summary>
        public NmcNeighbour? Neighbour { get; set; }

        /// <summary>
        /// The route record for an <see cref="NmcLineKind.Route"/> line, else null.
        /// </summary>
        public NmcRoute? Route { get; set; }

        /// <summary>
        /// The free text for an <see cref="NmcLineKind.Event"/> or <see cref="NmcLineKind.Ok"/> line.
        /// </summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parses the switch management API's text response lines into typed records. Pure and allocation-light
    /// so it can be shared by the console and the GUI and verified byte-for-byte against the switch output
    /// (see docs/NMC_API_V1.md).
    /// </summary>
    public static class NmcLineParser
    {
        /// <summary>
        /// Parses one response line (with any trailing CR/LF already removed).
        /// </summary>
        /// <param name="line">
        /// The response line.
        /// </param>
        /// <returns>
        /// The parsed result; <see cref="NmcLineKind.Unknown"/> for an unrecognised line.
        /// </returns>
        public static NmcParsedLine Parse(string line)
        {
            NmcParsedLine result = new NmcParsedLine();
            if (string.IsNullOrEmpty(line))
            {
                return result;
            }

            // INFO and STATS use pipe-delimited fields (values may contain spaces); the record types use
            // space-delimited fields (values never contain spaces).
            if (StartsWithKeyword(line, "INFO", out string infoPayload))
            {
                result.Kind = NmcLineKind.Info;
                result.Info = new NmcNodeInfo
                {
                    Dnic = Field(infoPayload, "dnic", '|'),
                    Name = Field(infoPayload, "name", '|'),
                    Location = Field(infoPayload, "location", '|'),
                    Coords = Field(infoPayload, "coords", '|')
                };
            }
            else if (StartsWithKeyword(line, "STATS", out string statsPayload))
            {
                result.Kind = NmcLineKind.Stats;
                result.Stats = new NmcStats
                {
                    Dnic = Field(statsPayload, "dnic", '|'),
                    Ports = ToInt(Field(statsPayload, "ports", '|')),
                    Stitches = ToInt(Field(statsPayload, "stitches", '|')),
                    Calls = ToLong(Field(statsPayload, "calls", '|')),
                    PacketsIn = ToLong(Field(statsPayload, "pkts_in", '|')),
                    PacketsOut = ToLong(Field(statsPayload, "pkts_out", '|')),
                    Bytes = ToLong(Field(statsPayload, "bytes", '|')),
                    UptimeSeconds = ToLong(Field(statsPayload, "up", '|'))
                };
            }
            else if (StartsWithKeyword(line, "NEIGHBOUR", out string nbPayload))
            {
                result.Kind = NmcLineKind.Neighbour;
                result.Neighbour = new NmcNeighbour
                {
                    Name = Field(nbPayload, "name", ' '),
                    MgmtAddr = Field(nbPayload, "mgmt", ' '),
                    Via = ToInt(Field(nbPayload, "via", ' ')),
                    Up = Field(nbPayload, "up", ' ') == "1"
                };
            }
            else if (StartsWithKeyword(line, "ROUTE", out string routePayload))
            {
                result.Kind = NmcLineKind.Route;
                result.Route = new NmcRoute
                {
                    Type = Field(routePayload, "type", ' '),
                    Address = Field(routePayload, "addr", ' '),
                    Port = ToInt(Field(routePayload, "port", ' ')),
                    PrefixLen = ToInt(Field(routePayload, "prefix", ' '))
                };
            }
            else if (StartsWithKeyword(line, "EVENT", out string eventPayload))
            {
                result.Kind = NmcLineKind.Event;
                result.Text = eventPayload;
            }
            else if (StartsWithKeyword(line, "OK", out string okPayload))
            {
                result.Kind = NmcLineKind.Ok;
                result.Text = okPayload;
            }
            else if (line == "PONG")
            {
                result.Kind = NmcLineKind.Pong;
            }

            return result;
        }

        /// <summary>
        /// Extracts the value of a <c>key=value</c> field from a delimited payload, matching the key at a
        /// token boundary so one key cannot match inside another.
        /// </summary>
        /// <param name="payload">
        /// The delimited field list (after the keyword).
        /// </param>
        /// <param name="key">
        /// The field key to find.
        /// </param>
        /// <param name="sep">
        /// The field separator (<c>|</c> or space).
        /// </param>
        /// <returns>
        /// The field value, or an empty string when the key is absent.
        /// </returns>
        public static string Field(string payload, string key, char sep)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return string.Empty;
            }

            int i = 0;
            int n = payload.Length;
            int klen = key.Length;
            while (i < n)
            {
                int end = payload.IndexOf(sep, i);
                if (end < 0)
                {
                    end = n;
                }

                // A token matches when it is exactly key + '=' + value.
                if (end - i > klen && payload[i + klen] == '='
                    && string.CompareOrdinal(payload, i, key, 0, klen) == 0)
                {
                    return payload.Substring(i + klen + 1, end - (i + klen + 1));
                }

                i = end + 1;
            }

            return string.Empty;
        }

        // True when the line begins with the keyword followed by a single space; yields the remaining payload.
        private static bool StartsWithKeyword(string line, string keyword, out string payload)
        {
            int klen = keyword.Length;
            if (line.Length > klen && line[klen] == ' '
                && string.CompareOrdinal(line, 0, keyword, 0, klen) == 0)
            {
                payload = line.Substring(klen + 1);
                return true;
            }

            payload = string.Empty;
            return false;
        }

        private static int ToInt(string s)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private static long ToLong(string s)
        {
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        }
    }
}
