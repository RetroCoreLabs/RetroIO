using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using RetroIO.Protocols.Xmsg.Ethernet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Ethernet.Tests
{
    /// <summary>
    /// Holds the ND link layer's constants to what real ND machines actually do, by reading the
    /// captures the constants were measured from.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// <para>
    /// <see cref="NdLinkLayer.SendWindow"/> and <see cref="NdLinkHeader.SequenceModulus"/> are not
    /// specified anywhere - they were counted off three real machine-to-machine captures on
    /// 2026-08-11. A number justified by a measurement should be pinned BY that measurement, so
    /// that changing it breaks a test rather than a live machine. Before these tests existed the
    /// evidence lived only in a comment, and the code did not obey it: the layer sent with no
    /// window at all and wrapped the sequence at 256.
    /// </para>
    /// <para><b>What that cost</b></para>
    /// <para>
    /// Live on D100 our unacknowledged backlog reached 33 frames. D100 re-sent everything it had
    /// not seen acknowledged, our file server took each repeat for a new request, and D100 gave up
    /// with SINTRAN error 267 octal. Two nights went into the file-access protocol for a fault one
    /// layer below it.
    /// </para>
    /// <para><b>These read the SHIPPED captures, not fixtures</b></para>
    /// <para>
    /// If the capture files move or go missing these tests FAIL rather than skip. A conformance
    /// test that quietly passes when it cannot find its evidence is worse than no test - it reads
    /// as proof while proving nothing.
    /// </para>
    /// </remarks>
    public sealed class NdLinkCaptureConformanceTests
    {
        /// <summary>
        /// The captures, relative to the folder holding them.
        /// </summary>
        private static readonly string[] CaptureNames =
        {
            "capture-list-files.txt", "capture-read.txt", "capture-write.txt"
        };

        /// <summary>
        /// One ND frame taken out of a capture line.
        /// </summary>
        private readonly struct CapturedFrame
        {
            /// <summary>
            /// Initialises a captured frame.
            /// </summary>
            /// <param name="station">
            /// The sender's station address as hex, which identifies the machine.
            /// </param>
            /// <param name="kind">
            /// The ND link kind byte.
            /// </param>
            /// <param name="sequence">
            /// The sequence the frame carried.
            /// </param>
            public CapturedFrame(string station, byte kind, byte sequence)
            {
                Station = station;
                Kind = kind;
                Sequence = sequence;
            }

            /// <summary>
            /// Gets the sender's station address as hex.
            /// </summary>
            public string Station { get; }

            /// <summary>
            /// Gets the ND link kind byte.
            /// </summary>
            public byte Kind { get; }

            /// <summary>
            /// Gets the sequence the frame carried.
            /// </summary>
            public byte Sequence { get; }
        }

        /// <summary>
        /// Finds the folder holding the captures by walking up from the test assembly.
        /// </summary>
        /// <returns>
        /// The full path of the capture folder.
        /// </returns>
        /// <remarks>
        /// Walking up rather than counting <c>..</c> segments, so the depth of the build output
        /// does not matter.
        /// </remarks>
        private static string FindCaptureFolder()
        {
            DirectoryInfo? at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null)
            {
                string candidate = Path.Combine(
                    at.FullName, "DOC", "captures", "FA-READ-WRITE-2026-08-04");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                at = at.Parent;
            }

            throw new DirectoryNotFoundException(
                "DOC\\captures\\FA-READ-WRITE-2026-08-04 was not found above " +
                AppContext.BaseDirectory +
                ". These tests measure the real captures; without them they prove nothing, so " +
                "this is a failure and not a skip.");
        }

        /// <summary>
        /// Reads every ND frame out of one capture file.
        /// </summary>
        /// <param name="path">
        /// The capture file.
        /// </param>
        /// <returns>
        /// The frames, in capture order.
        /// </returns>
        /// <remarks>
        /// Capture line format is <c>time length hex</c>. Inside the hex: 12 bytes of addresses, 2
        /// of 802.3 length, the LLC1 signature <c>A8 A8 03</c>, then the 11-byte ND link header
        /// whose byte 2 is the kind and byte 4 the sequence. Anything that is not LLC1 is skipped
        /// rather than treated as an error - a capture may hold other traffic.
        /// </remarks>
        private static List<CapturedFrame> ReadFrames(string path)
        {
            List<CapturedFrame> frames = new List<CapturedFrame>();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(
                    ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                byte[] frame;
                try
                {
                    frame = Convert.FromHexString(parts[2]);
                }
                catch (FormatException)
                {
                    continue;
                }

                // LLC1 signature, three bytes after the 14-byte 802.3 header.
                if (frame.Length < 28 ||
                    frame[14] != 0xA8 || frame[15] != 0xA8 || frame[16] != 0x03)
                {
                    continue;
                }

                string station = Convert.ToHexString(frame, 6, 6);
                frames.Add(new CapturedFrame(station, frame[19], frame[21]));
            }

            return frames;
        }

        /// <summary>
        /// Reads every frame of every capture.
        /// </summary>
        /// <returns>
        /// The capture file name paired with its frames.
        /// </returns>
        private static List<KeyValuePair<string, List<CapturedFrame>>> ReadAllCaptures()
        {
            string folder = FindCaptureFolder();
            List<KeyValuePair<string, List<CapturedFrame>>> all =
                new List<KeyValuePair<string, List<CapturedFrame>>>();
            for (int i = 0; i < CaptureNames.Length; i++)
            {
                string path = Path.Combine(folder, CaptureNames[i]);
                Assert.True(File.Exists(path), path + " is missing; these tests measure it.");
                all.Add(new KeyValuePair<string, List<CapturedFrame>>(
                    CaptureNames[i], ReadFrames(path)));
            }

            return all;
        }

        /// <summary>
        /// No real ND ever puts a sequence outside the seven-bit space our modulus describes.
        /// </summary>
        [Fact]
        public void NoCapturedFrameCarriesASequenceOutsideTheModulus()
        {
            List<KeyValuePair<string, List<CapturedFrame>>> captures = ReadAllCaptures();
            int counted = 0;

            for (int c = 0; c < captures.Count; c++)
            {
                List<CapturedFrame> frames = captures[c].Value;
                for (int i = 0; i < frames.Count; i++)
                {
                    counted++;
                    Assert.True(
                        frames[i].Sequence < NdLinkHeader.SequenceModulus,
                        captures[c].Key + " frame " + i + " carries sequence 0x" +
                        frames[i].Sequence.ToString("X2", CultureInfo.InvariantCulture) +
                        ", which does not fit NdLinkHeader.SequenceModulus of " +
                        NdLinkHeader.SequenceModulus +
                        ". Either the modulus is wrong or the capture reader is.");
                }
            }

            // Guard against the reader silently finding nothing and the assertions above passing
            // over an empty list.
            Assert.True(counted > 800, "expected 800+ ND frames across the captures, read " + counted);
        }

        /// <summary>
        /// Our send window is exactly the largest burst a real ND makes - no smaller, no larger.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This test earned its place the day it was written. The window had been set to 2 from
        /// <c>capture-list-files.txt</c> alone; run against all three captures it failed at once on
        /// <c>capture-read.txt</c>, where a real D102 sends FOUR frames before waiting. A listing
        /// sends no content messages, so it cannot show the burst a read makes.
        /// </para>
        /// <para>
        /// The two sides number independently, so each is tracked on its own. An acknowledgement
        /// carries the sender's NEXT EXPECTED value, which is the low edge of the other side's
        /// window; the difference from what that side has sent is its backlog. A side is only
        /// measured once both its numbers are known, because a capture starts mid-conversation and
        /// the opening frames would otherwise be compared against a made-up zero.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSendWindowIsTheWidestBurstARealMachineMakes()
        {
            List<KeyValuePair<string, List<CapturedFrame>>> captures = ReadAllCaptures();
            int widest = 0;

            for (int c = 0; c < captures.Count; c++)
            {
                List<CapturedFrame> frames = captures[c].Value;
                Dictionary<string, byte> sent = new Dictionary<string, byte>();
                Dictionary<string, byte> acknowledged = new Dictionary<string, byte>();
                int worst = 0;

                for (int i = 0; i < frames.Count; i++)
                {
                    CapturedFrame frame = frames[i];
                    if (frame.Kind == (byte)NdLinkFrameKind.Data)
                    {
                        sent[frame.Station] = frame.Sequence;
                    }
                    else if (frame.Kind == (byte)NdLinkFrameKind.Acknowledge)
                    {
                        // An acknowledgement is ABOUT the other side, so it belongs to whoever this
                        // frame is not from. Only two machines are ever on one of these captures.
                        foreach (KeyValuePair<string, byte> other in sent)
                        {
                            if (!string.Equals(other.Key, frame.Station, StringComparison.Ordinal))
                            {
                                acknowledged[other.Key] = frame.Sequence;
                            }
                        }
                    }
                    else
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, byte> side in sent)
                    {
                        if (!acknowledged.TryGetValue(side.Key, out byte edge))
                        {
                            continue;
                        }

                        int backlog = (side.Value + 1 - edge + NdLinkHeader.SequenceModulus)
                            % NdLinkHeader.SequenceModulus;
                        if (backlog > worst)
                        {
                            worst = backlog;
                        }

                        Assert.True(
                            backlog <= NdLinkLayer.SendWindow,
                            captures[c].Key + " frame " + i + ": station " + side.Key +
                            " had " + backlog + " frames unacknowledged, more than " +
                            "NdLinkLayer.SendWindow of " + NdLinkLayer.SendWindow +
                            ". A real machine going wider than our window means the window is " +
                            "too small; if this fails, RAISE it - do not delete the test.");
                    }
                }

                Assert.True(
                    worst >= 1,
                    captures[c].Key + " showed no outstanding frames at all, so it cannot support " +
                    "any window value - the reader is probably broken.");

                widest = worst > widest ? worst : widest;
            }

            // Lower edge: at least as wide as anything in these captures, or we serialise a file
            // read - the four-frame case here is one request's whole answer.
            Assert.True(
                NdLinkLayer.SendWindow >= widest,
                "NdLinkLayer.SendWindow is " + NdLinkLayer.SendWindow + " but a real machine sends " +
                widest + " in these captures. Below that, a content burst goes one frame at a time. " +
                "RAISE it - do not weaken this test.");

            // Upper edge: never wider than a real machine has been SEEN to go, or we are inventing
            // rather than measuring. That number is five, from a hub capture these tests cannot read
            // - see NdLinkLayer.WidestBurstSeenFromARealMachine for the citation.
            //
            // This used to be Assert.Equal(SendWindow, widest), which quietly claimed the three text
            // captures were the whole world. They are not: reading the hub captures put the real
            // maximum at five, the SECOND time in one day this number moved because a wider capture
            // was read. An equality here has to be edited every time the corpus grows, and reads as
            // proof in between.
            Assert.True(
                NdLinkLayer.SendWindow <= NdLinkLayer.WidestBurstSeenFromARealMachine,
                "NdLinkLayer.SendWindow is " + NdLinkLayer.SendWindow + ", wider than the " +
                NdLinkLayer.WidestBurstSeenFromARealMachine + " any real machine has been seen to " +
                "send. Raising it needs a capture showing one going wider, not a preference.");
        }

        /// <summary>
        /// Every acknowledgement in the captures carries a zero trailing word, which is why the
        /// send window cannot be negotiated and has to be a measured constant.
        /// </summary>
        /// <remarks>
        /// This is the evidence behind the comment on <see cref="NdLinkLayer.SendWindow"/> saying
        /// there is no credit field. If a capture ever turns up with something else in that word,
        /// the window stops being a constant and becomes a negotiated value - which is a design
        /// change, so it should announce itself by failing here.
        /// </remarks>
        [Fact]
        public void EveryCapturedAcknowledgementHasAZeroTrailingWord()
        {
            string folder = FindCaptureFolder();
            int acknowledgements = 0;

            for (int c = 0; c < CaptureNames.Length; c++)
            {
                string[] lines = File.ReadAllLines(Path.Combine(folder, CaptureNames[c]));
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split(
                        ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    byte[] frame;
                    try
                    {
                        frame = Convert.FromHexString(parts[2]);
                    }
                    catch (FormatException)
                    {
                        continue;
                    }

                    if (frame.Length < 28 ||
                        frame[14] != 0xA8 || frame[15] != 0xA8 || frame[16] != 0x03 ||
                        frame[19] != (byte)NdLinkFrameKind.Acknowledge)
                    {
                        continue;
                    }

                    acknowledgements++;
                    int trailing = (frame[26] << 8) | frame[27];
                    Assert.Equal(0, trailing);
                }
            }

            Assert.True(
                acknowledgements > 800,
                "expected 800+ acknowledgements across the captures, read " + acknowledgements);
        }
    }
}
