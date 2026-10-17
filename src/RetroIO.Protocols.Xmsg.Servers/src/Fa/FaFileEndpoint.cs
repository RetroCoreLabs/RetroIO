//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// The addressing a file-access conversation needs, whichever way the file is moving.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// <para>
    /// A read and a write address a server in exactly the same way: same node, same system name,
    /// same well-known port, same conversation number, same reserve fields. Only two things differ,
    /// and both belong to the subclass:
    /// </para>
    ///  - how long a file specification may be, because a write quotes the name and a read does not.
    ///  - the access letter, which a write sends under selector 3 and a read does not send at all.
    /// <para>
    /// So the common part lives here and each direction adds its own rule.
    /// <see cref="FaWriteTarget"/> keeps its constructor, its properties and its exception messages
    /// exactly as they were - this was extracted from it without changing what it does, because it
    /// is the only path proved against a real ND and a working push is not something to put at risk
    /// for tidiness.
    /// </para>
    /// <para><b>The filespec is LOCAL to the server, not machine-qualified</b></para>
    /// <para>
    /// The captured requests send <c>BIGPSH3:TXT</c> - no machine, no user. They do not need them:
    /// the conversation is already addressed to that machine, and the USER is carried separately by
    /// the <c>ReserveFileEntry</c> request. A <c>D102(SYSTEM).</c> prefix is command-line syntax for
    /// a SINTRAN operator and has no place on the FA wire.
    /// </para>
    /// </remarks>
    public abstract class FaFileEndpoint
    {
        /// <summary>
        /// Creates an endpoint and checks the parts every direction shares.
        /// </summary>
        /// <param name="serverNode">
        /// The node running <c>*FA-SERVER</c>, for example 100.
        /// </param>
        /// <param name="serverSystemName">
        /// The name of the machine the server runs on - <c>D100</c> for node 100. This is what goes
        /// in the connect letter, because XROUT looks the server up by the system it is asked for.
        /// It is NOT our own name.
        /// </param>
        /// <param name="fileSpec">
        /// The file specification AS THE SERVER'S OWN MACHINE SEES IT.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverSystemName"/> or <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is empty.
        /// </exception>
        /// <remarks>
        /// The LENGTH of <paramref name="fileSpec"/> is deliberately not checked here. The ceiling
        /// differs by direction and the message that explains it has to name the right fields, so
        /// each subclass checks its own and says why.
        /// </remarks>
        protected FaFileEndpoint(ushort serverNode, string serverSystemName, string fileSpec)
        {
            if (serverSystemName == null) { throw new ArgumentNullException(nameof(serverSystemName)); }
            if (fileSpec == null) { throw new ArgumentNullException(nameof(fileSpec)); }

            if (serverSystemName.Length == 0)
            {
                throw new ArgumentException(
                    "The server's system name cannot be empty.", nameof(serverSystemName));
            }

            if (fileSpec.Length == 0)
            {
                throw new ArgumentException("The filespec cannot be empty.", nameof(fileSpec));
            }

            ServerNode = serverNode;
            ServerSystemName = serverSystemName;
            FileSpec = fileSpec;

            // The server's system number is its node number on every capture we have.
            ServerSystem = serverNode;
            ServerPort = FaServer.FaServerWirePort;

            // Defaults the captured clients used. All of them may be varied.
            Conversation = 0x0044;
            LetterEchoWord = 0x0002;
            BackgroundProgram = "BAK04";
            User = "SYSTEM";
            LocalUser = "SYSTEM";
            PasswordWord = 0;
        }

        /// <summary>
        /// Gets the node running the file server.
        /// </summary>
        public ushort ServerNode { get; }

        /// <summary>
        /// Gets the name of the machine the file server runs on, as carried in the connect letter.
        /// </summary>
        public string ServerSystemName { get; }

        /// <summary>
        /// Gets the remote filespec.
        /// </summary>
        public string FileSpec { get; }

        /// <summary>
        /// Gets or sets the server's XMSG system number.
        /// </summary>
        public ushort ServerSystem { get; set; }

        /// <summary>
        /// Gets or sets the server's wire port.
        /// </summary>
        public ushort ServerPort { get; set; }

        /// <summary>
        /// Gets or sets the conversation number we stamp on every request.
        /// </summary>
        /// <remarks>
        /// The asker picks it. Across the captures it ran 003F, 0044, 0046, 0048, 0052 and never
        /// repeated, so it behaves as a counter rather than an opcode.
        /// </remarks>
        public ushort Conversation { get; set; }

        /// <summary>
        /// Gets or sets the word the connect letter carries for the server to echo.
        /// </summary>
        /// <remarks>
        /// Whatever we put here comes back in the confirmation and is then stamped on everything
        /// the server sends. It is NOT always <c>0x0002</c>; treating it as a constant hung a live
        /// terminal.
        /// </remarks>
        public ushort LetterEchoWord { get; set; }

        /// <summary>
        /// Gets or sets the background-program name the reserve request carries.
        /// </summary>
        /// <remarks>
        /// <c>BAK04</c> in the captured write, <c>BAK05</c> in the captured read. Decoded by
        /// cross-reading five captures: only this field varies between them, so it names the
        /// CLIENT's background program and not the file.
        /// </remarks>
        public string BackgroundProgram { get; set; }

        /// <summary>
        /// Gets or sets the REMOTE user whose directory the file belongs to.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the one that decides where a file lands</b></para>
        /// <para>
        /// The OPEN request carries only the bare name, so nothing in it says which directory is
        /// meant. The user travels in the RESERVE request instead. Leave this at its default and
        /// the far end uses its own session user, which is what happened to every push until
        /// 2026-08-24: the caller asked for UTILITY, nothing set this, and the file arrived in
        /// SYSTEM.
        /// </para>
        /// </remarks>
        public string User { get; set; }

        /// <summary>
        /// Gets or sets the user we are asking AS, which need not be the one being read.
        /// </summary>
        /// <remarks>
        /// Field 3 of the reserve request - <c>BAK03  SYSTEM</c> in the captures - names the
        /// background program and this user. Field 4 names <see cref="User"/>. They were believed
        /// to be the same field twice until a capture appeared with a client reading somebody
        /// else directory; see <c>FaWriteRequests.ReserveFileEntry</c>.
        /// </remarks>
        public string LocalUser { get; set; }

        /// <summary>
        /// Gets or sets <see cref="User"/> password, already folded to one word, or zero when
        /// that user has none.
        /// </summary>
        /// <remarks>
        /// <para><b>Folded, never plaintext</b></para>
        /// <para>
        /// SINTRAN folds a password to a single 16-bit word and sends only that:
        /// <c>acc = ROL16(acc,3) + toupper(c)</c>. Use
        /// <c>RetroIO.Protocols.Xmsg.Api.SintranPassword.Encode</c> to produce it. Confirmed on
        /// the wire on 2026-07-29 - <c>secret</c> travelled as <c>6D 2A</c> and the plaintext
        /// appeared nowhere in the frame.
        /// </para>
        /// <para>
        /// Zero means "no password", which is what a user without one needs and what every
        /// same-user capture shows. A WRONG value answers WRONG PASSWORD rather than failing
        /// quietly.
        /// </para>
        /// </remarks>
        public ushort PasswordWord { get; set; }
    }
}
