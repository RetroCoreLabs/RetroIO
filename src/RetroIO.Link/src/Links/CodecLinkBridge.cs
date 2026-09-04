using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Framing;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// Bridges a framing codec (below) to a LAPB instance (above), owning the frame check sequence.
    /// </summary>
    /// <remarks>
    /// This is where the FCS ownership lives, relocated out of the framing layer. The framing codecs
    /// (SLIP, HDLC/PPP, XOT) carry an opaque, delimited payload with no checksum; this bridge appends the
    /// CRC-16 FCS on transmit and validates then strips it on receive, so the FCS-agnostic
    /// <see cref="LapbInstance"/> exchanges plain address+control+information frames.
    ///
    /// A codec plus this bridge plus a <see cref="LapbInstance"/> forms a complete link over any
    /// <see cref="IFrameTransport"/>. Swapping the codec (SLIP / HDLC / XOT) changes only the wire framing;
    /// the LAPB engine and FCS handling are unchanged: framing is delimiting-only and the FCS (CRC-16)
    /// belongs to the LAPB module.
    /// </remarks>
    public sealed class CodecLinkBridge : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _linkId;
        private readonly FrameCodecBase _codec;
        private readonly LapbInstance _lapb;
        private bool _disposed;

        /// <summary>
        /// Wires a framing codec to a LAPB instance and begins bridging frames between them.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the owning link, used in log messages.
        /// </param>
        /// <param name="codec">
        /// The framing codec below (delimiting/escaping only, no FCS).
        /// </param>
        /// <param name="lapb">
        /// The LAPB state machine above (exchanges FCS-free address+control+information frames).
        /// </param>
        /// <param name="logger">
        /// Logger for dropped-frame diagnostics.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        public CodecLinkBridge(string linkId, FrameCodecBase codec, LapbInstance lapb, ILogger logger)
        {
            _linkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _lapb = lapb ?? throw new ArgumentNullException(nameof(lapb));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _codec.FrameReceived += OnCodecFrameReceived;
            _lapb.FrameToTransmit += OnLapbFrameToTransmit;
        }

        /// <summary>
        /// Unsubscribes from the codec and LAPB instance.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _codec.FrameReceived -= OnCodecFrameReceived;
            _lapb.FrameToTransmit -= OnLapbFrameToTransmit;
        }

        // Codec delivered a complete de-framed frame: validate + strip FCS, then hand to LAPB.
        private void OnCodecFrameReceived(string linkId, ReadOnlySpan<byte> frame)
        {
            if (!Fcs16.IsValid(frame))
            {
                _logger.LogWarning("Link[{Link}]: dropping received frame with bad FCS ({Length} bytes)",
                    _linkId, frame.Length);
                return;
            }

            int payloadLength = frame.Length - 2;
            byte[] payload = frame.Slice(0, payloadLength).ToArray();
            _lapb.ReceiveFrame(payload, payloadLength);
        }

        // LAPB wants to transmit a frame (address+control+information, no FCS): append FCS, then frame it.
        private void OnLapbFrameToTransmit(object? sender, LapbFrameEventArgs args)
        {
            int length = args.Length;
            byte[] withFcs = new byte[length + 2];
            Array.Copy(args.FrameData, 0, withFcs, 0, length);
            Fcs16.WriteFcs(args.FrameData.AsSpan(0, length), withFcs.AsSpan(length));
            _codec.SendFrame(withFcs);
        }
    }
}
