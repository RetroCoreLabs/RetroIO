//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Security;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// One friend of a user, as <see cref="NdfsFileSystem.ListFriends"/> reports it.
    /// </summary>
    /// <param name="Index">
    /// The friend's user index.
    /// </param>
    /// <param name="Name">
    /// The friend's user name, or empty when no user has that index.
    /// </param>
    /// <param name="Bits">
    /// The raw 16-bit friend word: index in bits 0-7, rights in bits 8-12, in-use in bit 15.
    /// </param>
    /// <param name="Rights">
    /// The rights as five letters, <c>-</c> where a right is missing, e.g. <c>RWA--</c>.
    /// </param>
    public readonly record struct NdfsFriendInfo(int Index, string Name, ushort Bits, string Rights);

    /// <summary>
    /// The SINTRAN access and friend commands on a mounted pack: <c>@LIST-FRIENDS</c>,
    /// <c>@CREATE-FRIEND</c>, <c>@DELETE-FRIEND</c>, <c>@SET-FRIEND-ACCESS</c>,
    /// <c>@SET-FILE-ACCESS</c> and <c>@SET-DEFAULT-FILE-ACCESS</c> (SINTRAN III Reference Manual
    /// ND-60.128.5). Each change writes only the page that holds the changed entry and flushes the
    /// device. The same calls exist in the norskdata-ndfs C, Python and TypeScript libraries.
    /// </summary>
    /// <remarks>
    /// Users and friends may be given by name (any case) or by decimal user index. A friend given
    /// by index need not be a named user, as in the other ports. Access fields are SINTRAN's own
    /// form; see <see cref="SintranAccessForm"/>.
    /// </remarks>
    public partial class NdfsFileSystem
    {
        /// <summary>
        /// Lists a user's friends in slot order (<c>@LIST-FRIENDS</c>).
        /// </summary>
        /// <param name="userNameOrIndex">
        /// The owner, by name or decimal index.
        /// </param>
        /// <returns>
        /// The friends; empty when the user has none.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches.
        /// </exception>
        public IReadOnlyList<NdfsFriendInfo> ListFriends(string userNameOrIndex)
        {
            EnsureInitialized();
            UserEntry owner = RequireUser(userNameOrIndex);

            var list = new List<NdfsFriendInfo>(owner.Friends.Length);
            for (int i = 0; i < owner.Friends.Length; i++)
            {
                UserFriend f = owner.Friends[i];
                if (!f.EntryUsed)
                    continue;

                UserEntry? fu = _userFile.GetUser(f.FriendUserIndex);
                list.Add(new NdfsFriendInfo(f.FriendUserIndex, fu?.UserName ?? string.Empty, f.Bits, f.GetPermissionString()));
            }
            return list;
        }

        /// <summary>
        /// Adds a friend to a user (<c>@CREATE-FRIEND</c>).
        /// </summary>
        /// <param name="userNameOrIndex">
        /// The owner, by name or decimal index.
        /// </param>
        /// <param name="friendNameOrIndex">
        /// The friend, by name or decimal index (0-255).
        /// </param>
        /// <param name="rights">
        /// The friend's rights, one SINTRAN access field; SINTRAN gives a new friend RWA.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the owner or a named friend does not exist, the friend is already on the
        /// list, or the rights are bad.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only, or all eight friend slots are used.
        /// </exception>
        public void AddFriend(string userNameOrIndex, string friendNameOrIndex, string rights = "RWA")
        {
            EnsureWritableForAccess();
            UserEntry owner = RequireUser(userNameOrIndex);
            byte friendIndex = ResolveFriendIndex(friendNameOrIndex);
            byte bits = SintranAccessForm.ParseAccessRights(rights);

            if (owner.IsFriend(friendIndex))
                throw new ArgumentException($"Already a friend of {owner.UserName}: {friendNameOrIndex}", nameof(friendNameOrIndex));
            if (!owner.AddFriend(friendIndex, bits))
                throw new InvalidOperationException($"{owner.UserName} already has 8 friends, the most SINTRAN allows");

            WriteUserEntryNow(owner.UserIndex);
        }

        /// <summary>
        /// Removes a friend from a user (<c>@DELETE-FRIEND</c>).
        /// </summary>
        /// <param name="userNameOrIndex">
        /// The owner, by name or decimal index.
        /// </param>
        /// <param name="friendNameOrIndex">
        /// The friend, by name or decimal index.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the owner does not exist or the friend is not on the list.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only.
        /// </exception>
        public void RemoveFriend(string userNameOrIndex, string friendNameOrIndex)
        {
            EnsureWritableForAccess();
            UserEntry owner = RequireUser(userNameOrIndex);
            byte friendIndex = ResolveFriendIndex(friendNameOrIndex);

            if (!owner.RemoveFriend(friendIndex))
                throw new ArgumentException($"Not a friend of {owner.UserName}: {friendNameOrIndex}", nameof(friendNameOrIndex));

            WriteUserEntryNow(owner.UserIndex);
        }

        /// <summary>
        /// Changes the rights of a friend already on the list (<c>@SET-FRIEND-ACCESS</c>). The
        /// friend keeps its slot; only the rights bits change, and the friend stays on the list even
        /// with <c>N</c>.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// The owner, by name or decimal index.
        /// </param>
        /// <param name="friendNameOrIndex">
        /// The friend, by name or decimal index.
        /// </param>
        /// <param name="rights">
        /// One SINTRAN access field: <c>N</c> or letters RWACD. Empty means no change, as in SINTRAN,
        /// and writes nothing.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the owner does not exist, the friend is not on the list (use
        /// <see cref="AddFriend"/> for a new one), or the rights are bad.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only.
        /// </exception>
        public void SetFriendAccess(string userNameOrIndex, string friendNameOrIndex, string rights)
        {
            EnsureWritableForAccess();
            UserEntry owner = RequireUser(userNameOrIndex);
            byte friendIndex = ResolveFriendIndex(friendNameOrIndex);

            UserFriend? friend = owner.GetFriend(friendIndex);
            if (friend == null)
                throw new ArgumentException($"Not a friend of {owner.UserName}: {friendNameOrIndex}", nameof(friendNameOrIndex));

            if (string.IsNullOrEmpty(rights))
                return;

            friend.SetFriend(friendIndex, SintranAccessForm.ParseAccessRights(rights));
            WriteUserEntryNow(owner.UserIndex);
        }

        /// <summary>
        /// Sets the 15-bit access word of an existing file (<c>@SET-FILE-ACCESS</c>). Use
        /// <see cref="SintranAccessForm.ApplyAccessTriple"/> to build the word from SINTRAN's
        /// <c>PUBLIC,FRIEND,OWN</c> form.
        /// </summary>
        /// <param name="path">
        /// <c>USER/NAME:TYPE</c>.
        /// </param>
        /// <param name="accessBits">
        /// The new access word; bit 15 is ignored.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the file does not exist.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only.
        /// </exception>
        public void SetFileAccess(string path, ushort accessBits)
        {
            EnsureWritableForAccess();
            var (userName, fileName) = ParseNdfsPath(path);
            ObjectEntry obj = FindObjectEntry(userName, fileName)
                ?? throw new FileNotFoundException($"File not found: {path}");

            obj.AccessBits = (uint)(accessBits & 0x7FFF);
            if (_masterBlock?.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device!, _masterBlock.ObjectFilePointer, obj.ObjectIndex);
            _device!.Flush();
        }

        /// <summary>
        /// Gets the access word of an existing file.
        /// </summary>
        /// <param name="path">
        /// <c>USER/NAME:TYPE</c>.
        /// </param>
        /// <returns>
        /// The 15-bit access word.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the file does not exist.
        /// </exception>
        public ushort GetFileAccess(string path)
        {
            EnsureInitialized();
            var (userName, fileName) = ParseNdfsPath(path);
            ObjectEntry obj = FindObjectEntry(userName, fileName)
                ?? throw new FileNotFoundException($"File not found: {path}");
            // The entry holds the word in a wider field; only bits 0-14 are access.
            return (ushort)(obj.AccessBits & 0x7FFF);
        }

        /// <summary>
        /// Sets a user's default file access, the access word a file gets when the user creates it
        /// (user entry offset 40; <c>@SET-DEFAULT-FILE-ACCESS</c>). Existing files keep their own.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// The user, by name or decimal index.
        /// </param>
        /// <param name="accessBits">
        /// The new access word; bit 15 is ignored.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only.
        /// </exception>
        public void SetDefaultFileAccess(string userNameOrIndex, ushort accessBits)
        {
            EnsureWritableForAccess();
            UserEntry user = RequireUser(userNameOrIndex);
            user.DefaultFileAccess = (ushort)(accessBits & 0x7FFF);
            WriteUserEntryNow(user.UserIndex);
        }

        /// <summary>
        /// Gets a user's default file access word.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// The user, by name or decimal index.
        /// </param>
        /// <returns>
        /// The 15-bit access word.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches.
        /// </exception>
        public ushort GetDefaultFileAccess(string userNameOrIndex)
        {
            EnsureInitialized();
            return RequireUser(userNameOrIndex).DefaultFileAccess;
        }

        /// <summary>
        /// The access word a new file gets: its owner's default file access, as SINTRAN does for
        /// indexed and contiguous files alike (measured 2026-11-04 on nd100x).
        /// </summary>
        /// <param name="owner">
        /// The user the file is created for.
        /// </param>
        /// <returns>
        /// The owner's default, or 0x03FF (OWN and FRIEND everything, PUBLIC nothing) when the
        /// user entry holds 0 - the same fallback the norskdata-ndfs ports use.
        /// </returns>
        /// <remarks>
        /// SINTRAN does not do this (measured 2026-11-04 on nd100x): it copies the default as it
        /// is, and refuses <c>@CREATE-FILE</c> with <c>NOT DIRECTORY ACCESS</c> whenever the
        /// default's OWN part has no D - for 0 as well. Keeping the fallback was Ronny's decision
        /// on 2026-11-05, so a tool can still put a file into such a user's area.
        /// </remarks>
        private static uint NewFileAccess(UserEntry owner)
        {
            ushort bits = (ushort)(owner.DefaultFileAccess & 0x7FFF);
            return bits != 0 ? bits : 0x03FFu;
        }

        /// <summary>
        /// Refuses a change on an unmounted or read-only device before anything in memory moves,
        /// so a change that cannot reach the disk never looks made.
        /// </summary>
        private void EnsureWritableForAccess()
        {
            EnsureInitialized();
            if (_device == null || _device.IsReadOnly || _masterBlock == null)
                throw new InvalidOperationException("Filesystem is read-only");
        }

        /// <summary>
        /// Writes the page holding one user entry and flushes the device.
        /// </summary>
        /// <param name="userIndex">
        /// The user index.
        /// </param>
        private void WriteUserEntryNow(int userIndex)
        {
            PersistUserEntry(userIndex);
            _device!.Flush();
        }

        /// <summary>
        /// Resolves a friend reference. A decimal number is taken as a user index as it is (the
        /// friend need not be a named user); a name must belong to a user.
        /// </summary>
        /// <param name="friendNameOrIndex">
        /// The friend, by name or decimal index.
        /// </param>
        /// <returns>
        /// The friend's user index.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for an index above 255 or an unknown name.
        /// </exception>
        private byte ResolveFriendIndex(string friendNameOrIndex)
        {
            if (string.IsNullOrEmpty(friendNameOrIndex))
                throw new ArgumentException("Friend required", nameof(friendNameOrIndex));

            bool digits = true;
            for (int i = 0; i < friendNameOrIndex.Length; i++)
            {
                if (friendNameOrIndex[i] < '0' || friendNameOrIndex[i] > '9')
                {
                    digits = false;
                    break;
                }
            }

            if (digits)
            {
                if (!int.TryParse(friendNameOrIndex, out int index) || index > 255)
                    throw new ArgumentException($"User index out of range: {friendNameOrIndex}", nameof(friendNameOrIndex));
                return (byte)index;
            }

            UserEntry user = _userFile.FindUser(friendNameOrIndex)
                ?? throw new ArgumentException($"User not found: {friendNameOrIndex}", nameof(friendNameOrIndex));
            return (byte)user.UserIndex;
        }
    }
}
