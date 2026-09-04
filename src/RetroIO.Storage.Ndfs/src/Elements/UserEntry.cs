//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// Represents a user entry in the NDFS user file.
    /// Each user has a name, quota, usage information, authentication, and friend access control.
    /// </summary>
    public class UserEntry
    {
        /// <summary>
        /// The user entry flags byte, byte 0 of the on-disk entry.
        /// </summary>
        /// <value>
        /// The whole flags byte as read from disk, not just the bits this class models.
        /// </value>
        /// <remarks>
        /// Modelled bits:
        ///  - Bit 7 — entry used (U).
        ///  - Bit 0 — user/object entry discriminator, set for a user entry.
        /// Any other bit is unknown to this class and is preserved rather than interpreted; see
        /// <see cref="UfValidUser"/>.
        /// </remarks>
        public byte UF { get; set; } = UfValidUser;

        /// <summary>
        /// The two bits that mark byte 0 as a valid user entry.
        /// </summary>
        /// <remarks>
        /// <para><b>Value</b></para>
        /// <c>0x81</c> — bit 7 "entry used" plus bit 0 "this is a user entry, not an object entry".
        /// <para><b>Why the whole byte round-trips</b></para>
        /// Use this constant when building an entry from scratch. <see cref="FromBytes"/> accepts any
        /// byte 0 whose <c>0x81</c> bits are both set, so a real pack may carry additional bits that
        /// this class does not model. <see cref="ToBytes"/> therefore writes <see cref="UF"/> back
        /// verbatim rather than re-hardcoding <c>0x81</c>, which would destroy those bits on every
        /// read/modify/write cycle — the same defect, and the same fix, as
        /// <see cref="ObjectEntry.Header"/>.
        /// </remarks>
        public const byte UfValidUser = 0x81;

        /// <summary>
        /// Number of times user has logged in.
        /// </summary>
        public byte EnterCount { get; set; }

        /// <summary>
        /// User index (0-based).
        /// </summary>
        public int UserIndex { get; set; }

        /// <summary>
        /// User name (max 16 characters).
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Password hash (16-bit).
        /// Note: In production, use proper password hashing (e.g., bcrypt).
        /// </summary>
        public ushort Password { get; set; }

        /// <summary>
        /// Date user was created (ND date format).
        /// </summary>
        public uint DateCreated { get; set; }

        /// <summary>
        /// Last date user logged in (ND date format).
        /// </summary>
        public uint LastDateEntered { get; set; }

        /// <summary>
        /// Number of pages reserved for this user (quota).
        /// </summary>
        public uint PagesReserved { get; set; }

        /// <summary>
        /// Number of pages currently used by this user.
        /// </summary>
        public uint PagesUsed { get; set; }

        /// <summary>
        /// Directory index for multi-directory support.
        /// </summary>
        public byte DirectoryIndex { get; set; }

        /// <summary>
        /// Default file access permissions for files created by this user.
        /// Default: 0x4FF (owner full, friends read/write, public read)
        /// </summary>
        public ushort DefaultFileAccess { get; set; } = 0x4FF;

        /// <summary>
        /// Gets or sets MXOBL - the maximum number of object blocks this user area may have (1-16).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A SINTRAN user area holds <b>256 files per object block</b>: one block by default, and up
        /// to 16 blocks (4096 files) on version K. The operator raises the ceiling with
        /// <c>@GIVE-OBJECT-BLOCKS</c> and reads it back from <c>@USER-STATISTICS</c>, which prints
        /// it as <c>MAXIMUM NUMBER OF FILES</c> - that value is exactly this property times 256.
        /// </para>
        /// <para>
        /// Stored on disk in the <b>high nibble of byte 47, zero-based</b> (a default user stores
        /// <c>0x00</c> meaning one block). VERIFIED 2026-08-01 against a live SINTRAN III K pack
        /// with a control group: user BIGMAN, given 3 extra blocks and holding 482 files, stores
        /// <c>0x31</c>, giving MXOBL 4 which matches its reported maximum of 1024 files; every
        /// other user on the same pack stores <c>0x00</c>.
        /// </para>
        /// <para>
        /// Doc: <c>NDInsight SINTRAN/XMSG/DOC/NDFS-OBJECT-BLOCKS-DECODED-2026-08-01.md</c>.
        /// </para>
        /// </remarks>
        public int MaxObjectBlocks { get; set; } = 1;

        /// <summary>
        /// Gets or sets ACOBL - the number of object blocks actually allocated to this user so far
        /// (1 to <see cref="MaxObjectBlocks"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// SINTRAN allocates a further object block on demand when the allocated ones are full, so
        /// this can be lower than <see cref="MaxObjectBlocks"/>. On the verified pack BIGMAN reads
        /// ACOBL 2, matching its 482 files - two blocks of 256.
        /// </para>
        /// <para>
        /// Stored in the <b>low nibble of byte 47, zero-based</b>.
        /// </para>
        /// <para>
        /// This value bounds where a user's entries live. Object block <c>n</c> (0-based) for user
        /// <c>U</c> occupies object-file pages <c>n*512 + U*8</c> through <c>n*512 + U*8 + 7</c>, so
        /// a reader must walk every allocated block, not just the first.
        /// </para>
        /// </remarks>
        public int AllocatedObjectBlocks { get; set; } = 1;

        /// <summary>
        /// Array of up to 8 friends with specific access permissions.
        /// </summary>
        public UserFriend[] Friends { get; set; }

        /// <summary>
        /// Legacy friend indices list (kept for compatibility).
        /// </summary>
        public List<int> FriendIndices { get; set; } = new List<int>();

        /// <summary>
        /// Verbatim on-disk 64 bytes, used as the base when re-serializing so
        /// bytes not explicitly modelled survive a round trip. Null for new entries.
        /// </summary>
        private byte[]? _raw;

        /// <summary>
        /// Creates a new user entry with default values.
        /// </summary>
        public UserEntry()
        {
            Friends = new UserFriend[8];
            for (int i = 0; i < 8; i++)
                Friends[i] = new UserFriend();
        }

        /// <summary>
        /// Parses a user entry from raw bytes.
        /// </summary>
        /// <remarks>
        /// When at least 64 bytes are available the original on-disk bytes are retained, so bytes
        /// this class does not model survive a later <see cref="ToBytes"/> call.
        /// </remarks>
        /// <param name="data">
        /// Buffer holding the raw entry.
        /// </param>
        /// <param name="offset">
        /// Byte position in <paramref name="data"/> at which the entry starts.
        /// </param>
        /// <returns>
        /// The parsed entry, or <c>null</c> when the slot is free — that is, when byte 0 does not
        /// have both <see cref="UfValidUser"/> bits set.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is null, or holds fewer than 32 bytes from
        /// <paramref name="offset"/>.
        /// </exception>
        public static UserEntry? FromBytes(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 32)
                throw new ArgumentException("Insufficient data for user entry");

            var entry = new UserEntry();
            if (data.Length >= offset + 64)
            {
                entry._raw = new byte[64];
                Array.Copy(data, offset, entry._raw, 0, 64);
            }

            // Based on C code:
            // Byte 0: 0x81 indicates valid user entry
            // Bytes 2-17: User name (terminated by ')
            // Byte 37: User index
            // Bytes 28-31: Pages reserved (big-endian)
            // Bytes 32-35: Pages used (big-endian)

            // Check if valid user entry
            if ((data[offset] & UfValidUser) != UfValidUser)
                return null;

            // Keep the WHOLE flags byte, not just the 0x81 bits we test on. Anything else set here
            // is a flag this class does not model yet, and ToBytes writes it straight back out.
            entry.UF = data[offset];

            // Read user name (16 bytes at offset+2, terminated by ')
            int nameLength = 0;
            for (int i = 0; i < 16; i++)
            {
                if (data[offset + 2 + i] == 0x27) break; // ' is the terminator
                if (data[offset + 2 + i] == 0) break;
                nameLength++;
            }
            entry.UserName = Encoding.ASCII.GetString(data, offset + 2, nameLength);

            // Password (bytes 18-19, big-endian 16-bit)
            entry.Password = (ushort)((data[offset + 18] << 8) | data[offset + 19]);

            // User index (byte 37)
            entry.UserIndex = data[offset + 37];

            // Pages reserved (bytes 28-31, big-endian)
            entry.PagesReserved = (uint)((data[offset + 28] << 24) |
                                         (data[offset + 29] << 16) |
                                         (data[offset + 30] << 8) |
                                         data[offset + 31]);

            // Pages used (bytes 32-35, big-endian)
            entry.PagesUsed = (uint)((data[offset + 32] << 24) |
                                     (data[offset + 33] << 16) |
                                     (data[offset + 34] << 8) |
                                     data[offset + 35]);

            entry.EnterCount = data[offset + 1];
            entry.DirectoryIndex = data[offset + 36];

            // Dates (bytes 20-23, 24-27, big-endian ND timestamps)
            entry.DateCreated = (uint)((data[offset + 20] << 24) | (data[offset + 21] << 16) |
                                       (data[offset + 22] << 8) | data[offset + 23]);
            entry.LastDateEntered = (uint)((data[offset + 24] << 24) | (data[offset + 25] << 16) |
                                           (data[offset + 26] << 8) | data[offset + 27]);

            // Default file access at offset 40 (verified on real images; offset
            // 38-39 is unused), then 8 friends of 2 bytes each at offset 48.
            entry.DefaultFileAccess = (ushort)((data[offset + 40] << 8) | data[offset + 41]);

            // Byte 47: MXOBL/ACOBL, two ZERO-BASED nibbles. A default user stores 0x00, meaning one
            // block max and one allocated = 256 files, so +1 on both halves turns the raw byte into
            // usable counts. Before 2026-08-01 this byte was never parsed and every consumer assumed
            // 256 files per user, which silently mis-numbered any file in a second object block.
            byte objectBlocks = data[offset + 47];
            entry.MaxObjectBlocks = (objectBlocks >> 4) + 1;
            entry.AllocatedObjectBlocks = (objectBlocks & 0x0F) + 1;
            for (int i = 0; i < 8; i++)
                entry.Friends[i] = UserFriend.FromBytes(data, offset + 48 + i * 2);

            return entry;
        }

        /// <summary>
        /// Converts the user entry to its 64-byte on-disk form.
        /// </summary>
        /// <remarks>
        /// When the entry came from <see cref="FromBytes"/> the original on-disk bytes are copied
        /// first, so bytes this class does not model still survive the round trip. Only the modelled
        /// fields are then written over the top. A caller that built the entry from scratch must have
        /// set <see cref="UF"/> — see <see cref="UfValidUser"/>.
        /// </remarks>
        /// <returns>
        /// A new 64-byte buffer holding the serialized entry.
        /// </returns>
        public byte[] ToBytes()
        {
            // Base on the original on-disk bytes when available so unmodelled
            // bytes survive; otherwise start zeroed.
            var buffer = new byte[64];
            if (_raw != null && _raw.Length == 64)
                Array.Copy(_raw, 0, buffer, 0, 64);

            // Byte 0: the flags byte, written verbatim so unmodelled bits survive a
            // read/modify/write. This used to be a hardcoded 0x81, which silently cleared every
            // other bit in the byte on any entry we rewrote (e.g. the password-clear path).
            // Callers building an entry from scratch must set UF (see UfValidUser) - a zero byte 0
            // makes FromBytes reject the slot and the user disappears.
            buffer[0] = UF;
            buffer[1] = EnterCount;

            // Bytes 2-17: User name + a single 0x27 terminator then NULs.
            for (int i = 2; i < 18; i++) buffer[i] = 0;
            var nameBytes = Encoding.ASCII.GetBytes(UserName);
            int copyLength = Math.Min(nameBytes.Length, 16);
            Array.Copy(nameBytes, 0, buffer, 2, copyLength);
            if (copyLength < 16)
                buffer[2 + copyLength] = 0x27;

            // Bytes 18-19: Password (big-endian 16-bit)
            buffer[18] = (byte)(Password >> 8);
            buffer[19] = (byte)Password;

            // Bytes 20-27: dates (big-endian ND timestamps)
            buffer[20] = (byte)(DateCreated >> 24); buffer[21] = (byte)(DateCreated >> 16);
            buffer[22] = (byte)(DateCreated >> 8); buffer[23] = (byte)DateCreated;
            buffer[24] = (byte)(LastDateEntered >> 24); buffer[25] = (byte)(LastDateEntered >> 16);
            buffer[26] = (byte)(LastDateEntered >> 8); buffer[27] = (byte)LastDateEntered;

            // Bytes 28-31: Pages reserved (big-endian)
            buffer[28] = (byte)(PagesReserved >> 24);
            buffer[29] = (byte)(PagesReserved >> 16);
            buffer[30] = (byte)(PagesReserved >> 8);
            buffer[31] = (byte)PagesReserved;

            // Bytes 32-35: Pages used (big-endian)
            buffer[32] = (byte)(PagesUsed >> 24);
            buffer[33] = (byte)(PagesUsed >> 16);
            buffer[34] = (byte)(PagesUsed >> 8);
            buffer[35] = (byte)PagesUsed;

            // Byte 36: directory index; byte 37: user index
            buffer[36] = DirectoryIndex;
            buffer[37] = (byte)UserIndex;

            // Bytes 40-41: default file access; bytes 48-63: 8 friends (2 bytes each).
            buffer[40] = (byte)(DefaultFileAccess >> 8);
            buffer[41] = (byte)DefaultFileAccess;

            // Byte 47: MXOBL/ACOBL back to zero-based nibbles. Clamped to 1..16 so a caller cannot
            // write a count SINTRAN could not express - 16 blocks of 256 files is the version-K
            // ceiling of 4096.
            int maxBlocks = MaxObjectBlocks < 1 ? 1 : (MaxObjectBlocks > 16 ? 16 : MaxObjectBlocks);
            int allocatedBlocks = AllocatedObjectBlocks < 1 ? 1 : AllocatedObjectBlocks;
            if (allocatedBlocks > maxBlocks)
            {
                allocatedBlocks = maxBlocks;
            }

            buffer[47] = (byte)(((maxBlocks - 1) << 4) | (allocatedBlocks - 1));
            for (int i = 0; i < 8 && i < Friends.Length; i++)
            {
                var fb = Friends[i].ToBytes();
                if (fb != null && fb.Length >= 2)
                {
                    buffer[48 + i * 2] = fb[0];
                    buffer[48 + i * 2 + 1] = fb[1];
                }
            }

            return buffer;
        }

        /// <summary>
        /// Checks if the user has exceeded their quota.
        /// </summary>
        public bool IsOverQuota()
        {
            return PagesUsed > PagesReserved;
        }

        /// <summary>
        /// Gets the number of free pages remaining in the user's quota.
        /// </summary>
        public int GetFreePages()
        {
            return (int)PagesReserved - (int)PagesUsed;
        }

        /// <summary>
        /// Checks if provided password matches stored password hash.
        /// Note: This is a simple implementation. Production should use proper hashing.
        /// </summary>
        /// <param name="password">Password to check</param>
        /// <returns>True if password matches</returns>
        public bool CheckPassword(ushort password)
        {
            return Password == password;
        }

        /// <summary>
        /// Sets the user name (max 16 characters, uppercase).
        /// </summary>
        /// <param name="userName">User name to set</param>
        public void SetName(string userName)
        {
            if (string.IsNullOrEmpty(userName))
                throw new ArgumentException("User name cannot be empty");

            UserName = userName.ToUpper().Trim();
            if (UserName.Length > 16)
                UserName = UserName.Substring(0, 16);
        }

        /// <summary>
        /// Checks if a user is in this user's friend list.
        /// </summary>
        /// <param name="userId">User index to check</param>
        /// <returns>True if user is a friend</returns>
        public bool IsFriend(int userId)
        {
            for (int i = 0; i < Friends.Length; i++)
            {
                if (Friends[i].EntryUsed && Friends[i].FriendUserIndex == userId)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Adds a friend to this user's friend list.
        /// </summary>
        /// <param name="friendId">Friend's user index</param>
        /// <param name="permissions">Permission bits</param>
        /// <returns>True if added successfully</returns>
        public bool AddFriend(byte friendId, byte permissions)
        {
            // Find first empty slot
            for (int i = 0; i < Friends.Length; i++)
            {
                if (!Friends[i].EntryUsed)
                {
                    Friends[i].SetFriend(friendId, permissions);
                    return true;
                }
            }
            return false; // No empty slots
        }

        /// <summary>
        /// Removes a friend from this user's friend list.
        /// </summary>
        /// <param name="friendId">Friend's user index</param>
        /// <returns>True if removed successfully</returns>
        public bool RemoveFriend(int friendId)
        {
            for (int i = 0; i < Friends.Length; i++)
            {
                if (Friends[i].EntryUsed && Friends[i].FriendUserIndex == friendId)
                {
                    Friends[i].Clear();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gets friend entry for specified user.
        /// </summary>
        /// <param name="friendId">Friend's user index</param>
        /// <returns>UserFriend entry or null if not found</returns>
        public UserFriend? GetFriend(int friendId)
        {
            for (int i = 0; i < Friends.Length; i++)
            {
                if (Friends[i].EntryUsed && Friends[i].FriendUserIndex == friendId)
                    return Friends[i];
            }
            return null;
        }

        /// <summary>
        /// Increments the login counter and updates last entered date.
        /// </summary>
        /// <param name="currentDate">Current ND date</param>
        public void RecordLogin(uint currentDate)
        {
            if (EnterCount < 255)
                EnterCount++;
            LastDateEntered = currentDate;
        }
    }
}
