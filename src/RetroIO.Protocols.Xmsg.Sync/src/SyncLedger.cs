using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Remembers what content was last carried for each path, so unchanged files are not resent
    /// and a file coming back is not mistaken for a new edit.
    /// </summary>
    /// <remarks>
    /// <para><b>The two problems this solves are the same problem</b></para>
    /// <para>
    /// A save with no edit should cost nothing. And a file we just pushed will come back from the
    /// return leg looking exactly like a change - push it again and the daemon fights itself for
    /// ever, each direction re-triggering the other.
    /// </para>
    /// <para>
    /// Both are answered by remembering the CONTENT last transferred for a path. If what we are
    /// about to send matches what the ledger holds, there is nothing to do, whichever direction
    /// raised it. Content, not timestamps: a timestamp cannot survive the trip anyway - packed ND
    /// dates only span 1950 to 2013, so a present-day stamp lands as zero.
    /// </para>
    /// <para><b>Direction is recorded but is not the guard</b></para>
    /// <para>
    /// It would be tempting to suppress the echo by direction alone - "ignore an inbound change
    /// for a file we just pushed". That fails the moment the machine legitimately rewrites a file
    /// we also edit, which is the normal case for anything a compiler touches. Comparing content
    /// is right in both cases; the direction is kept only so a human can see what happened.
    /// </para>
    /// </remarks>
    public sealed class SyncLedger
    {
        /// <summary>
        /// What the ledger holds for one path.
        /// </summary>
        private sealed class Entry
        {
            /// <summary>
            /// Hash of the content last carried.
            /// </summary>
            public byte[] Hash = Array.Empty<byte>();

            /// <summary>
            /// Which way it was carried, for reporting only.
            /// </summary>
            public SyncDirection Direction;
        }

        private readonly Dictionary<string, Entry> _entries;

        /// <summary>
        /// Paths the far machine has told us about, which we never carried ourselves.
        /// </summary>
        /// <remarks>
        /// <para><b>Existence is not the same as having been synced</b></para>
        /// <see cref="_entries"/> answers "we carried this, and here is the content we carried".
        /// This answers only "it is over there". They are kept apart deliberately: putting a made-up
        /// hash into the entries to record existence would tell <see cref="NeedsTransfer"/> that the
        /// file is already up to date, and the file would never be sent.
        /// <para><b>Not persisted, and that is a choice rather than an omission</b></para>
        /// It is re-learned the first time a create is refused, which costs one round trip per file
        /// per daemon restart and cannot go stale - the alternative is a ledger-file format change
        /// carrying a fact the machine will happily tell us again for free.
        /// </remarks>
        private readonly HashSet<string> _knownRemote;

        /// <summary>
        /// Bumped by every call that changes what the ledger knows. See <see cref="Revision"/>.
        /// </summary>
        private int _revision;

        /// <summary>
        /// Creates an empty ledger.
        /// </summary>
        public SyncLedger()
        {
            _knownRemote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets how many paths the ledger knows about.
        /// </summary>
        /// <summary>
        /// Gets a number that changes whenever the ledger learns anything.
        /// </summary>
        /// <remarks>
        /// <para><b>So the daemon can tell "worth writing" from "nothing happened"</b></para>
        /// It used to save only when a TRANSFER completed. That misses the other thing the ledger
        /// learns - that a file is already on the machine - which is recorded when a create is
        /// refused. MEASURED 2026-08-18: the daemon learned it, was killed before the following
        /// overwrite finished, and the fact was gone; the next run paid the same refusal again.
        /// <para>
        /// A counter rather than a dirty flag, so a caller compares it with what it last saw and
        /// nothing has to be reset. Wrapping is harmless: the comparison is for INEQUALITY, so the
        /// only cost of a wrap landing exactly on the previous value is one skipped save.
        /// </para>
        /// </remarks>
        public int Revision
        {
            get { return _revision; }
        }

        /// <summary>
        /// Gets how many transfer entries the ledger holds.
        /// </summary>
        /// <remarks>
        /// Transfer entries only. A path known to be on the machine but never carried by us has no
        /// entry and is not counted here - see <see cref="CopyRemoteOnlyPaths"/>.
        /// </remarks>
        public int Count
        {
            get { return _entries.Count; }
        }

        /// <summary>
        /// Decides whether content needs carrying for a path.
        /// </summary>
        /// <param name="path">
        /// The path being considered.
        /// </param>
        /// <param name="hash">
        /// Hash of the content that would be sent.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the ledger has no record of this path, or its record holds
        /// different content.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> or <paramref name="hash"/> is null.
        /// </exception>
        public bool NeedsTransfer(string path, byte[] hash)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (hash == null)
            {
                throw new ArgumentNullException(nameof(hash));
            }

            Entry? entry;
            if (!_entries.TryGetValue(path, out entry))
            {
                return true;
            }

            return !SameHash(entry.Hash, hash);
        }

        /// <summary>
        /// Records that content was carried for a path.
        /// </summary>
        /// <param name="path">
        /// The path that was carried.
        /// </param>
        /// <param name="hash">
        /// Hash of the content carried. Copied, so a caller reusing its buffer cannot corrupt the
        /// ledger afterwards.
        /// </param>
        /// <param name="direction">
        /// Which way it went.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> or <paramref name="hash"/> is null.
        /// </exception>
        public void RecordTransfer(string path, byte[] hash, SyncDirection direction)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (hash == null)
            {
                throw new ArgumentNullException(nameof(hash));
            }

            byte[] copy = new byte[hash.Length];
            for (int i = 0; i < hash.Length; i++)
            {
                copy[i] = hash[i];
            }

            Entry? entry;
            if (!_entries.TryGetValue(path, out entry))
            {
                entry = new Entry();
                _entries.Add(path, entry);
            }

            entry.Hash = copy;
            entry.Direction = direction;
            _revision++;
        }

        /// <summary>
        /// Gets which way a path was last carried.
        /// </summary>
        /// <param name="path">
        /// The path to ask about.
        /// </param>
        /// <param name="direction">
        /// Set to the recorded direction, or <see cref="SyncDirection.None"/> when the path is
        /// unknown.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the path was found.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public bool TryGetDirection(string path, out SyncDirection direction)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            Entry? entry;
            if (_entries.TryGetValue(path, out entry))
            {
                direction = entry.Direction;
                return true;
            }

            direction = SyncDirection.None;
            return false;
        }

        /// <summary>
        /// Whether this path has ever been carried, whatever its content was at the time.
        /// </summary>
        /// <param name="path">
        /// The path to ask about.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a transfer of this path has been recorded.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// Deliberately NOT about content - <see cref="NeedsTransfer"/> answers that. This answers
        /// a different question: has this file ever been on the machine? A recorded transfer is
        /// evidence that it was, which is what lets the planner choose between creating a file and
        /// replacing one when it has no directory listing to consult.
        /// </remarks>
        public bool HasCarried(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return _entries.ContainsKey(path);
        }

        /// <summary>
        /// Records that a file is on the far machine although we never carried it there.
        /// </summary>
        /// <param name="path">
        /// The local path whose remote twin exists.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Where this fact comes from</b></para>
        /// The machine itself, by refusing a create with SINTRAN error 62, "File already exists".
        /// That refusal IS the directory listing we did not have - see
        /// <see cref="KnownToExistRemotely"/> for why the planner needs it.
        /// <para>
        /// Deliberately records no hash. We know the file is there; we know nothing at all about
        /// what is in it, and inventing a hash would be worse than knowing nothing.
        /// </para>
        /// </remarks>
        public void RecordRemoteExistence(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (_knownRemote.Add(path))
            {
                _revision++;
            }
        }

        /// <summary>
        /// Lists the paths known to be on the machine that we never carried ourselves.
        /// </summary>
        /// <returns>
        /// The paths, in no particular order.
        /// </returns>
        /// <remarks>
        /// Only the ones with no transfer entry. A path we carried is already known to exist by
        /// virtue of the entry, so writing it twice would say nothing and could disagree with
        /// itself after an edit.
        /// </remarks>
        public string[] CopyRemoteOnlyPaths()
        {
            // CopyTo then a for loop: a HashSet cannot be indexed, and foreach is avoided here as
            // everywhere else in this codebase.
            string[] all = new string[_knownRemote.Count];
            _knownRemote.CopyTo(all);

            List<string> only = new List<string>(all.Length);

            for (int i = 0; i < all.Length; i++)
            {
                if (!_entries.ContainsKey(all[i]))
                {
                    only.Add(all[i]);
                }
            }

            return only.ToArray();
        }

        /// <summary>
        /// Gets whether the file is believed to be on the far machine.
        /// </summary>
        /// <param name="path">
        /// The path to ask about.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when we carried it there, or the machine has told us it is there.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>This is the question the planner actually has</b></para>
        /// It must choose between CREATE and OVERWRITE, and SINTRAN refuses a create of a file that
        /// already exists. <see cref="HasCarried"/> was standing in for this and answers a narrower
        /// question - it is only ever true for files WE moved. A file somebody else put there, or
        /// one that outlived a deleted ledger, was invisible, so the planner chose create and the
        /// machine refused it. MEASURED 2026-08-18: before the refusal was read at all that was
        /// recorded as success and the file was silently never sent; after, it retried for ever.
        /// </remarks>
        public bool KnownToExistRemotely(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return _entries.ContainsKey(path) || _knownRemote.Contains(path);
        }

        /// <summary>
        /// Reads back everything recorded for a path.
        /// </summary>
        /// <param name="path">
        /// The path to ask about.
        /// </param>
        /// <param name="hash">
        /// The recorded content hash, or an empty array when the path is unknown.
        /// </param>
        /// <param name="direction">
        /// The recorded direction, or <see cref="SyncDirection.None"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the path was found.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// Exists so the ledger can be WRITTEN DOWN. Without the hash a saved ledger could only
        /// say "this path was carried once", which is not what the ledger promises - it promises
        /// "this exact CONTENT was carried", and that is the only form of it worth keeping.
        /// </remarks>
        public bool TryGetEntry(string path, out byte[] hash, out SyncDirection direction)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            Entry? entry;
            if (_entries.TryGetValue(path, out entry))
            {
                hash = entry.Hash;
                direction = entry.Direction;
                return true;
            }

            hash = Array.Empty<byte>();
            direction = SyncDirection.None;
            return false;
        }

        /// <summary>
        /// Copies out every path the ledger remembers.
        /// </summary>
        /// <returns>
        /// The paths, in no particular order. A fresh array each time, so a caller can forget
        /// paths while walking it.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is how a DELETED file is noticed at all. A folder scan can only report what is
        /// there; a file that has gone leaves no trace except the ledger's memory of having
        /// carried it. Without this the daemon would silently leave stale copies on the machine
        /// for ever.
        /// </para>
        /// <para>
        /// A copy rather than the live keys, because the caller's next move is usually to decide
        /// some of those paths are gone and forget them, and a dictionary cannot be changed while
        /// its keys are being walked.
        /// </para>
        /// </remarks>
        public string[] CopyPaths()
        {
            string[] paths = new string[_entries.Count];

            int index = 0;
            Dictionary<string, Entry>.KeyCollection.Enumerator keys = _entries.Keys.GetEnumerator();
            while (keys.MoveNext())
            {
                paths[index] = keys.Current;
                index++;
            }

            return paths;
        }

        /// <summary>
        /// Forgets a path, so the next transfer for it is treated as new.
        /// </summary>
        /// <param name="path">
        /// The path to forget.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the path was known.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public bool Forget(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return _entries.Remove(path);
        }

        /// <summary>
        /// Compares two hashes.
        /// </summary>
        /// <param name="left">
        /// The first hash.
        /// </param>
        /// <param name="right">
        /// The second hash.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when they are the same length and every byte matches.
        /// </returns>
        private static bool SameHash(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
