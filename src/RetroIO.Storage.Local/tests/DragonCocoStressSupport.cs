//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Runs the Toolshed programs <c>decb</c> and <c>os9</c> in WSL Ubuntu for the stress tests.
    /// </summary>
    /// <remarks>
    /// Toolshed is a separate program written by other people, so what it says about an image is an independent check of what our code wrote.
    /// </remarks>
    internal static class StressToolshed
    {
        /// <summary>
        /// Runs one Toolshed program and gives everything it printed.
        /// </summary>
        /// <param name="program">
        /// <c>decb</c> or <c>os9</c>.
        /// </param>
        /// <param name="arguments">
        /// The arguments, one per element.
        /// </param>
        /// <returns>
        /// Standard output followed by standard error, or null when WSL or the program is not on this machine.
        /// </returns>
        public static string? Run(string program, params string[] arguments)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }

            ProcessStartInfo info = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add("Ubuntu");
            info.ArgumentList.Add("--exec");
            info.ArgumentList.Add(program);
            for (int i = 0; i < arguments.Length; i++)
            {
                info.ArgumentList.Add(arguments[i]);
            }

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }

            if (process == null)
            {
                return null;
            }

            using (process)
            {
                System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(120000))
                {
                    // Our own child process: killed by its handle, not by name.
                    process.Kill(true);
                    throw new TimeoutException(program + " did not finish in 120 seconds");
                }

                string all = output + error.Result;

                // wsl.exe prints its OWN messages (such as a missing distribution) in UTF-16, while the program inside prints UTF-8. Read as UTF-8,
                // UTF-16 text has a NUL after every character, so "There is no distribution" never matched and the CI runner (wsl.exe present, no
                // Ubuntu) failed five tests instead of skipping the Toolshed check. The NULs are dropped for the test only; the text returned is unchanged.
                string plain = all.Replace("\0", string.Empty);
                if (plain.Contains("execvpe(" + program + ") failed") || plain.Contains("Command '" + program + "' not found")
                    || plain.Contains("There is no distribution with the supplied name"))
                {
                    return null;
                }

                return all;
            }
        }

        /// <summary>
        /// Turns a Windows path into the path WSL sees, for example <c>/mnt/c/...</c>.
        /// </summary>
        /// <param name="windowsPath">
        /// A full or relative Windows path.
        /// </param>
        /// <returns>
        /// The WSL path.
        /// </returns>
        public static string WslPath(string windowsPath)
        {
            string full = Path.GetFullPath(windowsPath);
            return "/mnt/" + char.ToLowerInvariant(full[0]) + full.Substring(2).Replace('\\', '/');
        }

        /// <summary>
        /// Finds the number that comes right before a piece of text, for example 470 in "470 Free sectors".
        /// </summary>
        /// <remarks>
        /// Places where the text has no number in front (for example "root directory" when looking for "2 directories") are passed over.
        /// </remarks>
        /// <param name="text">
        /// The text Toolshed printed.
        /// </param>
        /// <param name="after">
        /// The text that follows the number.
        /// </param>
        /// <returns>
        /// The first number found, or -1 when there is none.
        /// </returns>
        public static long NumberBefore(string text, string after)
        {
            int at = text.IndexOf(after, StringComparison.Ordinal);
            while (at > 0)
            {
                int end = at;
                while (end > 0 && text[end - 1] == ' ')
                {
                    end--;
                }

                int start = end;
                while (start > 0 && char.IsDigit(text[start - 1]))
                {
                    start--;
                }

                if (start != end)
                {
                    return long.Parse(text.Substring(start, end - start));
                }

                at = text.IndexOf(after, at + 1, StringComparison.Ordinal);
            }

            return -1;
        }

        /// <summary>
        /// Finds the number that comes right after a piece of text, for example 62 in "Free granules: 62 (...)".
        /// </summary>
        /// <param name="text">
        /// The text Toolshed printed.
        /// </param>
        /// <param name="before">
        /// The text in front of the number.
        /// </param>
        /// <returns>
        /// The number, or -1 when the text is not there.
        /// </returns>
        public static long NumberAfter(string text, string before)
        {
            int at = text.IndexOf(before, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            int start = at + before.Length;
            while (start < text.Length && text[start] == ' ')
            {
                start++;
            }

            int end = start;
            while (end < text.Length && char.IsDigit(text[end]))
            {
                end++;
            }

            return start == end ? -1 : long.Parse(text.Substring(start, end - start));
        }
    }

    /// <summary>
    /// Records how long each file system call of a stress run took, by kind of call, by how full the disk was and by round, and checks that
    /// nothing gets slower as the run goes on.
    /// </summary>
    /// <remarks>
    /// All storage is taken up front, so recording a sample allocates nothing and does not show up in the memory measurement of the same run.
    /// </remarks>
    internal sealed class StressTimings
    {
        /// <summary>
        /// Number of fill levels: below 25, 50, 75 and 100 percent of the space that files can use.
        /// </summary>
        public const int FillBuckets = 4;

        /// <summary>
        /// Fill levels the fixed probe runs at, in percent of the space files can use: empty, half full and 70 percent full (the probe itself
        /// needs room on top of that).
        /// </summary>
        public static readonly int[] ProbePercent = { 0, 50, 70 };

        private readonly string[] _kindNames;
        private readonly long[] _ticks;
        private readonly byte[] _kind;
        private readonly byte[] _bucket;
        private readonly short[] _round;
        private readonly long[] _probe;
        private readonly long[] _probeIo;
        private readonly int _rounds;
        private int _count;

        /// <summary>
        /// Creates a recorder with room for a fixed number of samples.
        /// </summary>
        /// <param name="kindNames">
        /// The names of the kinds of call, indexed by the kind number passed to <see cref="Add"/>.
        /// </param>
        /// <param name="capacity">
        /// The most samples that will be recorded. More are dropped.
        /// </param>
        /// <param name="rounds">
        /// The number of rounds, for the probe times.
        /// </param>
        public StressTimings(string[] kindNames, int capacity, int rounds)
        {
            _kindNames = kindNames;
            _ticks = new long[capacity];
            _kind = new byte[capacity];
            _bucket = new byte[capacity];
            _round = new short[capacity];
            _rounds = rounds;
            _probe = new long[rounds * ProbePercent.Length];
            _probeIo = new long[rounds * ProbePercent.Length];
            for (int i = 0; i < _probe.Length; i++)
            {
                _probe[i] = long.MaxValue;
                _probeIo[i] = long.MaxValue;
            }
        }

        /// <summary>
        /// Records one pass of the fixed probe. Only the fastest pass of a round and fill level is kept, because a pass can only be made
        /// slower by things outside the code (another process, the garbage collector, the disk cache), never faster.
        /// </summary>
        /// <param name="round">
        /// The round, from 0.
        /// </param>
        /// <param name="level">
        /// Index into <see cref="ProbePercent"/>.
        /// </param>
        /// <param name="ticks">
        /// Stopwatch ticks spent inside the file system during the pass.
        /// </param>
        /// <param name="blocks">
        /// Blocks the file system read and wrote during the pass.
        /// </param>
        public void AddProbe(int round, int level, long ticks, long blocks)
        {
            int at = (round * ProbePercent.Length) + level;
            if (ticks < _probe[at])
            {
                _probe[at] = ticks;
            }

            if (blocks < _probeIo[at])
            {
                _probeIo[at] = blocks;
            }
        }

        private double ProbeMicroseconds(int round, int level)
        {
            long ticks = _probe[(round * ProbePercent.Length) + level];
            return ticks == long.MaxValue ? -1 : ticks * 1000000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// Compares the probe of every round after the first two with the probe of round 1 (round 0 is the warm-up), at each fill level.
        /// </summary>
        /// <param name="timeFactor">
        /// How many times slower a later probe may be.
        /// </param>
        /// <param name="blockFactor">
        /// How many times more blocks a later probe may read and write.
        /// </param>
        /// <returns>
        /// The probes that were too slow or did too much, one per line, or an empty string.
        /// </returns>
        public string ProbeGrowth(double timeFactor, double blockFactor)
        {
            StringBuilder problems = new StringBuilder();
            if (_rounds < 3)
            {
                return string.Empty;
            }

            for (int level = 0; level < ProbePercent.Length; level++)
            {
                double baseline = ProbeMicroseconds(1, level);
                long baseBlocks = _probeIo[ProbePercent.Length + level];
                for (int round = 2; round < _rounds; round++)
                {
                    double now = ProbeMicroseconds(round, level);
                    if (baseline > 0 && now > baseline * timeFactor)
                    {
                        problems.Append("probe at ").Append(ProbePercent[level]).Append(" % full: ").Append(baseline.ToString("F0"))
                            .Append(" us in round 1, ").Append(now.ToString("F0")).Append(" us in round ").Append(round)
                            .Append(", ratio ").Append((now / baseline).ToString("F2")).AppendLine();
                    }

                    long blocks = _probeIo[(round * ProbePercent.Length) + level];
                    if (blocks > baseBlocks * blockFactor)
                    {
                        problems.Append("probe at ").Append(ProbePercent[level]).Append(" % full: ").Append(baseBlocks)
                            .Append(" blocks read and written in round 1, ").Append(blocks).Append(" in round ").Append(round).AppendLine();
                    }
                }
            }

            return problems.ToString();
        }

        /// <summary>
        /// Gets the number of samples recorded.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Gives the fill bucket for a disk with this much used out of this much usable.
        /// </summary>
        /// <param name="used">
        /// Space used by files and directories.
        /// </param>
        /// <param name="usable">
        /// Space a file system can give to files and directories on the empty disk.
        /// </param>
        /// <returns>
        /// 0 to <see cref="FillBuckets"/> minus 1.
        /// </returns>
        public static int BucketOf(long used, long usable)
        {
            if (usable <= 0)
            {
                return 0;
            }

            int bucket = (int)(used * FillBuckets / usable);
            return bucket < 0 ? 0 : (bucket >= FillBuckets ? FillBuckets - 1 : bucket);
        }

        /// <summary>
        /// Records one call.
        /// </summary>
        /// <param name="kind">
        /// The kind of call.
        /// </param>
        /// <param name="bucket">
        /// The fill bucket before the call.
        /// </param>
        /// <param name="round">
        /// The round the call belongs to, from 0.
        /// </param>
        /// <param name="ticks">
        /// Stopwatch ticks the call took.
        /// </param>
        public void Add(int kind, int bucket, int round, long ticks)
        {
            if (_count >= _ticks.Length)
            {
                return;
            }

            _ticks[_count] = ticks;
            _kind[_count] = (byte)kind;
            _bucket[_count] = (byte)bucket;
            _round[_count] = (short)round;
            _count++;
        }

        // Median in microseconds of the samples of one kind and bucket in the rounds first to last (inclusive). -1 when fewer than minSamples.
        private double Median(int kind, int bucket, int first, int last, int minSamples, out int samples)
        {
            samples = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_kind[i] == kind && (bucket < 0 || _bucket[i] == bucket) && _round[i] >= first && _round[i] <= last)
                {
                    samples++;
                }
            }

            if (samples < minSamples || samples == 0)
            {
                return -1;
            }

            long[] picked = new long[samples];
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_kind[i] == kind && (bucket < 0 || _bucket[i] == bucket) && _round[i] >= first && _round[i] <= last)
                {
                    picked[n++] = _ticks[i];
                }
            }

            Array.Sort(picked);
            long middle = (samples % 2) == 1 ? picked[samples / 2] : (picked[(samples / 2) - 1] + picked[samples / 2]) / 2;
            return middle * 1000000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// Gives the number of calls per second over the given rounds, counting only the time spent inside the file system.
        /// </summary>
        /// <param name="first">
        /// First round, inclusive.
        /// </param>
        /// <param name="last">
        /// Last round, inclusive.
        /// </param>
        /// <returns>
        /// Calls per second, or 0 when there were none.
        /// </returns>
        public double CallsPerSecond(int first, int last)
        {
            long ticks = 0;
            int calls = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_round[i] >= first && _round[i] <= last)
                {
                    ticks += _ticks[i];
                    calls++;
                }
            }

            return ticks == 0 ? 0 : calls * (double)Stopwatch.Frequency / ticks;
        }

        /// <summary>
        /// Writes a table of median times per kind of call and fill bucket, for the early and the late rounds.
        /// </summary>
        /// <param name="report">
        /// Where the table goes.
        /// </param>
        /// <param name="rounds">
        /// The number of rounds in the run.
        /// </param>
        public void Describe(StringBuilder report, int rounds)
        {
            SplitRounds(rounds, out int earlyFirst, out int earlyLast, out int lateFirst, out int lateLast);
            report.Append("  calls per second inside the file system: all rounds ").Append(CallsPerSecond(0, rounds - 1).ToString("F0"))
                .Append(", early rounds ").Append(CallsPerSecond(earlyFirst, earlyLast).ToString("F0"))
                .Append(", late rounds ").Append(CallsPerSecond(lateFirst, lateLast).ToString("F0")).AppendLine();
            report.AppendLine("  median microseconds per call, early rounds / late rounds (n = samples), by fill level 0-25-50-75-100 %:");
            for (int k = 0; k < _kindNames.Length; k++)
            {
                StringBuilder line = new StringBuilder();
                line.Append("    ").Append(_kindNames[k].PadRight(10));
                bool any = false;
                for (int b = 0; b < FillBuckets; b++)
                {
                    double early = Median(k, b, earlyFirst, earlyLast, 1, out int earlyN);
                    double late = Median(k, b, lateFirst, lateLast, 1, out int lateN);
                    line.Append(" | ");
                    if (earlyN == 0 && lateN == 0)
                    {
                        line.Append("-".PadRight(26));
                        continue;
                    }

                    any = true;
                    string cell = (earlyN > 0 ? early.ToString("F1") : "-") + "/" + (lateN > 0 ? late.ToString("F1") : "-")
                        + " (n=" + earlyN + "/" + lateN + ")";
                    line.Append(cell.PadRight(26));
                }

                if (any)
                {
                    report.Append(line.ToString()).AppendLine();
                }
            }
        }

        // Round 0 is the warm-up (the JIT compiles the code and the caches fill), so it is left out. The rest is split in two halves.
        private static void SplitRounds(int rounds, out int earlyFirst, out int earlyLast, out int lateFirst, out int lateLast)
        {
            earlyFirst = rounds > 2 ? 1 : 0;
            int middle = earlyFirst + ((rounds - earlyFirst) / 2);
            earlyLast = middle - 1;
            lateFirst = middle;
            lateLast = rounds - 1;
        }

        /// <summary>
        /// Writes the probe times: the fastest pass of each round at each fill level.
        /// </summary>
        /// <param name="report">
        /// Where the table goes.
        /// </param>
        public void DescribeProbes(StringBuilder report)
        {
            report.AppendLine("  fixed probe (same calls, same sizes), fastest of the passes, microseconds per round:");
            for (int level = 0; level < ProbePercent.Length; level++)
            {
                report.Append("    ").Append(ProbePercent[level].ToString().PadLeft(2)).Append(" % full:");
                for (int round = 0; round < _rounds; round++)
                {
                    report.Append(' ').Append(ProbeMicroseconds(round, level).ToString("F0"));
                }

                report.AppendLine();
            }

            report.AppendLine("  fixed probe, blocks read and written by the file system per pass, per round:");
            for (int level = 0; level < ProbePercent.Length; level++)
            {
                report.Append("    ").Append(ProbePercent[level].ToString().PadLeft(2)).Append(" % full:");
                for (int round = 0; round < _rounds; round++)
                {
                    report.Append(' ').Append(_probeIo[(round * ProbePercent.Length) + level]);
                }

                report.AppendLine();
            }
        }
    }

    /// <summary>
    /// The checks over a whole stress run: memory that is not given back, and calls that get slower as the run goes on.
    /// </summary>
    internal static class StressChecks
    {
        /// <summary>
        /// A rise of the retained memory from one round to the next that is larger than this counts as growth. Measured 2026-10-21 (Debug
        /// build, .NET 10, Windows 11): with the model empty, rounds differ by 0 to 8 kB on all six disks. A round makes 300 or more calls, so a
        /// leak of one small object per call (a 256-byte sector buffer is 280 bytes on the heap) is 84 kB a round and well above this.
        /// </summary>
        public const long RoundGrowthBytes = 16 * 1024;

        /// <summary>
        /// Most rounds that may show growth. Measured: one DECB run held 303 kB more from one round on and then stayed flat for the next ten
        /// rounds (to within 56 bytes); that is a one-time step outside the file system (the runtime keeping something once), not a leak,
        /// which grows in every round. So one step is allowed and a second one fails.
        /// </summary>
        public const int AllowedGrowthRounds = 1;

        /// <summary>
        /// Most the retained memory may rise over the whole run, even in one step. Four DECB disk images (161 kB each) is more than any
        /// one-time step seen, and a leaked copy of the image every round passes it within the five rounds of the default run.
        /// </summary>
        public const long TotalGrowthBytes = 640 * 1024;

        /// <summary>
        /// How many times slower a later probe may be than the probe of round 1. Measured 2026-10-21: the fastest of three passes varied up
        /// to 2.1 times from round to round (every call also writes the whole image file, and the disk cache and virus scanner are not under
        /// the test's control), so a smaller factor would fail on a busy machine. The block count below is the exact check; this one only
        /// catches a slowdown that does not show in the blocks (time spent in the code itself).
        /// </summary>
        public const double SlowdownFactor = 5.0;

        /// <summary>
        /// How many times more blocks a later probe may read and write than the probe of round 1. The count does not depend on the clock, so
        /// it is the same on every machine. It may still rise a little because directories keep the slots they ever had (DECB keeps deleted
        /// entries up to the end marker, OS-9 never shrinks a directory), and a lookup reads the directory up to that point. Measured
        /// 2026-10-21: rounds 1 to 4 differ by at most 3.5 percent (DECB 339 to 351 blocks; every OS-9 disk the same count in every round).
        /// Work that grows with the number of calls made passes 25 percent within a few rounds.
        /// </summary>
        public const double BlockFactor = 1.25;

        /// <summary>
        /// Fails when the retained memory rose by more than <see cref="RoundGrowthBytes"/> in more than <see cref="AllowedGrowthRounds"/> rounds,
        /// or by more than <see cref="TotalGrowthBytes"/> over the run.
        /// </summary>
        /// <param name="memory">
        /// Retained bytes after each round, each taken with the model empty.
        /// </param>
        /// <param name="report">
        /// The run report, added to the failure message.
        /// </param>
        public static void RequireNoLeak(long[] memory, string report)
        {
            int growthRounds = 0;
            for (int i = 1; i < memory.Length; i++)
            {
                if (memory[i] - memory[i - 1] > RoundGrowthBytes)
                {
                    growthRounds++;
                }
            }

            NUnit.Framework.Assert.That(growthRounds, NUnit.Framework.Is.LessThanOrEqualTo(AllowedGrowthRounds),
                "the memory held after a round rose by more than " + RoundGrowthBytes + " bytes in " + growthRounds + " rounds\n" + report);
            long growth = memory[memory.Length - 1] - memory[0];
            NUnit.Framework.Assert.That(growth, NUnit.Framework.Is.LessThanOrEqualTo(TotalGrowthBytes),
                "memory held after the last round is " + growth + " bytes more than after the first round\n" + report);
        }

        /// <summary>
        /// Fails when the fixed probe of any later round, at any fill level, took more than <see cref="SlowdownFactor"/> times as long as in
        /// round 1, or read and wrote more than <see cref="BlockFactor"/> times as many blocks.
        /// </summary>
        /// <param name="timings">
        /// The timings of the run.
        /// </param>
        /// <param name="report">
        /// The run report, added to the failure message.
        /// </param>
        public static void RequireNoSlowdown(StressTimings timings, string report)
        {
            string growth = timings.ProbeGrowth(SlowdownFactor, BlockFactor);
            NUnit.Framework.Assert.That(growth, NUnit.Framework.Is.Empty, "the same calls got slower as the run went on:\n" + growth + report);
        }
    }

    /// <summary>
    /// A block device that passes every call to another one and counts the blocks read and written and the flushes.
    /// </summary>
    /// <remarks>
    /// The file system under test is given this device, so the counts are exactly the work it asked for. The test's own reads go to the inner
    /// device and are not counted.
    /// </remarks>
    internal sealed class CountingBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _inner;

        /// <summary>
        /// Creates a counting device in front of another device.
        /// </summary>
        /// <param name="inner">
        /// The device that does the work.
        /// </param>
        public CountingBlockDevice(IBlockDevice inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Gets or sets whether counting is paused, for the test's own checks that go through the file system.
        /// </summary>
        public bool Paused { get; set; }

        /// <summary>
        /// Gets the number of blocks read so far.
        /// </summary>
        public long Reads { get; private set; }

        /// <summary>
        /// Gets the number of blocks written so far.
        /// </summary>
        public long Writes { get; private set; }

        /// <summary>
        /// Gets the number of flushes so far.
        /// </summary>
        public long Flushes { get; private set; }

        /// <summary>
        /// Gets the block size of the inner device.
        /// </summary>
        public int BlockSize => _inner.BlockSize;

        /// <summary>
        /// Gets the number of blocks of the inner device.
        /// </summary>
        public uint TotalBlocks => _inner.TotalBlocks;

        /// <summary>
        /// Gets whether the inner device is read-only.
        /// </summary>
        public bool IsReadOnly => _inner.IsReadOnly;

        /// <summary>
        /// Gets whether the inner device is mounted.
        /// </summary>
        public bool IsMounted => _inner.IsMounted;

        /// <summary>
        /// Gets whether the inner device has variable geometry.
        /// </summary>
        public bool SupportsVariableGeometry => _inner.SupportsVariableGeometry;

        /// <summary>
        /// Mounts the inner device.
        /// </summary>
        /// <param name="imageStream">
        /// The image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount read-only.
        /// </param>
        public void Mount(Stream imageStream, bool readOnly)
        {
            _inner.Mount(imageStream, readOnly);
        }

        /// <summary>
        /// Reads a block from the inner device and counts it.
        /// </summary>
        /// <param name="address">
        /// The block.
        /// </param>
        /// <param name="buffer">
        /// Receives the block.
        /// </param>
        /// <returns>
        /// What the inner device returns.
        /// </returns>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            if (!Paused)
            {
                Reads++;
            }

            return _inner.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Writes a block to the inner device and counts it.
        /// </summary>
        /// <param name="address">
        /// The block.
        /// </param>
        /// <param name="data">
        /// The block data.
        /// </param>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            if (!Paused)
            {
                Writes++;
            }

            _inner.WriteBlock(address, data);
        }

        /// <summary>
        /// Gets the geometry of the inner device.
        /// </summary>
        /// <returns>
        /// What the inner device returns.
        /// </returns>
        public BlockDeviceInfo GetBlockDeviceInfo()
        {
            return _inner.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Gets the blocks per track of the inner device.
        /// </summary>
        /// <param name="track">
        /// The track.
        /// </param>
        /// <returns>
        /// What the inner device returns.
        /// </returns>
        public int GetBlocksPerTrack(int track)
        {
            return _inner.GetBlocksPerTrack(track);
        }

        /// <summary>
        /// Flushes the inner device and counts it.
        /// </summary>
        public void Flush()
        {
            if (!Paused)
            {
                Flushes++;
            }

            _inner.Flush();
        }

        /// <summary>
        /// Does nothing: the inner device belongs to the test, which disposes it.
        /// </summary>
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Memory and file handle checks shared by the stress tests.
    /// </summary>
    internal static class StressMemory
    {
        /// <summary>
        /// Collects all garbage, runs the finalizers, collects again, and gives the bytes still held by live objects.
        /// </summary>
        /// <returns>
        /// The result of <see cref="GC.GetTotalMemory(bool)"/> after a full collection.
        /// </returns>
        public static long Retained()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            return GC.GetTotalMemory(true);
        }

        /// <summary>
        /// Tells whether a file can be opened with no sharing at all, which fails while any handle to it is still open.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <param name="reason">
        /// The error when it cannot be opened, otherwise an empty string.
        /// </param>
        /// <returns>
        /// True when the file could be opened alone.
        /// </returns>
        public static bool CanOpenExclusively(string path, out string reason)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    reason = string.Empty;
                    return true;
                }
            }
            catch (IOException ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads a whole file that another stream may hold open for writing.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <returns>
        /// The bytes of the file.
        /// </returns>
        public static byte[] ReadShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] all = new byte[stream.Length];
                stream.ReadExactly(all, 0, all.Length);
                return all;
            }
        }
    }
}
