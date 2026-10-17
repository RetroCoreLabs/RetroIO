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
    /// One local Windows folder tied to one SINTRAN user, with the settings that say what moves
    /// and in which direction.
    /// </summary>
    /// <remarks>
    /// <para><b>Folder to USER, because SINTRAN has no folders</b></para>
    /// <para>
    /// A SINTRAN machine keeps a FLAT directory per user, so there is nothing for a Windows path
    /// to correspond to except the user itself: <c>E:\work\proj</c> maps to <c>D102</c> user
    /// <c>SYSTEM</c>, and every file in it becomes a file in that user's directory. Several local
    /// folders can be mapped to several users; each mapping is one of these.
    /// </para>
    /// <para><b>Every destructive or surprising behaviour is off by default</b></para>
    /// <para>
    /// Deleting on the far end and dragging a whole user directory back into a Windows folder are
    /// both things a person should ask for rather than discover. See
    /// <see cref="DeleteRemoteWhenLocalDeleted"/> and <see cref="PullTypes"/>.
    /// </para>
    /// </remarks>
    public sealed class SyncFolderMapping
    {
        /// <summary>
        /// The SINTRAN file types pulled back when nothing is configured.
        /// </summary>
        /// <remarks>
        /// Empty on purpose: a mapping pulls back NOTHING until somebody names the types. A user
        /// directory holds far more than one project's build output, so "everything" is the wrong
        /// default in both directions - it would be slow, and it would drop files into a Windows
        /// folder that the person never asked for.
        /// </remarks>
        private readonly List<string> _pullTypes = new List<string>();

        /// <summary>
        /// Creates a mapping from a local folder to a user on a machine.
        /// </summary>
        /// <param name="localFolder">
        /// The Windows folder, for example <c>E:\work\proj</c>.
        /// </param>
        /// <param name="machine">
        /// The SINTRAN machine name as the remote knows it, for example <c>D102</c>.
        /// </param>
        /// <param name="user">
        /// The user whose flat directory the files live in, for example <c>SYSTEM</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when any argument is empty.
        /// </exception>
        public SyncFolderMapping(string localFolder, string machine, string user)
        {
            Require(localFolder, nameof(localFolder));
            Require(machine, nameof(machine));
            Require(user, nameof(user));

            // Kept without a trailing separator so that comparing paths is one rule rather than
            // two. A caller may reasonably pass either form.
            LocalFolder = localFolder.TrimEnd('\\', '/');
            Machine = machine;
            User = user;
            Direction = SyncDirection.ToMachine;
            Subfolders = SyncSubfolderPolicy.TopLevelOnly;
        }

        /// <summary>
        /// Gets the local folder, without a trailing separator.
        /// </summary>
        public string LocalFolder { get; }

        /// <summary>
        /// Gets the SINTRAN machine name.
        /// </summary>
        public string Machine { get; }

        /// <summary>
        /// Gets the SINTRAN user whose directory the files live in.
        /// </summary>
        public string User { get; }

        /// <summary>
        /// Gets or sets which way this mapping carries files.
        /// </summary>
        /// <remarks>
        /// <see cref="SyncDirection.ToMachine"/> by default - editing on Windows and building
        /// on SINTRAN is the case this exists for. Pulling build output back is
        /// <see cref="SyncDirection.FromMachine"/>, and a mapping that does both is two
        /// mappings over the same folder, because a transfer has ONE direction.
        /// </remarks>
        public SyncDirection Direction { get; set; }

        /// <summary>
        /// Gets or sets what happens to files in sub-folders of the mapped folder.
        /// </summary>
        public SyncSubfolderPolicy Subfolders { get; set; }

        /// <summary>
        /// Gets or sets whether deleting a local file deletes the remote one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// False by default, and that is a safety decision rather than a preference. A sync
        /// that deletes on the far end by default turns any bad local state - a half-finished
        /// checkout, a folder that failed to mount, a wrong mapping - into the destruction of the
        /// only good copy. Nothing on the SINTRAN side is under version control.
        /// </para>
        /// <para>
        /// Whether the FA <c>DeleteFile</c> operation works at all is UNVERIFIED; see task #23.
        /// This setting says what the daemon should INTEND, not that the intention can yet be
        /// carried out.
        /// </para>
        /// </remarks>
        public bool DeleteRemoteWhenLocalDeleted { get; set; }

        /// <summary>
        /// Gets the SINTRAN file types this mapping pulls back, upper-cased and without the colon.
        /// </summary>
        /// <remarks>
        /// Typically the build output that never existed locally - <c>BPUN</c>, <c>SYMB</c>,
        /// <c>LIST</c>, <c>PROG</c>. Empty means pull nothing.
        /// </remarks>
        public IReadOnlyList<string> PullTypes
        {
            get { return _pullTypes; }
        }

        /// <summary>
        /// Adds a SINTRAN file type to pull back.
        /// </summary>
        /// <param name="type">
        /// The type without the colon, for example <c>BPUN</c>. Case does not matter; it is stored
        /// upper-cased, which is how SINTRAN reports it.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="type"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="type"/> is empty or longer than
        /// <see cref="SintranFileName.MaxTypeLength"/>.
        /// </exception>
        public void AddPullType(string type)
        {
            Require(type, nameof(type));

            string upper = type.ToUpperInvariant();
            if (upper.Length > SintranFileName.MaxTypeLength)
            {
                throw new ArgumentException(
                    "A SINTRAN file type is at most " + SintranFileName.MaxTypeLength
                        + " characters; '" + type + "' is " + type.Length + ".",
                    nameof(type));
            }

            for (int i = 0; i < _pullTypes.Count; i++)
            {
                if (_pullTypes[i] == upper)
                {
                    return;
                }
            }

            _pullTypes.Add(upper);
        }

        /// <summary>
        /// Decides whether a SINTRAN file type is one this mapping brings back.
        /// </summary>
        /// <param name="type">
        /// The type reported by the remote, with or without a colon and in any case.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the type was named through <see cref="AddPullType"/>.
        /// </returns>
        /// <remarks>
        /// A mapping with no types named pulls NOTHING, which is what stops a first run from
        /// dragging a whole user directory onto the local disk.
        /// </remarks>
        public bool PullsType(string type)
        {
            if (type == null)
            {
                return false;
            }

            string upper = type.TrimStart(':').ToUpperInvariant();

            for (int i = 0; i < _pullTypes.Count; i++)
            {
                if (_pullTypes[i] == upper)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Rejects a null or empty argument.
        /// </summary>
        /// <param name="value">
        /// The value to check.
        /// </param>
        /// <param name="parameterName">
        /// The argument to blame.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="value"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="value"/> is empty.
        /// </exception>
        private static void Require(string value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("The value cannot be empty.", parameterName);
            }
        }
    }
}
