//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// The rules of a room - who is in it, what names are free, and what everybody should be told -
    /// with no transport of any kind.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the rules were pulled out of <see cref="ChatServer"/></b></para>
    /// <para>
    /// There are two ways into a room. One is a port-to-port conversation, which is what
    /// <see cref="ChatServer"/> serves. The other is somebody sitting at a SINTRAN terminal that
    /// has connected to this node, where there is no port, no magic number and no message to
    /// decode - just typed lines and text pushed back to a screen.
    /// </para>
    /// <para>
    /// Those two need identical rules and completely different plumbing. Written twice they would
    /// drift, and the drift would be in the awkward cases - a duplicate nickname, a rename that
    /// collides, who is told when somebody leaves. So the rules live here once and each transport
    /// carries them.
    /// </para>
    /// <para><b>A member is an opaque number</b></para>
    /// <para>
    /// Whoever owns the transport chooses what it means: the port conversation keys off the
    /// client's magic number, the terminal off its tty number. This class only needs to tell
    /// members apart.
    /// </para>
    /// <para><b>Seats are NOT counted here</b></para>
    /// <para>
    /// A room's capacity is enforced by XROUT's free-connection count on the port path, before a
    /// join ever reaches any of this code. Counting seats here as well would be a second, quietly
    /// different answer to "is the room full". The terminal path has no XROUT in front of it and
    /// so has no limit, which is a real difference and is stated rather than papered over.
    /// </para>
    /// </remarks>
    public sealed class ChatRoom
    {
        private readonly List<Occupant> _members;

        /// <summary>
        /// Creates an empty room.
        /// </summary>
        public ChatRoom()
        {
            _members = new List<Occupant>();
        }

        /// <summary>
        /// Gets how many members are in the room.
        /// </summary>
        public int Count
        {
            get { return _members.Count; }
        }

        /// <summary>
        /// The longest nickname the room will accept.
        /// </summary>
        /// <remarks>
        /// <para>
        /// SIXTEEN because that is what the SINTRAN server has room for: CHATSV.PLNC stores names
        /// in <c>memberName(1:8, 1:16)</c>, a fixed sixteen bytes per seat, and PLANC checks no
        /// array bound. A longer name is not a cosmetic difference there - copying it in writes
        /// through the other seats and out of the array.
        /// </para>
        /// <para>
        /// So the limit is a rule of the room, enforced here where the rules live, rather than an
        /// accident of one implementation's storage. Without it this side would happily admit a
        /// twenty-character name that the other side must drop, and the two rooms would disagree
        /// about who is in them.
        /// </para>
        /// </remarks>
        public const int MaxNicknameLength = 16;

        /// <summary>
        /// Admits a member.
        /// </summary>
        /// <param name="id">
        /// The transport's handle for this member.
        /// </param>
        /// <param name="nickname">
        /// The name they want to be known by.
        /// </param>
        /// <param name="refusal">
        /// Why they were refused, or an empty string when they were admitted.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the member is now in the room.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="nickname"/> is null.
        /// </exception>
        public bool TryJoin(long id, string nickname, out string refusal)
        {
            if (nickname == null) { throw new ArgumentNullException(nameof(nickname)); }

            if (nickname.Length == 0)
            {
                refusal = "a nickname is required";
                return false;
            }

            if (nickname.Length > MaxNicknameLength)
            {
                refusal = "a nickname is at most 16 characters";
                return false;
            }

            if (IndexOfNickname(nickname) >= 0)
            {
                refusal = "that nickname is taken";
                return false;
            }

            if (IndexOf(id) >= 0)
            {
                refusal = "already joined";
                return false;
            }

            _members.Add(new Occupant(id, nickname));
            refusal = string.Empty;
            return true;
        }

        /// <summary>
        /// Renames a member.
        /// </summary>
        /// <param name="id">
        /// The member asking.
        /// </param>
        /// <param name="nickname">
        /// The name they want.
        /// </param>
        /// <param name="previous">
        /// The name they had, when this returns <see langword="true"/>.
        /// </param>
        /// <param name="refusal">
        /// Why it was refused, or empty when it was allowed or was a no-op.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the name changed and the room should be told.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="nickname"/> is null.
        /// </exception>
        /// <remarks>
        /// Asking for the name you already have returns <see langword="false"/> with NO refusal:
        /// nothing changed, and it is not an error either. The caller can tell the two apart by
        /// whether <paramref name="refusal"/> is empty.
        /// </remarks>
        public bool TryRename(long id, string nickname, out string previous, out string refusal)
        {
            if (nickname == null) { throw new ArgumentNullException(nameof(nickname)); }

            previous = string.Empty;

            int index = IndexOf(id);
            if (index < 0)
            {
                refusal = "you are not in the room";
                return false;
            }

            if (nickname.Length == 0)
            {
                refusal = "a nickname is required";
                return false;
            }

            if (nickname.Length > MaxNicknameLength)
            {
                refusal = "a nickname is at most 16 characters";
                return false;
            }

            string current = _members[index].Nickname;
            if (string.Equals(nickname, current, StringComparison.OrdinalIgnoreCase))
            {
                // Not an error and not news.
                refusal = string.Empty;
                return false;
            }

            if (IndexOfNickname(nickname) >= 0)
            {
                refusal = "that nickname is taken";
                return false;
            }

            _members[index] = new Occupant(id, nickname);
            previous = current;
            refusal = string.Empty;
            return true;
        }

        /// <summary>
        /// Removes a member.
        /// </summary>
        /// <param name="id">
        /// The member leaving.
        /// </param>
        /// <param name="nickname">
        /// The name they were known by, when this returns <see langword="true"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when somebody actually left and the room should be told.
        /// </returns>
        public bool TryLeave(long id, out string nickname)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                nickname = string.Empty;
                return false;
            }

            nickname = _members[index].Nickname;
            _members.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Reports whether a member is in the room.
        /// </summary>
        /// <param name="id">
        /// The member handle to look for.
        /// </param>
        /// <returns>
        /// True when that handle is currently seated.
        /// </returns>
        /// <remarks>
        /// Exists so seat accounting can ask the plain question without inventing a nickname it has
        /// no use for - the answer is what decides whether a forwarded letter keeps its seat.
        /// </remarks>
        public bool Contains(long id)
        {
            return IndexOf(id) >= 0;
        }

        /// <summary>
        /// Gets the name a member is known by.
        /// </summary>
        /// <param name="id">
        /// The member.
        /// </param>
        /// <param name="nickname">
        /// Their name, or an empty string when they are not in the room.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when they are in the room.
        /// </returns>
        public bool TryGetNickname(long id, out string nickname)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                nickname = string.Empty;
                return false;
            }

            nickname = _members[index].Nickname;
            return true;
        }

        /// <summary>
        /// Copies out every member's handle, so the caller can tell each of them something.
        /// </summary>
        /// <returns>
        /// The handles, in join order. A fresh array each time, so a caller may remove members
        /// while walking it.
        /// </returns>
        public long[] CopyMemberIds()
        {
            long[] ids = new long[_members.Count];
            for (int i = 0; i < _members.Count; i++)
            {
                ids[i] = _members[i].Id;
            }

            return ids;
        }

        /// <summary>
        /// Copies out every member's name, for a "who is here" reply.
        /// </summary>
        /// <returns>
        /// The names, in join order.
        /// </returns>
        public string[] CopyNicknames()
        {
            string[] names = new string[_members.Count];
            for (int i = 0; i < _members.Count; i++)
            {
                names[i] = _members[i].Nickname;
            }

            return names;
        }

        /// <summary>
        /// Finds a member by handle.
        /// </summary>
        /// <param name="id">
        /// The handle.
        /// </param>
        /// <returns>
        /// The index, or -1.
        /// </returns>
        private int IndexOf(long id)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Finds a member by name, ignoring case.
        /// </summary>
        /// <param name="nickname">
        /// The name.
        /// </param>
        /// <returns>
        /// The index, or -1.
        /// </returns>
        private int IndexOfNickname(string nickname)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (string.Equals(_members[i].Nickname, nickname, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// One person in the room.
        /// </summary>
        private readonly struct Occupant
        {
            internal Occupant(long id, string nickname)
            {
                Id = id;
                Nickname = nickname;
            }

            internal long Id { get; }

            internal string Nickname { get; }
        }
    }
}
