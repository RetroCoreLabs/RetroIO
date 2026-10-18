//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Api;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// One user in a chat room: joins a service by name, then talks to it directly.
    /// </summary>
    /// <remarks>
    /// <para><b>The client never learns an address in advance</b></para>
    /// It writes to a NAME. XROUT forwards that letter and tells the client nothing about where it
    /// went - that is the whole point of the letterbox model. The server's address arrives with the
    /// welcome, and only then can the client speak to it directly. A client that is refused, or
    /// that writes to a full room, never learns anything about the server at all.
    /// <para><b>No blocking</b></para>
    /// <see cref="Poll"/> returns what has arrived and nothing more. There is no scheduler to
    /// suspend on, so a caller drives this from its own loop.
    /// </remarks>
    public sealed class ChatClient
    {
        private readonly XmsgKernel _kernel;
        private readonly XroutDirectory _directory;
        // NOT readonly: a rename the server accepted changes it - see Poll. The chosen name at
        // construction is only the opening bid.
        private string _nickname;
        private readonly byte[] _scratch;

        private XmsgPortNumber _port;
        private XmsgMagicNumber _magic;
        private XmsgMagicNumber _server;
        private bool _hasPort;
        private bool _joined;

        /// <summary>
        /// Initialises a chat client on a kernel and a name table.
        /// </summary>
        /// <param name="kernel">
        /// The kernel this user's port belongs to.
        /// </param>
        /// <param name="directory">
        /// The XROUT stand-in used to post the join letter.
        /// </param>
        /// <param name="nickname">
        /// The name to be known by in the room.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="nickname"/> is empty.
        /// </exception>
        public ChatClient(XmsgKernel kernel, XroutDirectory directory, string nickname)
        {
            if (kernel == null)
            {
                throw new ArgumentNullException(nameof(kernel));
            }

            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (nickname == null)
            {
                throw new ArgumentNullException(nameof(nickname));
            }

            if (nickname.Length == 0)
            {
                throw new ArgumentException("A nickname is required.", nameof(nickname));
            }

            _kernel = kernel;
            _directory = directory;
            _nickname = nickname;
            _scratch = new byte[1024];
        }

        /// <summary>
        /// Gets the nickname this client joined under.
        /// </summary>
        public string Nickname
        {
            get { return _nickname; }
        }

        /// <summary>
        /// Gets a value indicating whether the server has welcomed this client.
        /// </summary>
        public bool IsJoined
        {
            get { return _joined; }
        }

        /// <summary>
        /// Opens a port and posts a join letter to a named room.
        /// </summary>
        /// <param name="roomName">
        /// The registered name of the chat service.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/> when the letter was forwarded,
        /// <see cref="XroutError.XRUNN"/> when no such name is registered, or
        /// <see cref="XroutError.XRNSP"/> when the room is full.
        /// </returns>
        /// <remarks>
        /// A successful return means the letter reached the server, NOT that you are in the room.
        /// The server still decides; <see cref="Poll"/> brings back the welcome or the refusal.
        /// </remarks>
        public XroutError Join(string roomName)
        {
            return Join(roomName, string.Empty);
        }

        /// <summary>
        /// Opens a port and posts a join letter to the chat service, naming a room.
        /// </summary>
        /// <param name="serviceName">
        /// The registered name of the chat service, which is <c>*CHAT</c>.
        /// </param>
        /// <param name="room">
        /// The room to join. Empty means the server's default room.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/> when the letter was forwarded,
        /// <see cref="XroutError.XRUNN"/> when no such name is registered, or
        /// <see cref="XroutError.XRNSP"/> when the service is full.
        /// </returns>
        /// <remarks>
        /// <para><b>The room travels in the Join's TEXT field, not in the name</b></para>
        /// It used to be the name: every room registered its own XROUT entry, <c>CHAT-LOBBY</c> and
        /// the like, and you joined a room by addressing it. That is retired on both sides. One
        /// service name, and the room named inside the message - which cost no wire change at all,
        /// because a Join already carried an empty text field.
        /// <para>
        /// A successful return means the letter reached the server, NOT that you are in the room.
        /// The server still decides; <see cref="Poll"/> brings back the welcome or the refusal.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serviceName"/> or <paramref name="room"/> is null.
        /// </exception>
        public XroutError Join(string serviceName, string room)
        {
            if (serviceName == null)
            {
                throw new ArgumentNullException(nameof(serviceName));
            }

            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }

            string roomName = serviceName;

            if (!_hasPort)
            {
                XmsgStatus opened = _kernel.OpenPort(out _port);
                if (opened.IsError)
                {
                    return XroutError.XRNSP;
                }

                XmsgStatus converted = _kernel.ConvertPortToMagic(_port, out _magic);
                if (converted.IsError)
                {
                    return XroutError.XRUNM;
                }

                _hasPort = true;
            }

            ChatMessage join = new ChatMessage(ChatMessageKind.Join, _nickname, room);
            byte[] bytes = new byte[join.ByteCount];
            join.Encode(bytes);

            return _directory.SendLetter(roomName, _magic, bytes);
        }

        /// <summary>
        /// Says something to the room.
        /// </summary>
        /// <param name="text">
        /// The line to say.
        /// </param>
        /// <returns>
        /// True when the line was sent; false when this client has not been welcomed yet.
        /// </returns>
        public bool Say(string text)
        {
            if (!_joined)
            {
                return false;
            }

            SendToServer(new ChatMessage(ChatMessageKind.Say, _nickname, text));
            return true;
        }

        /// <summary>
        /// Asks to be known by a different name from now on.
        /// </summary>
        /// <param name="nickname">
        /// The wanted name.
        /// </param>
        /// <returns>
        /// <c>true</c> when the request was sent. NOT whether it was accepted - the
        /// server decides, and says so with a <see cref="ChatMessageKind.Renamed"/> everybody sees
        /// or a <see cref="ChatMessageKind.Reject"/> to the asker.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="nickname"/> is null.
        /// </exception>
        /// <remarks>
        /// The local nickname is NOT changed here. It is changed when the server's answer arrives,
        /// in <see cref="Poll"/> - otherwise a refused rename would leave this client calling
        /// itself something the room does not know it by, and every later line would be attributed
        /// wrongly on this screen alone.
        /// </remarks>
        public bool Rename(string nickname)
        {
            if (nickname == null)
            {
                throw new ArgumentNullException(nameof(nickname));
            }

            if (!_joined)
            {
                return false;
            }

            SendToServer(new ChatMessage(ChatMessageKind.Rename, nickname, string.Empty));
            return true;
        }

        /// <summary>
        /// Asks the room who is in it.
        /// </summary>
        /// <returns>
        /// True when the question was sent; false when this client has not joined a room.
        /// </returns>
        /// <remarks>
        /// The answer comes back as an ordinary arrival of kind <see cref="ChatMessageKind.Who"/>
        /// carrying the names in its text, separated by single spaces - it is not returned from
        /// here, because nothing in this client blocks waiting for the server.
        /// </remarks>
        public bool Who()
        {
            if (!_joined)
            {
                return false;
            }

            SendToServer(new ChatMessage(ChatMessageKind.Who, _nickname, string.Empty));
            return true;
        }

        /// <summary>
        /// Leaves the room and closes the port.
        /// </summary>
        /// <remarks>
        /// Tell the server. It is what returns the seat to the room's free count - a client that
        /// just vanishes holds its seat until something else clears it.
        /// </remarks>
        public void Leave()
        {
            if (_joined)
            {
                SendToServer(new ChatMessage(ChatMessageKind.Leave, _nickname, string.Empty));
                _joined = false;
            }

            if (_hasPort)
            {
                _kernel.ClosePort(_port);
                _hasPort = false;
            }
        }

        /// <summary>
        /// Collects everything that has arrived for this client.
        /// </summary>
        /// <returns>
        /// The messages received, oldest first. Empty when nothing was waiting.
        /// </returns>
        public IReadOnlyList<ChatMessage> Poll()
        {
            List<ChatMessage> received = new List<ChatMessage>();
            if (!_hasPort)
            {
                return received;
            }

            while (true)
            {
                XmsgReceiveResult arrived = _kernel.Receive(_port, XmsgWaitOptions.None);
                if (!arrived.Received)
                {
                    return received;
                }

                XmsgMagicNumber sender = _kernel.GetMessageStatus(arrived.Message).Sender;

                int read;
                _kernel.Read(arrived.Message, _scratch, 0, out read);
                _kernel.ReleaseBuffer(arrived.Message);

                ChatMessage message;
                if (!ChatMessage.TryDecode(new ReadOnlySpan<byte>(_scratch, 0, read), out message))
                {
                    continue;
                }

                if (message.Kind == ChatMessageKind.Welcome)
                {
                    // The welcome is what reveals the server's address. Keep it: everything this
                    // client sends from now on goes straight there, with no name lookup.
                    _server = sender;
                    _joined = true;
                }

                // OUR OWN rename, confirmed. The server broadcasts the new name with the old one in
                // the text, so this client recognises its own by the OLD name - which is still what
                // it is calling itself at this moment. Changing the local name any earlier would
                // leave a refused rename showing lines under a name the room never agreed to.
                if (message.Kind == ChatMessageKind.Renamed
                    && string.Equals(message.Text, _nickname, StringComparison.OrdinalIgnoreCase))
                {
                    _nickname = message.Nickname;
                }

                received.Add(message);
            }
        }

        private void SendToServer(ChatMessage message)
        {
            // Shared with ChatServer.SendTo - see ChatWire, which is where this sequence lives now.
            ChatWire.Send(_kernel, _server, _port, message);
        }
    }
}
