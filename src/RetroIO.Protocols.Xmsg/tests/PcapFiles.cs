//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Finds the recorded <c>.pcapng</c> files the wire-model tests read, and decides what happens
    /// when they are not on the machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Thirteen test files each had their own private copy of the same
    /// directory-walking code, and every one of them treated a missing file the same way: print
    /// "skipping" and PASS. These are the tests that check our decoder and our generator against
    /// bytes recorded off the real ND-100s, so on any machine without the files the whole set
    /// reported green while checking nothing at all.
    /// </para>
    /// <para>
    /// <b>The policy is now the opposite.</b> Missing files are an error. A developer who genuinely
    /// does not have them sets <c>XMSG_PCAP_OPTIONAL=1</c> and the tests go back to skipping - but
    /// that is a deliberate choice someone has to make, not the silent default.
    /// </para>
    /// <para>
    /// Two environment variables are read:
    /// </para>
    ///  - <c>XMSG_PCAP_DIR</c> names the folder directly, for a machine where it is somewhere else.
    ///  - <c>XMSG_PCAP_OPTIONAL=1</c> turns a missing folder or file back into a skip.
    /// <para>
    /// With neither set, the folder is found by walking up from the test binary looking for
    /// <c>X25Emulator\pcap</c>. That works from both this repository and the RetroFS one, because
    /// <c>NDInsight</c>, <c>RetroFS</c> and <c>X25Emulator</c> sit side by side.
    /// </para>
    /// </remarks>
    public static class PcapFiles
    {
        /// <summary>
        /// Name of the environment variable that names the folder holding the recorded files.
        /// </summary>
        public const string DirectoryVariable = "XMSG_PCAP_DIR";

        /// <summary>
        /// Name of the environment variable that turns a missing file back into a skipped test.
        /// </summary>
        public const string OptionalVariable = "XMSG_PCAP_OPTIONAL";

        /// <summary>
        /// The folder name searched for when walking up from the test binary.
        /// </summary>
        private const string WalkUpFolder = "X25Emulator";

        /// <summary>
        /// Gets a value indicating whether the caller has opted out of requiring the files.
        /// </summary>
        public static bool Optional
        {
            get
            {
                string? value = Environment.GetEnvironmentVariable(OptionalVariable);
                return !string.IsNullOrEmpty(value) && value != "0";
            }
        }

        /// <summary>
        /// Returns the folder holding the recorded files.
        /// </summary>
        /// <returns>
        /// The folder path, or <see langword="null"/> only when it is absent AND the caller has set
        /// <c>XMSG_PCAP_OPTIONAL</c>. A test may return early on <see langword="null"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the folder cannot be found and <c>XMSG_PCAP_OPTIONAL</c> is not set.
        /// </exception>
        public static string? Directory()
        {
            string? named = Environment.GetEnvironmentVariable(DirectoryVariable);
            CheckNamedFolder(named, !string.IsNullOrEmpty(named) && System.IO.Directory.Exists(named));

            return Apply(
                FindDirectory(),
                Optional,
                "the folder holding the recorded .pcapng files. Looked at " + DirectoryVariable
                + ", then walked up from " + AppContext.BaseDirectory + " for a "
                + WalkUpFolder + Path.DirectorySeparatorChar + "pcap folder");
        }

        /// <summary>
        /// Rejects a folder that was named explicitly and is not there.
        /// </summary>
        /// <param name="named">
        /// The folder named by the environment variable, or <see langword="null"/> when none was.
        /// </param>
        /// <param name="exists">
        /// Whether that folder was found.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a folder was named and does not exist.
        /// </exception>
        /// <remarks>
        /// <para>
        /// This is always an error, even when the opt-out is set: it means someone typed the path
        /// wrong, and quietly walking up to find a different folder would hide that.
        /// </para>
        /// <para>
        /// Pure, and separate from <see cref="Directory"/>, so it can be tested without setting the
        /// environment variable. An earlier version of the test DID set it, and because xUnit runs
        /// test classes in parallel, any other test reading the variable during that window failed.
        /// That produced exactly one intermittent failure before it was spotted.
        /// </para>
        /// </remarks>
        public static void CheckNamedFolder(string? named, bool exists)
        {
            if (string.IsNullOrEmpty(named) || exists) { return; }

            throw new InvalidOperationException(
                DirectoryVariable + " is set to '" + named + "', which does not exist. Correct it "
                + "or clear the variable to search for the folder instead.");
        }

        /// <summary>
        /// Applies the missing-file policy to a search result.
        /// </summary>
        /// <param name="found">
        /// What the search turned up, or <see langword="null"/> when it found nothing.
        /// </param>
        /// <param name="optional">
        /// Whether a missing item should be tolerated. Callers pass <see cref="Optional"/>.
        /// </param>
        /// <param name="whatWasSought">
        /// Description of what was being looked for, used in the error message.
        /// </param>
        /// <returns>
        /// <paramref name="found"/> when it is not null; otherwise <see langword="null"/> if
        /// <paramref name="optional"/> is set.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="found"/> is null and <paramref name="optional"/> is
        /// <see langword="false"/>.
        /// </exception>
        /// <remarks>
        /// This is the whole policy, and it is a pure function so it can be tested directly. That
        /// matters twice over. An earlier attempt to check the policy by pointing <c>XMSG_PCAP_DIR</c>
        /// at a nonexistent folder proved nothing, because the search fell back to walking up and found
        /// the real folder anyway. And reading the environment variable inside here would make the test
        /// change process-wide state that other tests running beside it also read.
        /// </remarks>
        public static string? Apply(string? found, bool optional, string whatWasSought)
        {
            if (found != null) { return found; }
            if (optional) { return null; }

            throw new InvalidOperationException(
                "Could not find " + whatWasSought + ". The tests that check our wire model against "
                + "the real machines cannot run without it. Set " + DirectoryVariable
                + " to the folder, or set " + OptionalVariable + "=1 to skip these tests "
                + "deliberately.");
        }

        /// <summary>
        /// Returns the full path of one recorded file.
        /// </summary>
        /// <param name="fileName">
        /// File name to look for, for example <c>claude-delete-file-102-to-100-2026-07-29.pcapng</c>.
        /// </param>
        /// <returns>
        /// The full path, or <see langword="null"/> only when it is absent AND the caller has set
        /// <c>XMSG_PCAP_OPTIONAL</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="fileName"/> is null or empty.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file cannot be found and <c>XMSG_PCAP_OPTIONAL</c> is not set.
        /// </exception>
        public static string? File(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException("A file name is required.", nameof(fileName));
            }

            string? directory = Directory();
            if (directory == null) { return null; }

            string candidate = Path.Combine(directory, fileName);
            return Apply(
                System.IO.File.Exists(candidate) ? candidate : null,
                Optional,
                "the recorded file '" + fileName + "' in " + directory
                + " (either it has been renamed, or that is not the folder this test expects)");
        }

        /// <summary>
        /// Locates the folder without applying the missing-file policy.
        /// </summary>
        /// <returns>
        /// The folder path, or <see langword="null"/> when it cannot be found.
        /// </returns>
        private static string? FindDirectory()
        {
            string? named = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrEmpty(named) && System.IO.Directory.Exists(named))
            {
                return named;
            }

            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, WalkUpFolder, "pcap");
                if (System.IO.Directory.Exists(candidate)) { return candidate; }
                dir = dir.Parent;
            }

            return null;
        }
    }
}
