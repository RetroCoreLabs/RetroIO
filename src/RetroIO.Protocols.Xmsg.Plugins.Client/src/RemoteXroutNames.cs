//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Google.Protobuf;

using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Plugins.Contracts;

namespace RetroIO.Protocols.Xmsg.Plugins.Client
{
    /// <summary>
    /// The XROUT name calls a plug-in's task code makes, carried to the node's name table.
    /// </summary>
    /// <remarks>
    /// A name claimed here is a name on the node: a SINTRAN machine sending a letter to it reaches
    /// this plug-in. When the plug-in's connection ends, the node clears every name it held.
    /// </remarks>
    public sealed class RemoteXroutNames : IXroutNames
    {
        private readonly PluginConnection _connection;
        private readonly RemoteXmsgKernel _kernel;

        internal RemoteXroutNames(PluginConnection connection, RemoteXmsgKernel kernel)
        {
            _connection = connection;
            _kernel = kernel;
        }

        /// <inheritdoc/>
        public XroutError RegisterName(string name, XmsgMagicNumber magic)
        {
            return Register(name, magic, false, 0, true);
        }

        /// <inheritdoc/>
        public XroutError RegisterConnectionPort(string name, XmsgMagicNumber magic, int maximumConnections, bool unique)
        {
            return Register(name, magic, true, maximumConnections, unique);
        }

        /// <inheritdoc/>
        public XroutError AdjustFreeConnections(string name, int delta)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return Status(_connection.Call(new PluginRequest { AdjustSeats = new AdjustSeats { Name = name, Delta = delta } }));
        }

        /// <inheritdoc/>
        public bool ClearName(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return Status(_connection.Call(new PluginRequest { ClearName = new ClearName { Name = name } })) == XroutError.XRSOK;
        }

        /// <inheritdoc/>
        public XroutError SendLetter(string name, XmsgMagicNumber sender, ReadOnlySpan<byte> letter)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return Status(_connection.Call(new PluginRequest
            {
                SendLetter = new SendLetter { Name = name, FromMagic = sender.Value, Data = ByteString.CopyFrom(letter) },
            }));
        }

        /// <inheritdoc/>
        /// <exception cref="NotSupportedException">
        /// Always: listing the node's names is not part of the plug-in contract yet.
        /// </exception>
        public XroutNameEntry[] CopyNames()
        {
            throw new NotSupportedException(
                "Listing the node's names is not carried by the plug-in contract (docs/XMSG-PLUGIN-SERVERS.md in RetroServer).");
        }

        private XroutError Register(string name, XmsgMagicNumber magic, bool connectionPort, int seats, bool unique)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!_kernel.OwnsMagic(magic))
            {
                return XroutError.XRUNM;
            }

            return Status(_connection.Call(new PluginRequest
            {
                RegisterName = new RegisterName
                {
                    Name = name,
                    Magic = magic.Value,
                    ConnectionPort = connectionPort,
                    Seats = seats,
                    Unique = unique,
                },
            }));
        }

        private static XroutError Status(Result? answer)
        {
            // No answer means the node is gone or did not reply in time. XRUNM ("unknown magic
            // number") is not exact, but it is an error, never a success.
            return answer == null ? XroutError.XRUNM : (XroutError)answer.Status;
        }
    }
}
