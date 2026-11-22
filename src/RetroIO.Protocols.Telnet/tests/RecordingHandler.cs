//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

namespace RetroIO.Protocols.Telnet.Tests
{
    /// <summary>
    /// Writes every event as one line of text, so a test can compare the whole sequence at once.
    /// Consecutive data calls are joined, because how data is split carries no meaning.
    /// </summary>
    internal sealed class RecordingHandler : ITelnetReaderHandler
    {
        private readonly List<string> _events = new List<string>();
        private readonly List<byte> _pendingData = new List<byte>();

        public IReadOnlyList<string> Events
        {
            get
            {
                FlushData();
                return _events;
            }
        }

        public void OnData(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                _pendingData.Add(data[i]);
            }
        }

        public void OnEndOfRecord()
        {
            FlushData();
            _events.Add("EOR");
        }

        public void OnNegotiation(byte verb, byte option)
        {
            FlushData();
            _events.Add("NEG " + verb + " " + option);
        }

        public void OnSubnegotiation(byte option, ReadOnlySpan<byte> parameters)
        {
            FlushData();
            _events.Add("SB " + option + " " + Hex(parameters));
        }

        public void OnCommand(byte command)
        {
            FlushData();
            _events.Add("CMD " + command);
        }

        internal static string Hex(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private void FlushData()
        {
            if (_pendingData.Count > 0)
            {
                _events.Add("DATA " + Hex(_pendingData.ToArray()));
                _pendingData.Clear();
            }
        }
    }
}
