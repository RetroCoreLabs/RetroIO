//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node
{
    /// <summary>
    /// A non-persisting sequence store: every remote node starts at <c>Flags1 = 0x0000</c> and
    /// nothing is saved. The default for unit tests and for any composition that does not need
    /// cross-restart persistence.
    /// </summary>
    public sealed class NullResponderSequenceStore : IResponderSequenceStore
    {
        /// <inheritdoc />
        public ushort LoadNextFlags1(ushort remoteNode)
        {
            return 0x0000;
        }

        /// <inheritdoc />
        public void SaveNextFlags1(ushort remoteNode, ushort nextFlags1)
        {
            // Intentionally does nothing - no persistence.
        }
    }
}
