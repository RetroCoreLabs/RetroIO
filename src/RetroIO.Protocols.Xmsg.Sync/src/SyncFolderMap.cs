//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// The set of folder-to-user mappings, and the rule that turns a local file path into the
    /// remote filespec it belongs at.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the quoting is done HERE and not by the caller</b></para>
    /// <para>
    /// SINTRAN's quoting rule is narrow and easy to get wrong - it was corrected four times in one
    /// afternoon. Quotes go around the FILE NAME only, never around the machine or user, and ONLY
    /// when the file is being created:
    /// </para>
    /// <code>
    /// creating   D102(SYSTEM)."BLKT7777:DATA"
    /// existing   D102(SYSTEM).BLKT7777:DATA
    /// </code>
    /// <para>
    /// Note that <c>LIST-FILES</c> DISPLAYS names as <c>D102.(SYSTEM)NAME</c>, which is not input
    /// syntax and must not be copied back in. Putting the rule in one method means a caller cannot
    /// get it wrong, and a correction lands once.
    /// </para>
    /// </remarks>
    public sealed class SyncFolderMap
    {
        /// <summary>
        /// The mappings, in the order they were added.
        /// </summary>
        /// <remarks>
        /// Order matters: the LONGEST matching local folder wins, so a mapping for a sub-folder
        /// can override the one for its parent. That is resolved in <see cref="TryResolve"/>
        /// rather than by sorting, so the caller's order is preserved for reporting.
        /// </remarks>
        private readonly List<SyncFolderMapping> _mappings = new List<SyncFolderMapping>();

        /// <summary>
        /// Gets how many mappings are configured.
        /// </summary>
        public int Count
        {
            get { return _mappings.Count; }
        }

        /// <summary>
        /// Adds a mapping.
        /// </summary>
        /// <param name="mapping">
        /// The folder-to-user mapping.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="mapping"/> is null.
        /// </exception>
        public void Add(SyncFolderMapping mapping)
        {
            if (mapping == null)
            {
                throw new ArgumentNullException(nameof(mapping));
            }

            _mappings.Add(mapping);
        }

        /// <summary>
        /// Finds the mapping that owns a local file.
        /// </summary>
        /// <param name="localPath">
        /// The full path of the local file.
        /// </param>
        /// <returns>
        /// The mapping whose folder is the LONGEST prefix of the path, or null when no mapping
        /// covers it.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="localPath"/> is null.
        /// </exception>
        public SyncFolderMapping? FindMapping(string localPath)
        {
            if (localPath == null)
            {
                throw new ArgumentNullException(nameof(localPath));
            }

            SyncFolderMapping? best = null;

            for (int i = 0; i < _mappings.Count; i++)
            {
                SyncFolderMapping candidate = _mappings[i];
                if (!IsUnder(localPath, candidate.LocalFolder))
                {
                    continue;
                }

                if (best == null || candidate.LocalFolder.Length > best.LocalFolder.Length)
                {
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Works out where a local file belongs on the remote machine.
        /// </summary>
        /// <param name="localPath">
        /// The full path of the local file.
        /// </param>
        /// <param name="creating">
        /// <see langword="true"/> when the remote file does not exist yet, which is the ONLY case
        /// that takes quotes.
        /// </param>
        /// <param name="fileSpec">
        /// Set to the remote filespec, or an empty string when the file cannot be carried.
        /// </param>
        /// <param name="problem">
        /// Set to a sentence saying why not, or an empty string on success.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a filespec was produced.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="localPath"/> is null.
        /// </exception>
        /// <remarks>
        /// Reports a REASON rather than a bare false, in the same spirit as
        /// <see cref="SintranFileName.TryConvert"/>: "sits in a sub-folder and the mapping is
        /// top-level only" is something a person can act on.
        /// </remarks>
        public bool TryResolve(
            string localPath, bool creating, out string fileSpec, out string problem)
        {
            if (localPath == null)
            {
                throw new ArgumentNullException(nameof(localPath));
            }

            fileSpec = string.Empty;
            problem = string.Empty;

            SyncFolderMapping? mapping = FindMapping(localPath);
            if (mapping == null)
            {
                problem = "No mapping covers '" + localPath + "'.";
                return false;
            }

            string relative = localPath.Substring(mapping.LocalFolder.Length).TrimStart('\\', '/');
            if (relative.Length == 0)
            {
                problem = "'" + localPath + "' is the mapped folder itself, not a file in it.";
                return false;
            }

            // A file deeper than the mapped folder has nowhere to go in a flat directory. Whether
            // that is an error or a flattening is the mapping's decision, never a silent one.
            int lastSeparator = LastSeparator(relative);
            if (lastSeparator >= 0 && mapping.Subfolders == SyncSubfolderPolicy.TopLevelOnly)
            {
                problem = "'" + relative + "' is in a sub-folder, and the mapping for "
                    + mapping.LocalFolder + " carries top-level files only. A SINTRAN user"
                    + " directory is flat, so two files of the same name in different"
                    + " sub-folders would collide on one name.";
                return false;
            }

            string localName = lastSeparator >= 0
                ? relative.Substring(lastSeparator + 1)
                : relative;

            string name;
            string type;
            if (!SintranFileName.TryConvert(localName, out name, out type, out problem))
            {
                return false;
            }

            fileSpec = BuildFileSpec(mapping.Machine, mapping.User, name, type, creating);
            return true;
        }

        /// <summary>
        /// Builds a remote filespec in the form SINTRAN accepts as INPUT.
        /// </summary>
        /// <param name="machine">
        /// The machine name, for example <c>D102</c>.
        /// </param>
        /// <param name="user">
        /// The user, for example <c>SYSTEM</c>.
        /// </param>
        /// <param name="name">
        /// The SINTRAN file name.
        /// </param>
        /// <param name="type">
        /// The SINTRAN file type, or an empty string for none.
        /// </param>
        /// <param name="creating">
        /// <see langword="true"/> to quote the name, which is what creates a new file.
        /// </param>
        /// <returns>
        /// The filespec.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="machine"/>, <paramref name="user"/> or
        /// <paramref name="name"/> is null.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The quotes go around the NAME AND TYPE together and nothing else:
        /// <c>D102(SYSTEM)."BLKT7777:DATA"</c>. Not around the machine, not around the user, and
        /// not around the whole thing. The dot sits AFTER the closing parenthesis.
        /// </para>
        /// <para>
        /// A file that already exists takes NO quotes. <c>CREATE-FILE</c> takes no quotes either -
        /// it answers <c>ILLEGAL CHARACTER IN PARAMETER</c> - but that is a different command and
        /// not what this builds.
        /// </para>
        /// <para>
        /// This is the REMOTE form only. A LOCAL filespec quotes differently - the user
        /// prefix goes INSIDE the quotes, as in
        /// <c>BINARY-DUMP "(UTILITY)ENCOSE0-DUMP:BPUN"</c>, and putting them after the <c>(</c>
        /// there is the error. Both forms are verified against a machine and they simply do not
        /// agree, so do not reach for this method to build a local one.
        /// </para>
        /// </remarks>
        public static string BuildFileSpec(
            string machine, string user, string name, string type, bool creating)
        {
            if (machine == null) { throw new ArgumentNullException(nameof(machine)); }
            if (user == null) { throw new ArgumentNullException(nameof(user)); }
            if (name == null) { throw new ArgumentNullException(nameof(name)); }

            string bare = SintranFileName.ToFileSpec(name, type);
            string quoted = creating ? "\"" + bare + "\"" : bare;

            return machine + "(" + user + ")." + quoted;
        }

        /// <summary>
        /// Reduces an addressed file specification to the bare name the FA wire carries.
        /// </summary>
        /// <param name="fileSpec">
        /// A specification as <see cref="BuildFileSpec"/> produces it, for example
        /// <c>D100(SYSTEM)."WATCH1:TXT"</c>.
        /// </param>
        /// <returns>
        /// Just the name and type, for example <c>WATCH1:TXT</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Two forms, one for a person and one for the wire</b></para>
        /// <para>
        /// A plan is meant to be read, so it names files the way somebody would type them at a
        /// SINTRAN terminal - machine, user, then the name. The OPEN request carries only the name,
        /// because the conversation is already addressed to that machine.
        /// </para>
        /// <para><b>The user is NOT thrown away - it moves, and the caller must carry it</b></para>
        /// <para>
        /// This used to say "the file-access protocol does not carry the machine or the user at
        /// all". That is WRONG and it cost a wasted diagnosis on 2026-08-24, when a push logged
        /// <c>create D100(UTILITY)."XSTART:MODE"</c> and the file arrived as
        /// <c>(SYSTEM)XSTART:MODE</c>. The protocol carries the user TWICE, in the
        /// ReserveFileEntry request that opens the conversation - see FaWriteRequests, which
        /// decodes both copies. So stripping it HERE is only correct if the caller also sets the
        /// user on its endpoint. Use <see cref="ToUser"/> for that.
        /// </para>
        /// <para>
        /// MEASURED 2026-08-11: handing the addressed form to the open request produced
        /// <c>'"D100(SYSTEM)."WATCH1:TXT""' is 27 characters</c> - the compact QFORM string tops
        /// out at fifteen bytes, so it was refused before it reached the wire. The quotes are
        /// dropped here too; whether the name is quoted is decided where the request is built,
        /// because only there is it known whether the file is being created.
        /// </para>
        /// </remarks>
        public static string ToWireName(string fileSpec)
        {
            if (fileSpec == null) { throw new ArgumentNullException(nameof(fileSpec)); }

            // Everything before the LAST dot that follows a ")" is the address. A file type is
            // separated by a colon, never a dot, so the first dot after the user group ends it.
            string rest = fileSpec;
            int close = rest.IndexOf(')');
            if (close >= 0 && close + 1 < rest.Length && rest[close + 1] == '.')
            {
                rest = rest.Substring(close + 2);
            }

            return rest.Replace("\"", string.Empty).Trim();
        }

        /// <summary>
        /// Recovers the user from an addressed file specification.
        /// </summary>
        /// <param name="fileSpec">
        /// A specification as <see cref="BuildFileSpec"/> produces it, for example
        /// <c>D100(UTILITY)."XSTART:MODE"</c>.
        /// </param>
        /// <returns>
        /// The user, for example <c>UTILITY</c>, or an empty string when the specification names
        /// none.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Why this is needed at all</b></para>
        /// <para>
        /// <see cref="ToWireName"/> reduces the specification to the bare name the OPEN request
        /// carries. The user still has to reach the machine, and it travels in the
        /// ReserveFileEntry request instead - so whoever builds an endpoint has to put it back.
        /// Without this a mapping's user is decoration: it shapes the log line and the ledger key
        /// and the file lands in the session's own user regardless.
        /// </para>
        /// </remarks>
        public static string ToUser(string fileSpec)
        {
            if (fileSpec == null) { throw new ArgumentNullException(nameof(fileSpec)); }

            int open = fileSpec.IndexOf('(');
            if (open < 0) { return string.Empty; }

            int close = fileSpec.IndexOf(')', open + 1);
            if (close < 0) { return string.Empty; }

            return fileSpec.Substring(open + 1, close - open - 1).Trim();
        }

        /// <summary>
        /// Decides whether a path sits inside a folder.
        /// </summary>
        /// <param name="path">
        /// The full path.
        /// </param>
        /// <param name="folder">
        /// The folder, without a trailing separator.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the path is the folder or below it.
        /// </returns>
        /// <remarks>
        /// The separator check matters: without it <c>E:\work\project2\x</c> would count as being
        /// inside a mapping for <c>E:\work\project</c>, and the file would be carried to the wrong
        /// user.
        /// </remarks>
        private static bool IsUnder(string path, string folder)
        {
            if (path.Length < folder.Length)
            {
                return false;
            }

            if (string.Compare(path, 0, folder, 0, folder.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            if (path.Length == folder.Length)
            {
                return true;
            }

            char next = path[folder.Length];
            return next == '\\' || next == '/';
        }

        /// <summary>
        /// Finds the last path separator, either kind.
        /// </summary>
        /// <param name="value">
        /// The relative path.
        /// </param>
        /// <returns>
        /// The index, or -1 when there is none.
        /// </returns>
        private static int LastSeparator(string value)
        {
            for (int i = value.Length - 1; i >= 0; i--)
            {
                if (value[i] == '\\' || value[i] == '/')
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
