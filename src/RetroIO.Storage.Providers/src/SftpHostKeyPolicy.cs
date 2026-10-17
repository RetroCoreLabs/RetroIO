//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Storage.Providers
{
    /// <summary>
    /// How <see cref="SftpHostKeyPolicy"/> treats a server whose host key it has not seen before.
    /// </summary>
    public enum SftpHostKeyMode
    {
        /// <summary>
        /// Refuse a host key that is not pinned and not in the known-hosts file. This is the default.
        /// </summary>
        Strict,

        /// <summary>
        /// Accept a host key the first time a host is seen and remember it. A key that later differs is refused.
        /// </summary>
        AcceptNew,

        /// <summary>
        /// Trust every host key. Anyone who can intercept the connection can pose as the server.
        /// </summary>
        Off
    }

    /// <summary>
    /// The result of checking one host key: whether to trust it, and why.
    /// </summary>
    public sealed class SftpHostKeyDecision
    {
        /// <summary>
        /// Creates a decision.
        /// </summary>
        /// <param name="trust">
        /// True to trust the host key.
        /// </param>
        /// <param name="reason">
        /// A sentence that says why, fit to show to the person who has to fix a refusal.
        /// </param>
        public SftpHostKeyDecision(bool trust, string reason)
        {
            Trust = trust;
            Reason = reason;
        }

        /// <summary>
        /// Gets a value indicating whether the host key is trusted.
        /// </summary>
        public bool Trust { get; }

        /// <summary>
        /// Gets the reason for the decision.
        /// </summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Decides whether to trust the host key an SFTP server presents.
    /// </summary>
    /// <remarks>
    /// <para><b>The rules, in order</b></para>
    /// Applied to a host and port and the server's SHA-256 key fingerprint:
    ///  - <see cref="SftpHostKeyMode.Off"/> trusts everything.
    ///  - When one or more fingerprints are pinned, the key is trusted only if it equals one of them.
    ///  - Otherwise the known-hosts file is consulted. A host listed there is trusted only with the listed key. A listed host with a different key is always refused, in every mode, and the file is never changed.
    ///  - A host that is not listed is refused in <see cref="SftpHostKeyMode.Strict"/> mode, and in <see cref="SftpHostKeyMode.AcceptNew"/> mode it is trusted and added to the file.
    /// <para><b>Query options of an sftp URI</b></para>
    ///  - <c>hostKey</c>: one or more fingerprints, separated by commas, in the form that ssh prints, for example <c>SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og</c>. The <c>SHA256:</c> prefix and trailing equals signs are optional.
    ///  - <c>hostKeyCheck</c>: <c>strict</c> (the default), <c>accept-new</c> or <c>off</c>.
    ///  - <c>knownHosts</c>: the path of the known-hosts file. The default is <c>.retroio/sftp-known-hosts</c> in the user profile folder.
    /// <para><b>Known-hosts file</b></para>
    /// One entry per line as <c>host:port SHA256:fingerprint</c>. Blank lines and lines that start with a hash sign are ignored. Host names are compared without regard to case.
    /// </remarks>
    public sealed class SftpHostKeyPolicy
    {
        private const string Sha256Prefix = "SHA256:";
        private const string DefaultFolderName = ".retroio";
        private const string DefaultFileName = "sftp-known-hosts";

        private static readonly object FileLock = new object();

        private readonly List<string> _pinned;

        /// <summary>
        /// Creates a policy.
        /// </summary>
        /// <param name="mode">
        /// How to treat a host that is neither pinned nor listed.
        /// </param>
        /// <param name="pinnedFingerprints">
        /// Fingerprints to require, or null or empty for none.
        /// </param>
        /// <param name="knownHostsPath">
        /// The known-hosts file, or null for the default file in the user profile folder.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="mode"/> is <see cref="SftpHostKeyMode.Off"/> and fingerprints are also pinned, because the two contradict each other.
        /// </exception>
        public SftpHostKeyPolicy(SftpHostKeyMode mode, IReadOnlyList<string>? pinnedFingerprints, string? knownHostsPath)
        {
            Mode = mode;
            _pinned = new List<string>();
            if (pinnedFingerprints != null)
            {
                for (int i = 0; i < pinnedFingerprints.Count; i++)
                    _pinned.Add(NormalizeFingerprint(pinnedFingerprints[i]));
            }

            if (mode == SftpHostKeyMode.Off && _pinned.Count > 0)
                throw new ArgumentException("hostKey cannot be combined with hostKeyCheck=off: a pinned key would be ignored.", nameof(pinnedFingerprints));

            KnownHostsPath = string.IsNullOrWhiteSpace(knownHostsPath) ? DefaultKnownHostsPath() : knownHostsPath;
        }

        /// <summary>
        /// Gets how a host that is neither pinned nor listed is treated.
        /// </summary>
        public SftpHostKeyMode Mode { get; }

        /// <summary>
        /// Gets the path of the known-hosts file.
        /// </summary>
        public string KnownHostsPath { get; }

        /// <summary>
        /// Reads the host key options from the query part of an sftp URI.
        /// </summary>
        /// <param name="query">
        /// The query string, with or without the leading question mark. May be empty.
        /// </param>
        /// <returns>
        /// The policy the options describe. With no options it is strict, with no pinned keys and the default known-hosts file.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <c>hostKeyCheck</c> has a value other than <c>strict</c>, <c>accept-new</c> or <c>off</c>, or when options contradict each other.
        /// </exception>
        public static SftpHostKeyPolicy FromQuery(string query)
        {
            SftpHostKeyMode mode = SftpHostKeyMode.Strict;
            string? check = QueryValue(query, "hostKeyCheck");
            if (!string.IsNullOrEmpty(check))
            {
                if (string.Equals(check, "strict", StringComparison.OrdinalIgnoreCase))
                    mode = SftpHostKeyMode.Strict;
                else if (string.Equals(check, "accept-new", StringComparison.OrdinalIgnoreCase))
                    mode = SftpHostKeyMode.AcceptNew;
                else if (string.Equals(check, "off", StringComparison.OrdinalIgnoreCase))
                    mode = SftpHostKeyMode.Off;
                else
                    throw new ArgumentException("hostKeyCheck must be strict, accept-new or off, not '" + check + "'.");
            }

            List<string> pins = new List<string>();
            string? pinned = QueryValue(query, "hostKey");
            if (!string.IsNullOrEmpty(pinned))
            {
                string[] parts = pinned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i < parts.Length; i++)
                    pins.Add(parts[i]);
            }

            return new SftpHostKeyPolicy(mode, pins, QueryValue(query, "knownHosts"));
        }

        /// <summary>
        /// Decides whether to trust the key a server presented.
        /// </summary>
        /// <param name="host">
        /// The host name the connection was made to.
        /// </param>
        /// <param name="port">
        /// The TCP port.
        /// </param>
        /// <param name="fingerprintSha256">
        /// The SHA-256 fingerprint of the server's host key, in the form ssh prints, with or without the <c>SHA256:</c> prefix.
        /// </param>
        /// <returns>
        /// The decision, with the reason.
        /// </returns>
        public SftpHostKeyDecision Evaluate(string host, int port, string fingerprintSha256)
        {
            string seen = NormalizeFingerprint(fingerprintSha256);

            if (Mode == SftpHostKeyMode.Off)
                return new SftpHostKeyDecision(true, "Host key checking is switched off.");

            if (_pinned.Count > 0)
            {
                for (int i = 0; i < _pinned.Count; i++)
                {
                    if (string.Equals(_pinned[i], seen, StringComparison.Ordinal))
                        return new SftpHostKeyDecision(true, "The host key matches a pinned fingerprint.");
                }

                return new SftpHostKeyDecision(false,
                    "The host key of " + host + ":" + port + " (" + Sha256Prefix + seen + ") does not match any fingerprint given in hostKey. " +
                    "Either this is not the server you expect, or the server's key has changed.");
            }

            string entryName = EntryName(host, port);
            lock (FileLock)
            {
                string? known = FindKnown(entryName);
                if (known != null)
                {
                    if (string.Equals(known, seen, StringComparison.Ordinal))
                        return new SftpHostKeyDecision(true, "The host key matches the entry for " + entryName + " in " + KnownHostsPath + ".");

                    return new SftpHostKeyDecision(false,
                        "THE HOST KEY OF " + entryName + " HAS CHANGED. " + KnownHostsPath + " has " + Sha256Prefix + known +
                        " but the server presented " + Sha256Prefix + seen + ". This can mean someone is intercepting the connection. " +
                        "If the change is expected, remove the entry for " + entryName + " from that file and connect again.");
                }

                if (Mode == SftpHostKeyMode.AcceptNew)
                {
                    Append(entryName, seen);
                    return new SftpHostKeyDecision(true, "First connection to " + entryName + ": the host key " + Sha256Prefix + seen + " was added to " + KnownHostsPath + ".");
                }
            }

            return new SftpHostKeyDecision(false,
                "The host key of " + entryName + " (" + Sha256Prefix + seen + ") is not known. Check that this is the server's key, then " +
                "add hostKey=" + Sha256Prefix + seen + " to the URI, or add the line '" + entryName + " " + Sha256Prefix + seen + "' to " +
                KnownHostsPath + ", or use hostKeyCheck=accept-new to remember it on the first connection.");
        }

        // Strips the optional SHA256: prefix and the base64 padding, so the pinned, listed and presented forms compare equal.
        private static string NormalizeFingerprint(string fingerprint)
        {
            if (fingerprint == null)
                throw new ArgumentNullException(nameof(fingerprint));

            string value = fingerprint.Trim();
            if (value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(Sha256Prefix.Length);

            value = value.TrimEnd('=');
            if (value.Length == 0)
                throw new ArgumentException("A host key fingerprint cannot be empty.", nameof(fingerprint));

            return value;
        }

        private static string EntryName(string host, int port)
        {
            return host.ToLowerInvariant() + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string DefaultKnownHostsPath()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, DefaultFolderName, DefaultFileName);
        }

        // Returns the listed fingerprint for the entry name, or null when the host is not listed.
        private string? FindKnown(string entryName)
        {
            if (!File.Exists(KnownHostsPath))
                return null;

            string[] lines = File.ReadAllLines(KnownHostsPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                int space = line.IndexOf(' ');
                if (space <= 0)
                    continue;

                string name = line.Substring(0, space);
                if (string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
                    return NormalizeFingerprint(line.Substring(space + 1));
            }

            return null;
        }

        private void Append(string entryName, string fingerprint)
        {
            string? folder = Path.GetDirectoryName(KnownHostsPath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.AppendAllText(KnownHostsPath, entryName + " " + Sha256Prefix + fingerprint + Environment.NewLine);
        }

        // Reads one value from a query string such as "?a=1&b=2", or null when the key is absent.
        private static string? QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            string trimmed = query[0] == '?' ? query.Substring(1) : query;
            string[] pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq <= 0)
                    continue;

                if (string.Equals(pairs[i].Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
            }

            return null;
        }
    }
}
