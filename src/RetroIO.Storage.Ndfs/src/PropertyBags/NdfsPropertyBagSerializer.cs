//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using System.Text;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Security;

namespace RetroIO.Storage.Ndfs.PropertyBags
{
    /// <summary>
    /// Serializes and deserializes NDFS metadata to/from property bags.
    /// Enables preservation of NDFS-specific metadata when copying files across filesystems.
    /// </summary>
    public static class NdfsPropertyBagSerializer
    {
        /// <summary>
        /// Populates a property bag with NDFS metadata from an ObjectEntry.
        /// </summary>
        /// <param name="entry">Object entry to serialize</param>
        /// <param name="propertyBag">Property bag to populate</param>
        public static void SerializeObjectEntry(ObjectEntry entry, IDictionary<string, object> propertyBag)
        {
            if (entry == null || propertyBag == null)
                return;

            // Core identification
            propertyBag[NdfsPropertyBagKeys.Header] = entry.Header;
            propertyBag[NdfsPropertyBagKeys.ObjectIndex] = entry.ObjectIndex;
            propertyBag[NdfsPropertyBagKeys.ObjectName] = entry.ObjectName;
            propertyBag[NdfsPropertyBagKeys.Type] = entry.Type;

            // Owner information
            propertyBag[NdfsPropertyBagKeys.UserName] = entry.UserName;
            propertyBag[NdfsPropertyBagKeys.UserIndex] = entry.UserIndex;

            // Versioning
            propertyBag[NdfsPropertyBagKeys.ObjectEntryNextVersion] = entry.ObjectEntryNextVersion;
            propertyBag[NdfsPropertyBagKeys.ObjectEntryPrevVersion] = entry.ObjectEntryPrevVersion;
            propertyBag[NdfsPropertyBagKeys.IsVersioned] =
                (entry.ObjectEntryNextVersion != 0 || entry.ObjectEntryPrevVersion != 0);

            // Access control
            propertyBag[NdfsPropertyBagKeys.AccessBits] = entry.AccessBits;
            var permissions = new AccessPermissions((ushort)entry.AccessBits);
            propertyBag[NdfsPropertyBagKeys.AccessPermissionString] = permissions.ToString();

            // File type
            propertyBag[NdfsPropertyBagKeys.FileTypeFlags] = (ushort)entry.FileTypeFlags;
            propertyBag[NdfsPropertyBagKeys.FileTypeString] = entry.FileTypeFlags.ToLongString();
            propertyBag[NdfsPropertyBagKeys.FileType] = entry.FileType;

            // Device and reservation
            propertyBag[NdfsPropertyBagKeys.DeviceNumber] = entry.DeviceNumber;
            propertyBag[NdfsPropertyBagKeys.UserIndexOfReservingUser] = entry.UserIndexOfReservingUser;

            // Open tracking
            propertyBag[NdfsPropertyBagKeys.CurrentOpenCount] = entry.CurrentOpenCount;
            propertyBag[NdfsPropertyBagKeys.TotalOpenCount] = entry.TotalOpenCount;

            // Timestamps
            propertyBag[NdfsPropertyBagKeys.DateCreated] = entry.DateCreated;
            propertyBag[NdfsPropertyBagKeys.LastDateOpenedForRead] = entry.LastDateOpenedForRead;
            propertyBag[NdfsPropertyBagKeys.LastDateOpenedForWrite] = entry.LastDateOpenedForWrite;

            // Size information
            propertyBag[NdfsPropertyBagKeys.PagesInFile] = entry.PagesInFile;
            propertyBag[NdfsPropertyBagKeys.BytesInFile] = entry.BytesInFile;
        }

        /// <summary>
        /// Restores NDFS metadata from a property bag into an ObjectEntry.
        /// </summary>
        /// <param name="propertyBag">Property bag containing metadata</param>
        /// <param name="entry">Object entry to populate</param>
        public static void DeserializeObjectEntry(IDictionary<string, object> propertyBag, ObjectEntry entry)
        {
            if (propertyBag == null || entry == null)
                return;

            // Core identification
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.Header, out var header))
                entry.Header = Convert.ToUInt32(header);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.ObjectIndex, out var objIdx))
                entry.ObjectIndex = Convert.ToInt32(objIdx);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.ObjectName, out var objName))
                entry.ObjectName = objName?.ToString() ?? string.Empty;

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.Type, out var type))
                entry.Type = type?.ToString() ?? string.Empty;

            // Owner information
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserName, out var userName))
                entry.UserName = userName?.ToString() ?? string.Empty;

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserIndex, out var userIdx))
                entry.UserIndex = Convert.ToInt32(userIdx);

            // Versioning
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.ObjectEntryNextVersion, out var nextVer))
                entry.ObjectEntryNextVersion = Convert.ToUInt32(nextVer);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.ObjectEntryPrevVersion, out var prevVer))
                entry.ObjectEntryPrevVersion = Convert.ToUInt32(prevVer);

            // Access control
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.AccessBits, out var accessBits))
                entry.AccessBits = Convert.ToUInt32(accessBits);

            // File type
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.FileTypeFlags, out var fileTypeFlags))
                entry.FileTypeFlags = (FileTypes)Convert.ToUInt16(fileTypeFlags);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.FileType, out var fileType))
                entry.FileType = Convert.ToInt32(fileType);

            // Device and reservation
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.DeviceNumber, out var deviceNum))
                entry.DeviceNumber = Convert.ToUInt32(deviceNum);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserIndexOfReservingUser, out var resUser))
                entry.UserIndexOfReservingUser = Convert.ToUInt32(resUser);

            // Open tracking
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.CurrentOpenCount, out var curOpen))
                entry.CurrentOpenCount = Convert.ToUInt32(curOpen);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.TotalOpenCount, out var totOpen))
                entry.TotalOpenCount = Convert.ToUInt32(totOpen);

            // Timestamps
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.DateCreated, out var dateCreated))
                entry.DateCreated = Convert.ToUInt32(dateCreated);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.LastDateOpenedForRead, out var lastRead))
                entry.LastDateOpenedForRead = Convert.ToUInt32(lastRead);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.LastDateOpenedForWrite, out var lastWrite))
                entry.LastDateOpenedForWrite = Convert.ToUInt32(lastWrite);

            // Size information
            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.PagesInFile, out var pages))
                entry.PagesInFile = Convert.ToUInt32(pages);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.BytesInFile, out var bytes))
                entry.BytesInFile = Convert.ToUInt64(bytes);
        }

        /// <summary>
        /// Serializes UserEntry friend list to JSON-like string.
        /// Format: "userId:permissions,userId:permissions,..."
        /// </summary>
        private static string SerializeFriendList(UserFriend[] friends)
        {
            if (friends == null || friends.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            bool first = true;

            for (int i = 0; i < friends.Length; i++)
            {
                if (friends[i].EntryUsed)
                {
                    if (!first) sb.Append(',');
                    sb.Append(friends[i].FriendUserIndex);
                    sb.Append(':');
                    sb.Append(friends[i].Bits);
                    first = false;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Deserializes friend list from string.
        /// </summary>
        private static UserFriend[] DeserializeFriendList(string friendsData)
        {
            var friends = new UserFriend[8];
            for (int i = 0; i < 8; i++)
                friends[i] = new UserFriend();

            if (string.IsNullOrEmpty(friendsData))
                return friends;

            var pairs = friendsData.Split(',');
            int friendIdx = 0;

            for (int i = 0; i < pairs.Length && friendIdx < 8; i++)
            {
                var parts = pairs[i].Split(':');
                if (parts.Length == 2)
                {
                    // parts[0] is parsed only to validate the pair, then deliberately discarded.
                    // It is REDUNDANT, not lost: UserFriend.FriendUserIndex is derived as
                    // (Bits & 0xFF), and the constructor that takes a user id stores it in exactly
                    // those low bits. So bits alone reconstructs the friend completely, identity
                    // included. Checked 2026-07-30 after this was reported as a defect; it is not
                    // one, and the id is written out again on the next serialize.
                    //
                    // What this DOES lose is slot position: friends are packed from index 0, so a
                    // friend that sat in slot 5 with 0-4 empty comes back in slot 0. Whether NDFS
                    // attaches meaning to the slot is UNKNOWN, and no test covers it.
                    if (ushort.TryParse(parts[0], out ushort userId) &&
                        ushort.TryParse(parts[1], out ushort bits))
                    {
                        friends[friendIdx] = new UserFriend(bits);
                        friendIdx++;
                    }
                }
            }

            return friends;
        }

        /// <summary>
        /// Populates property bag with UserEntry metadata.
        /// </summary>
        public static void SerializeUserEntry(UserEntry entry, IDictionary<string, object> propertyBag)
        {
            if (entry == null || propertyBag == null)
                return;

            propertyBag[NdfsPropertyBagKeys.UserFlags] = entry.UF;
            propertyBag[NdfsPropertyBagKeys.EnterCount] = entry.EnterCount;
            propertyBag[NdfsPropertyBagKeys.UserIndex] = entry.UserIndex;
            propertyBag[NdfsPropertyBagKeys.UserName] = entry.UserName;
            propertyBag[NdfsPropertyBagKeys.Password] = entry.Password;
            propertyBag[NdfsPropertyBagKeys.UserDateCreated] = entry.DateCreated;
            propertyBag[NdfsPropertyBagKeys.LastDateEntered] = entry.LastDateEntered;
            propertyBag[NdfsPropertyBagKeys.PagesReserved] = entry.PagesReserved;
            propertyBag[NdfsPropertyBagKeys.PagesUsed] = entry.PagesUsed;
            propertyBag[NdfsPropertyBagKeys.DirectoryIndex] = entry.DirectoryIndex;
            propertyBag[NdfsPropertyBagKeys.DefaultFileAccess] = entry.DefaultFileAccess;
            propertyBag[NdfsPropertyBagKeys.Friends] = SerializeFriendList(entry.Friends);
        }

        /// <summary>
        /// Restores UserEntry from property bag.
        /// </summary>
        public static void DeserializeUserEntry(IDictionary<string, object> propertyBag, UserEntry entry)
        {
            if (propertyBag == null || entry == null)
                return;

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserFlags, out var uf))
                entry.UF = Convert.ToByte(uf);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.EnterCount, out var ec))
                entry.EnterCount = Convert.ToByte(ec);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserIndex, out var ui))
                entry.UserIndex = Convert.ToInt32(ui);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserName, out var un))
                entry.UserName = un?.ToString() ?? string.Empty;

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.Password, out var pw))
                entry.Password = Convert.ToUInt16(pw);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.UserDateCreated, out var dc))
                entry.DateCreated = Convert.ToUInt32(dc);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.LastDateEntered, out var lde))
                entry.LastDateEntered = Convert.ToUInt32(lde);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.PagesReserved, out var pr))
                entry.PagesReserved = Convert.ToUInt32(pr);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.PagesUsed, out var pu))
                entry.PagesUsed = Convert.ToUInt32(pu);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.DirectoryIndex, out var di))
                entry.DirectoryIndex = Convert.ToByte(di);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.DefaultFileAccess, out var dfa))
                entry.DefaultFileAccess = Convert.ToUInt16(dfa);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.Friends, out var friends))
            {
                var friendsStr = friends?.ToString() ?? string.Empty;
                entry.Friends = DeserializeFriendList(friendsStr);
            }
        }

        /// <summary>
        /// Applies just the metadata fields that are safe to restore onto an ObjectEntry
        /// independently of the write that produced it: access permissions and the three
        /// historical timestamps (created, last read, last write — all equally portable, none of
        /// them tied to the physical allocation of THIS write). Deliberately does NOT touch
        /// structural/allocation fields (pages, bytes, file pointer, object index, owner/user
        /// index, open counts, versioning, device number) — those must always reflect the write
        /// that actually just happened, not a property bag that may have come from a different
        /// file (e.g. a re-imported ".xat" sidecar written on a previous export, being merged into
        /// a brand new write). NDFS also structurally ties file ownership to the object-table slot
        /// the file physically occupies (assigned when the object is created, based on the
        /// destination directory's user), not to a freeform property value — UserName/UserIndex
        /// are intentionally excluded rather than being (impossibly) reassigned after the fact.
        /// See NdfsFileSystem.WriteFileWithProperties, the sole caller.
        ///
        /// Regression note: LastDateOpenedForRead/LastDateOpenedForWrite were originally omitted
        /// here by oversight (not a deliberate safety exclusion, unlike the structural fields
        /// above) — a real re-imported .xat sidecar containing all three date fields only had
        /// DateCreated actually restored. See NdfsPropertyBagSerializerApplyPortableMetadataTests.
        /// </summary>
        public static void ApplyPortableMetadata(IDictionary<string, object> propertyBag, ObjectEntry entry)
        {
            if (propertyBag == null || entry == null)
                return;

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.AccessBits, out var accessBits))
                entry.AccessBits = Convert.ToUInt32(accessBits);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.DateCreated, out var dateCreated))
                entry.DateCreated = Convert.ToUInt32(dateCreated);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.LastDateOpenedForRead, out var lastRead))
                entry.LastDateOpenedForRead = Convert.ToUInt32(lastRead);

            if (propertyBag.TryGetValue(NdfsPropertyBagKeys.LastDateOpenedForWrite, out var lastWrite))
                entry.LastDateOpenedForWrite = Convert.ToUInt32(lastWrite);
        }

        /// <summary>
        /// Checks if property bag contains NDFS metadata.
        /// </summary>
        public static bool HasNdfsMetadata(IDictionary<string, object> propertyBag)
        {
            if (propertyBag == null)
                return false;

            // Check for at least one NDFS property
            foreach (var key in propertyBag.Keys)
            {
                if (NdfsPropertyBagKeys.IsNdfsProperty(key))
                    return true;
            }

            return false;
        }
    }
}
