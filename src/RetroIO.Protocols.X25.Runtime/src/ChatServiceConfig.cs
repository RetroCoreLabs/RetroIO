//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// The "Chat" section of the configuration: the built-in Altos CHAT 3.1 service
    /// (<see cref="RetroIO.Protocols.X25.Services.ChatProtocolService"/>). Off unless <see cref="Address"/> is set.
    /// </summary>
    public class ChatServiceConfig
    {
        /// <summary>
        /// The chat's own X.121 address: a PAD call to it is answered by the chat. Empty (the default) means no chat
        /// service at all. It must differ from <c>X25.LocalMachineId</c>, which is the TAD's address; the same address
        /// is refused when the runtime starts (logged), and the chat is then not registered. The original Altos chat
        /// was at 26245890040004 (NUA 0262 45 8900 40004).
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Most users in the chat at one time; the next caller gets "open of answerpipe failed." and is cleared. Default
        /// 30, the original's NUSERS (CHAT 3.1 chat.h:16).
        /// </summary>
        public int MaxUsers { get; set; } = 30;

        /// <summary>
        /// Octets that may be on their way to one user before a chat line to that user counts as a failed write, and
        /// the user is put out of the chat as the original did after a 2-second blocked write (CHAT 3.1 serv.c:200-204).
        /// Default 4096, a choice of this port.
        /// </summary>
        public int OutputLimitBytes { get; set; } = 4096;

        /// <summary>
        /// Milliseconds between "Waiting for connection." and "Connection established.". Default 3000: the original
        /// slept 3 seconds there (CHAT 3.1 chat.c:278). 0 joins at once.
        /// </summary>
        public int WaitPauseMs { get; set; } = 3000;

        /// <summary>
        /// The nickname every caller starts with. Default <c>guest</c>, the login every X.25 caller had on the Altos.
        /// </summary>
        public string StartingNickname { get; set; } = "guest";
    }
}
