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
    /// Thrown when an XMSG function returns a negative status and the caller asked for exceptions.
    /// </summary>
    /// <remarks>
    /// The XMSG interface itself is status-returning, not exception-based: every function reports
    /// through the T register (see <see cref="XmsgStatus"/>). This exception exists for the
    /// convenience surface, where a caller prefers not to test a status after every call. The
    /// status-returning methods never throw it.
    /// </remarks>
    public sealed class XmsgException : Exception
    {
        /// <summary>
        /// Initialises a new exception for a failed XMSG function.
        /// </summary>
        /// <param name="function">
        /// The function that failed.
        /// </param>
        /// <param name="error">
        /// The XE* error code returned in the T register.
        /// </param>
        public XmsgException(XmsgFunction function, XmsgError error)
            : base(function.ToString() + " failed with " + error.ToString() + " (" + ((int)error).ToString() + ")")
        {
            Function = function;
            Error = error;
        }

        /// <summary>
        /// Gets the function that failed.
        /// </summary>
        public XmsgFunction Function { get; }

        /// <summary>
        /// Gets the XE* error code returned in the T register.
        /// </summary>
        public XmsgError Error { get; }
    }
}
