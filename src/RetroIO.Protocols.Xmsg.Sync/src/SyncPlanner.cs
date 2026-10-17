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
    /// Turns a folder full of files, a listing from the machine and what was carried last time
    /// into a list of things to do.
    /// </summary>
    /// <remarks>
    /// <para><b>Deciding is separated from doing, on purpose</b></para>
    /// <para>
    /// This class opens no file, sends no frame and needs no machine. That is what makes the
    /// interesting cases testable at all: a file whose name will not fit, a delete that a setting
    /// refuses, an edit that is really just our own push coming back. Every one of those is a
    /// decision, and decisions are cheap to get wrong quietly.
    /// </para>
    /// <para><b>Silence means "nothing to do", and it is deliberate</b></para>
    /// <para>
    /// An unchanged file produces NO action. So does a remote file of a type this mapping does not
    /// bring back. A plan that listed every file on every pass would be a page of noise hiding the
    /// one line that mattered. What DOES get reported, as a <see cref="SyncActionKind.Skip"/>, is
    /// anything a person might need to act on: a refused name, or a delete that was wanted and
    /// declined.
    /// </para>
    /// <para><b>What this cannot decide</b></para>
    /// <para>
    /// A <see cref="SyncActionKind.Pull"/> is a CANDIDATE, not a certainty. A listing reports names
    /// and never content, so whether a remote file really differs from the local copy can only be
    /// settled after reading it. Whoever carries the plan out is expected to hash what came back
    /// and ask the ledger again before writing to disk.
    /// </para>
    /// </remarks>
    public sealed class SyncPlanner
    {
        private readonly SyncFolderMap _map;
        private readonly SyncLedger _ledger;

        /// <summary>
        /// Creates a planner.
        /// </summary>
        /// <param name="map">
        /// The folder-to-user mappings, which own the addressing and quoting rules.
        /// </param>
        /// <param name="ledger">
        /// What was carried last time. Read but never changed here - recording a transfer is the
        /// job of whoever actually completes one.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="map"/> or <paramref name="ledger"/> is null.
        /// </exception>
        public SyncPlanner(SyncFolderMap map, SyncLedger ledger)
        {
            if (map == null) { throw new ArgumentNullException(nameof(map)); }
            if (ledger == null) { throw new ArgumentNullException(nameof(ledger)); }

            _map = map;
            _ledger = ledger;
        }

        /// <summary>
        /// Works out what to do for one mapping.
        /// </summary>
        /// <param name="mapping">
        /// The mapping being run. Its <see cref="SyncFolderMapping.Direction"/> decides which half
        /// of the work applies.
        /// </param>
        /// <param name="local">
        /// Every file currently in the mapped folder, with its content hashed.
        /// </param>
        /// <param name="remote">
        /// Every file currently in the remote user's directory, as a listing reported them.
        /// </param>
        /// <returns>
        /// The actions, in the order the files were given, with any deletions last.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        /// <remarks>
        /// One entry point rather than a push method and a pull method, so that a caller cannot
        /// run a push over a mapping that was configured to pull. A mapping carries files ONE way;
        /// a folder that needs both is two mappings.
        /// </remarks>
        public SyncAction[] Plan(
            SyncFolderMapping mapping, LocalFileState[] local, RemoteFileState[] remote)
        {
            if (mapping == null) { throw new ArgumentNullException(nameof(mapping)); }
            if (local == null) { throw new ArgumentNullException(nameof(local)); }
            if (remote == null) { throw new ArgumentNullException(nameof(remote)); }

            List<SyncAction> actions = new List<SyncAction>();

            if (mapping.Direction == SyncDirection.ToMachine)
            {
                PlanPush(mapping, local, remote, actions);
            }
            else if (mapping.Direction == SyncDirection.FromMachine)
            {
                PlanPull(mapping, remote, actions);
            }

            return actions.ToArray();
        }

        /// <summary>
        /// Plans everything that goes out to the machine, including what should disappear there.
        /// </summary>
        /// <param name="mapping">
        /// The mapping being run.
        /// </param>
        /// <param name="local">
        /// The files in the mapped folder.
        /// </param>
        /// <param name="remote">
        /// The files in the remote user's directory.
        /// </param>
        /// <param name="actions">
        /// The list being built.
        /// </param>
        private void PlanPush(
            SyncFolderMapping mapping,
            LocalFileState[] local,
            RemoteFileState[] remote,
            List<SyncAction> actions)
        {
            for (int i = 0; i < local.Length; i++)
            {
                LocalFileState file = local[i];

                // A file handed to the wrong mapping would be carried to the wrong USER, which on
                // a flat directory means it lands next to somebody else's work under a name that
                // may already be taken. Worth a loud line rather than a quiet redirect.
                SyncFolderMapping? owner = _map.FindMapping(file.Path);
                if (!ReferenceEquals(owner, mapping))
                {
                    actions.Add(new SyncAction(
                        SyncActionKind.Skip,
                        file.Path,
                        string.Empty,
                        "'" + file.Path + "' is not covered by the mapping for "
                            + mapping.LocalFolder + "."));
                    continue;
                }

                // Resolving first, with creating false, gets every refusal in one place and with a
                // message worth reading - a sub-folder, a name over sixteen characters, a type over
                // four. The quoting is settled further down, once existence is known.
                string bareSpec;
                string problem;
                if (!_map.TryResolve(file.Path, false, out bareSpec, out problem))
                {
                    actions.Add(new SyncAction(
                        SyncActionKind.Skip, file.Path, string.Empty, problem));
                    continue;
                }

                // Nothing to do when what we would send is what we last sent. This is also what
                // stops the daemon fighting itself: a file that came back from the machine looks
                // like an edit to a folder watcher, and only the content says otherwise.
                if (!_ledger.NeedsTransfer(file.Path, file.Hash))
                {
                    continue;
                }

                string name;
                string type;
                string ignored;
                SintranFileName.TryConvert(LeafName(file.Path), out name, out type, out ignored);

                // DOES THE FILE ALREADY EXIST ON THE MACHINE? The listing is the authority when we
                // have one. When we have NONE - and the daemon has no listing at all today - an
                // empty array is indistinguishable from a genuinely empty directory, so everything
                // read as new and every push after a restart went out as a CREATE of a name that
                // already existed, which SINTRAN refuses.
                //
                // The ledger settles it without asking anybody: it holds an entry for this path
                // only if we have ACTUALLY carried that file to or from this machine. Either way
                // the file was there. That is real evidence, and it is the only evidence available
                // when there is no listing.
                //
                // It can still be wrong in exactly one way - somebody deleted the file on the
                // machine since - which is precisely what a listing would tell us. So the ledger is
                // consulted ONLY as a fallback, never to overrule a listing we actually have.
                // KnownToExistRemotely, not HasCarried. HasCarried is only ever true for files WE
                // moved, so a file somebody else created - or one that outlived a deleted ledger -
                // was invisible here, the plan said CREATE, and SINTRAN refused it. The ledger now
                // also remembers what the machine has TOLD us exists, which it does by refusing a
                // create with error 62.
                bool exists = ExistsRemotely(remote, name, type)
                    || (remote.Length == 0 && _ledger.KnownToExistRemotely(file.Path));

                if (exists)
                {
                    actions.Add(new SyncAction(
                        SyncActionKind.Overwrite,
                        file.Path,
                        bareSpec,
                        "The remote file exists and its content has changed."));
                    continue;
                }

                // A file being CREATED takes quotes around name and type. That is not decoration:
                // without them SINTRAN complains about a file that is not there.
                string quotedSpec;
                if (!_map.TryResolve(file.Path, true, out quotedSpec, out problem))
                {
                    actions.Add(new SyncAction(
                        SyncActionKind.Skip, file.Path, string.Empty, problem));
                    continue;
                }

                actions.Add(new SyncAction(
                    SyncActionKind.Create,
                    file.Path,
                    quotedSpec,
                    "No file of that name exists on the machine."));
            }

            PlanDeletions(mapping, local, remote, actions);
        }

        /// <summary>
        /// Plans what should go away on the machine because it has gone locally.
        /// </summary>
        /// <param name="mapping">
        /// The mapping being run.
        /// </param>
        /// <param name="local">
        /// The files still in the mapped folder.
        /// </param>
        /// <param name="remote">
        /// The files in the remote user's directory.
        /// </param>
        /// <param name="actions">
        /// The list being built.
        /// </param>
        /// <remarks>
        /// <para>
        /// A folder scan can only report what IS there, so a deleted file is invisible except in
        /// the ledger's memory of having carried it. That memory is the only evidence, which is
        /// also why this is careful: it acts on a path only when the ledger knows it, the mapping
        /// owns it, the local file is gone AND the remote one is really there.
        /// </para>
        /// <para>
        /// Nothing is forgotten here. The planner does not change the ledger, so a plan can be
        /// looked at, or thrown away, without leaving the daemon believing something happened.
        /// </para>
        /// </remarks>
        private void PlanDeletions(
            SyncFolderMapping mapping,
            LocalFileState[] local,
            RemoteFileState[] remote,
            List<SyncAction> actions)
        {
            string[] known = _ledger.CopyPaths();

            for (int i = 0; i < known.Length; i++)
            {
                string path = known[i];

                SyncFolderMapping? owner = _map.FindMapping(path);
                if (!ReferenceEquals(owner, mapping))
                {
                    continue;
                }

                if (IsInSnapshot(local, path))
                {
                    continue;
                }

                string bareSpec;
                string problem;
                if (!_map.TryResolve(path, false, out bareSpec, out problem))
                {
                    // The ledger holds a path the mapping can no longer address - a mapping that
                    // has been narrowed since, most likely. Say so instead of silently letting the
                    // remote copy live for ever.
                    actions.Add(new SyncAction(
                        SyncActionKind.Skip, path, string.Empty, problem));
                    continue;
                }

                string name;
                string type;
                string ignored;
                SintranFileName.TryConvert(LeafName(path), out name, out type, out ignored);

                if (!ExistsRemotely(remote, name, type))
                {
                    // Gone in both places. There is nothing to do and nothing to warn about.
                    continue;
                }

                if (!mapping.DeleteRemoteWhenLocalDeleted)
                {
                    actions.Add(new SyncAction(
                        SyncActionKind.Skip,
                        path,
                        bareSpec,
                        "Deleted locally, but the mapping for " + mapping.LocalFolder
                            + " does not delete on the machine. Turn on"
                            + " DeleteRemoteWhenLocalDeleted if that is wanted."));
                    continue;
                }

                actions.Add(new SyncAction(
                    SyncActionKind.DeleteRemote,
                    path,
                    bareSpec,
                    "The local file has been deleted and the mapping deletes on the machine."));
            }
        }

        /// <summary>
        /// Plans what comes back from the machine.
        /// </summary>
        /// <param name="mapping">
        /// The mapping being run.
        /// </param>
        /// <param name="remote">
        /// The files in the remote user's directory.
        /// </param>
        /// <param name="actions">
        /// The list being built.
        /// </param>
        /// <remarks>
        /// Only the types the mapping was told to bring back are considered, and a mapping with
        /// none named brings back nothing. That is what stops a first run from dragging a whole
        /// user directory onto the local disk.
        /// </remarks>
        private void PlanPull(
            SyncFolderMapping mapping, RemoteFileState[] remote, List<SyncAction> actions)
        {
            for (int i = 0; i < remote.Length; i++)
            {
                RemoteFileState file = remote[i];

                if (!mapping.PullsType(file.Type))
                {
                    continue;
                }

                string windowsName = SintranFileName.ToWindowsFileName(file.Name, file.Type);
                string localPath = mapping.LocalFolder + "\\" + windowsName;

                actions.Add(new SyncAction(
                    SyncActionKind.Pull,
                    localPath,
                    SyncFolderMap.BuildFileSpec(mapping.Machine, mapping.User, file.Name, file.Type, false),
                    "The mapping brings back files of type " + file.Type + "."));
            }
        }

        /// <summary>
        /// Decides whether a listing holds a file of this name and type.
        /// </summary>
        /// <param name="remote">
        /// The listing.
        /// </param>
        /// <param name="name">
        /// The SINTRAN file name.
        /// </param>
        /// <param name="type">
        /// The SINTRAN file type, or an empty string.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the file is there.
        /// </returns>
        /// <remarks>
        /// Name AND type, because <c>A:SYMB</c> and <c>A:BPUN</c> are two different files that a
        /// name-only match would confuse - and confusing them turns a create into an overwrite of
        /// something else.
        /// </remarks>
        private static bool ExistsRemotely(RemoteFileState[] remote, string name, string type)
        {
            string upperName = name.ToUpperInvariant();
            string upperType = type.ToUpperInvariant();

            for (int i = 0; i < remote.Length; i++)
            {
                if (remote[i].Name == upperName && remote[i].Type == upperType)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Decides whether a path is among the files currently on disk.
        /// </summary>
        /// <param name="local">
        /// The snapshot of the folder.
        /// </param>
        /// <param name="path">
        /// The path to look for.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the file is still there.
        /// </returns>
        private static bool IsInSnapshot(LocalFileState[] local, string path)
        {
            for (int i = 0; i < local.Length; i++)
            {
                if (string.Equals(local[i].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Takes the file name off the end of a path.
        /// </summary>
        /// <param name="path">
        /// The full path.
        /// </param>
        /// <returns>
        /// The part after the last separator, or the whole string when there is none.
        /// </returns>
        private static string LeafName(string path)
        {
            for (int i = path.Length - 1; i >= 0; i--)
            {
                if (path[i] == '\\' || path[i] == '/')
                {
                    return path.Substring(i + 1);
                }
            }

            return path;
        }
    }
}
