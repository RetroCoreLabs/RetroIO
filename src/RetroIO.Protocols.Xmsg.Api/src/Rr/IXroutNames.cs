//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The XROUT name services a task uses: claim a name, give seats back, drop the name, and send
    /// a letter to a name.
    /// </summary>
    /// <remarks>
    /// A task-style server (the chat server) is written against this, not against the in-process
    /// <see cref="XroutDirectory"/>, so the same server can run inside a node
    /// (<see cref="XroutDirectory.For"/>) or as a plug-in program whose names live in RetroServer
    /// (docs/XMSG-PLUGIN-SERVERS.md in the RetroServer repository). Each call is one XROUT service
    /// of the COSMOS Programmer Guide, appendix B: XSNAM, XSCRS, XSNSP, clearing a name, and XSLET.
    /// </remarks>
    public interface IXroutNames
    {
        /// <summary>
        /// Names a port (XSNAM).
        /// </summary>
        /// <param name="name">
        /// The name.
        /// </param>
        /// <param name="magic">
        /// The magic number of the port.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/>, or <see cref="XroutError.XRDDF"/> when the name is taken.
        /// </returns>
        XroutError RegisterName(string name, XmsgMagicNumber magic);

        /// <summary>
        /// Names a connection port with a count of free connections (XSCRS).
        /// </summary>
        /// <param name="name">
        /// The name.
        /// </param>
        /// <param name="magic">
        /// The magic number of the port.
        /// </param>
        /// <param name="maximumConnections">
        /// The free connections it starts with.
        /// </param>
        /// <param name="unique">
        /// True to refuse a name that is already taken.
        /// </param>
        /// <returns>
        /// The XROUT status.
        /// </returns>
        XroutError RegisterConnectionPort(string name, XmsgMagicNumber magic, int maximumConnections, bool unique);

        /// <summary>
        /// Adds to or takes from a connection port's free connections (XSNSP).
        /// </summary>
        /// <param name="name">
        /// The name.
        /// </param>
        /// <param name="delta">
        /// How many to add; a negative number takes away.
        /// </param>
        /// <returns>
        /// The XROUT status.
        /// </returns>
        XroutError AdjustFreeConnections(string name, int delta);

        /// <summary>
        /// Removes a name.
        /// </summary>
        /// <param name="name">
        /// The name.
        /// </param>
        /// <returns>
        /// True when the name was there.
        /// </returns>
        bool ClearName(string name);

        /// <summary>
        /// Sends a letter to a name (XSLET). The receiver gets the XSLET header and the data behind it.
        /// </summary>
        /// <param name="name">
        /// The name.
        /// </param>
        /// <param name="sender">
        /// The magic number of the sending port.
        /// </param>
        /// <param name="letter">
        /// The sender's own data.
        /// </param>
        /// <returns>
        /// The XROUT status.
        /// </returns>
        XroutError SendLetter(string name, XmsgMagicNumber sender, ReadOnlySpan<byte> letter);

        /// <summary>
        /// Lists the names and their free connections.
        /// </summary>
        /// <returns>
        /// One entry per name.
        /// </returns>
        XroutNameEntry[] CopyNames();
    }
}
