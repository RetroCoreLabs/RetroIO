using System;
using System.Globalization;
using System.Text;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// Decodes a raw X.25 packet into a <see cref="CaptureRecord"/> for the monitoring stream, reusing the
    /// clean <see cref="X25PacketParser"/> so the wire decode never drifts from the codec.
    /// </summary>
    /// <remarks>
    /// Only the X.25 layer is produced here; the layered model leaves room for LAPB/HDLC/Physical layers to
    /// be prepended by a link-level decoder when a capture originates below X.25 (for example a serial-LAPB
    /// tap). XOT captures carry the X.25 packet directly, so this layer is the whole decode.
    /// </remarks>
    public static class X25CaptureDecoder
    {
        /// <summary>
        /// Decodes an X.25 packet into a capture record.
        /// </summary>
        /// <param name="packet">
        /// The raw X.25 packet bytes.
        /// </param>
        /// <param name="port">
        /// The observing port/link identifier.
        /// </param>
        /// <param name="direction">
        /// The travel direction relative to the observing port.
        /// </param>
        /// <param name="seq">
        /// The server-assigned sequence number.
        /// </param>
        /// <param name="timestampMs">
        /// The capture timestamp in milliseconds.
        /// </param>
        /// <returns>
        /// The decoded capture record.
        /// </returns>
        public static CaptureRecord Decode(byte[] packet, int port, CaptureDirection direction, long seq, long timestampMs)
        {
            X25PacketInfo info = X25PacketParser.ParsePacket(packet);

            CaptureRecord record = new CaptureRecord
            {
                Seq = seq,
                TimestampMs = timestampMs,
                Port = port,
                Direction = direction,
                TotalLength = packet.Length,
                Frame = packet,
                Hex = ToHex(packet),
                PacketType = info.PacketType.ToString(),
                Called = info.CalledAddress ?? string.Empty,
                Calling = info.CallingAddress ?? string.Empty
            };

            // The typed dissector builds the X.25 layer with per-field byte offsets so the viewer can
            // highlight the matching hex (see X25Dissector / docs/MONITOR_PROTOCOL_V1.md section 12).
            record.Layers.Add(X25Dissector.Dissect(packet, info));
            return record;
        }

        private static string ToHex(byte[] data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
