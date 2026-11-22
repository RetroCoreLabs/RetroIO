//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// The served root of a COSMOS file server: one folder per SINTRAN user under it, that user's
    /// files in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>(ROLF)LETTER:SYMB</c> is <c>&lt;root&gt;\ROLF\LETTER.SYMB</c>. A user with no folder under the
    /// root does not exist: <see cref="ForUser"/> returns null and the server answers SINTRAN error 39,
    /// "No such user name in main directory". The server never creates a user folder by itself; a user
    /// is made by creating the folder (Ronny, 2026-11-22: per-user always, no flat-folder setting).
    /// </para>
    /// <para>
    /// The folder name is matched without regard to case, as SINTRAN user names are. Each user's files
    /// are a <see cref="FolderFileStore"/>, made the first time the user is asked for and kept, so its
    /// file numbers stay the same for the life of the server.
    /// </para>
    /// </remarks>
    public sealed class FaUserFolders
    {
        /// <summary>
        /// The longest user name accepted: the reserve's 16-byte user field less its terminator.
        /// </summary>
        public const int MaxUserNameLength = 15;

        private readonly string _root;
        private readonly IFileSystemBackend? _backend;
        private readonly string _location;
        private readonly Dictionary<string, FolderFileStore> _stores =
            new Dictionary<string, FolderFileStore>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        /// <summary>
        /// Initialises the user folders over a local folder.
        /// </summary>
        /// <param name="rootFolder">
        /// The folder that holds one folder per user. It is created when it does not exist.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="rootFolder"/> is empty.
        /// </exception>
        public FaUserFolders(string rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException("A root folder is required.", nameof(rootFolder));
            }

            _root = Path.GetFullPath(rootFolder);
            _location = _root;
            Directory.CreateDirectory(_root);
        }

        /// <summary>
        /// Initialises the user folders over any storage the backend reaches (TNFS, a mounts.json volume).
        /// </summary>
        /// <param name="backend">
        /// The storage.
        /// </param>
        /// <param name="rootFolder">
        /// The folder on that storage that holds one folder per user.
        /// </param>
        /// <param name="location">
        /// How the storage is named in logs, for example <c>tnfs:tnfs://host/cosmos</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="backend"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="rootFolder"/> is empty.
        /// </exception>
        public FaUserFolders(IFileSystemBackend backend, string rootFolder, string location)
        {
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException("A root folder is required.", nameof(rootFolder));
            }

            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _root = backend.GetFullPath(rootFolder);
            _location = string.IsNullOrEmpty(location) ? _root : location;
            if (!backend.DirectoryExists(_root))
            {
                backend.CreateDirectory(_root);
            }
        }

        /// <summary>
        /// Gets where the user folders are, for logs.
        /// </summary>
        public string Location
        {
            get { return _location; }
        }

        /// <summary>
        /// Gets the files of one user.
        /// </summary>
        /// <param name="user">
        /// The SINTRAN user name, as the client sent it.
        /// </param>
        /// <returns>
        /// The user's files, or null when the name is not a valid user name or no folder of that name
        /// is under the root.
        /// </returns>
        public IFaFileStore? ForUser(string? user)
        {
            if (user == null || !IsValidUserName(user))
            {
                return null;
            }

            lock (_gate)
            {
                if (_stores.TryGetValue(user, out FolderFileStore? known))
                {
                    return known;
                }

                string? folder = FindUserFolder(user);
                if (folder == null)
                {
                    return null;
                }

                FolderFileStore store = _backend == null
                    ? new FolderFileStore(folder)
                    : new FolderFileStore(_backend, folder, _location + "/" + user);
                _stores[user] = store;
                return store;
            }
        }

        /// <summary>
        /// Lists the users: the folders under the root whose names can be user names.
        /// </summary>
        /// <returns>
        /// The folder names, in name order.
        /// </returns>
        public IReadOnlyList<string> ListUsers()
        {
            List<string> users = new List<string>();
            string[] folders = ListFolders();
            for (int i = 0; i < folders.Length; i++)
            {
                string name = LastPart(folders[i]);
                if (IsValidUserName(name))
                {
                    users.Add(name);
                }
            }

            users.Sort(StringComparer.OrdinalIgnoreCase);
            return users;
        }

        /// <summary>
        /// Tells whether a name can be a SINTRAN user name and is safe as a folder name.
        /// </summary>
        /// <param name="user">
        /// The name.
        /// </param>
        /// <returns>
        /// True for 1 to <see cref="MaxUserNameLength"/> letters, digits and hyphens.
        /// </returns>
        /// <remarks>
        /// Anything else is refused rather than cleaned, so a name like <c>..</c> or one with a
        /// separator can never reach the storage: a cleaned traversal attempt is still an attempt.
        /// </remarks>
        public static bool IsValidUserName(string user)
        {
            if (string.IsNullOrEmpty(user) || user.Length > MaxUserNameLength)
            {
                return false;
            }

            for (int i = 0; i < user.Length; i++)
            {
                char c = user[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the last part of a path, with either separator.
        /// </summary>
        private static string LastPart(string path)
        {
            string trimmed = path.TrimEnd('/', '\\');
            int cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            return cut < 0 ? trimmed : trimmed.Substring(cut + 1);
        }

        /// <summary>
        /// Finds the folder for a user, matching the name without regard to case.
        /// </summary>
        private string? FindUserFolder(string user)
        {
            string[] folders = ListFolders();
            for (int i = 0; i < folders.Length; i++)
            {
                if (string.Equals(LastPart(folders[i]), user, StringComparison.OrdinalIgnoreCase))
                {
                    return folders[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Lists the folders directly under the root.
        /// </summary>
        private string[] ListFolders()
        {
            if (_backend == null)
            {
                return Directory.GetDirectories(_root);
            }

            List<string> folders = new List<string>();
            using (IEnumerator<string> entries = _backend.EnumerateFileSystemEntries(_root).GetEnumerator())
            {
                while (entries.MoveNext())
                {
                    if (_backend.IsDirectory(entries.Current))
                    {
                        folders.Add(entries.Current);
                    }
                }
            }

            return folders.ToArray();
        }
    }
}
