using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// A snapshot of one registered <b>server</b> (a named XROUT program) for the <c>list servers</c>
    /// command - the same shape as COSMOS <c>list-servers</c> (System, Port, Free SPs, Name).
    /// </summary>
    public sealed class XmsgServerInfo
    {
        /// <summary>
        /// Initialises a server-info snapshot.
        /// </summary>
        /// <param name="name">
        /// The registered name (for example <c>*TADADM</c>).
        /// </param>
        /// <param name="logicalPort">
        /// The well-known logical port (for example 2).
        /// </param>
        /// <param name="wirePort">
        /// The minted wire reply-from port (<c>(logical shifted left 7) | incarnation</c>).
        /// </param>
        /// <param name="sessionCount">
        /// The number of currently-active sessions.
        /// </param>
        /// <param name="sessionCapacity">
        /// The maximum concurrent sessions (Free SPs = capacity - count).
        /// </param>
        public XmsgServerInfo(string name, int logicalPort, ushort wirePort, int sessionCount, int sessionCapacity)
        {
            Name = name;
            LogicalPort = logicalPort;
            WirePort = wirePort;
            SessionCount = sessionCount;
            SessionCapacity = sessionCapacity;
        }

        /// <summary>
        /// Gets the registered server name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the well-known logical port.
        /// </summary>
        public int LogicalPort { get; }

        /// <summary>
        /// Gets the minted wire reply-from port.
        /// </summary>
        public ushort WirePort { get; }

        /// <summary>
        /// Gets the number of currently-active sessions.
        /// </summary>
        public int SessionCount { get; }

        /// <summary>
        /// Gets the maximum concurrent sessions.
        /// </summary>
        public int SessionCapacity { get; }

        /// <summary>
        /// Gets the number of free session slots (Free SPs).
        /// </summary>
        public int FreeSlots
        {
            get { return SessionCapacity - SessionCount; }
        }
    }

    /// <summary>
    /// A snapshot of one XROUT <b>service</b> (a numbered verb in the XMCSM low byte) for the
    /// <c>list service</c> command. These are XROUT operations (XSLET, XSGSY, ...), not programs.
    /// </summary>
    public sealed class XmsgServiceInfo
    {
        /// <summary>
        /// Initialises a service-info snapshot.
        /// </summary>
        /// <param name="serviceByte">
        /// The XMCSM low "service" byte (for example 0x41 for XSLET).
        /// </param>
        /// <param name="mnemonic">
        /// The service mnemonic (for example <c>XSLET</c>).
        /// </param>
        /// <param name="description">
        /// A short description, or an empty string when unknown.
        /// </param>
        public XmsgServiceInfo(byte serviceByte, string mnemonic, string description)
        {
            ServiceByte = serviceByte;
            Mnemonic = mnemonic;
            Description = description;
        }

        /// <summary>
        /// Gets the XMCSM low "service" byte.
        /// </summary>
        public byte ServiceByte { get; }

        /// <summary>
        /// Gets the service mnemonic.
        /// </summary>
        public string Mnemonic { get; }

        /// <summary>
        /// Gets a short description (empty when unknown).
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets a value indicating whether this is a request (bit 6 of the service byte set - the XROUT
        /// convention "service byte has bit 6 set => service request").
        /// </summary>
        public bool IsRequest
        {
            get { return (ServiceByte & 0x40) != 0; }
        }
    }

    /// <summary>
    /// The known XROUT services, built from the official <see cref="XroutService"/> code table with the
    /// most-used verbs annotated. Used by the <c>list service</c> command.
    /// </summary>
    public static class XmsgKnownServices
    {
        // Short descriptions for the services this framework actually meets on the wire; the rest are
        // listed by mnemonic + code from the enum. (The enum's full descriptions live in XML comments,
        // which are not available at runtime, so the important ones are annotated here.)
        private static readonly Dictionary<XroutService, string> Descriptions = new Dictionary<XroutService, string>
        {
            { XroutService.XSNUL, "Null command - returns 0 status to sender" },
            { XroutService.XSLET, "Send a letter (connect-to, list-systems)" },
            { XroutService.XSNAM, "Give a name to this port" },
            { XroutService.XSGNM, "Get the name of a port" },
            { XroutService.XSGSY, "Get routing info for a system (list-route)" },
            { XroutService.XSGIN, "Get information about a name" },
            { XroutService.XSPIN, "Get information about named ports" },
            { XroutService.XSLSY, "Get information about a system (list-systems)" },
            { XroutService.XSGSU, "Get system-utilisation info" },
            { XroutService.XSCRS, "Create a service (name, initial SPs)" },
        };

        /// <summary>
        /// Builds the full list of known XROUT services from the code table.
        /// </summary>
        /// <returns>
        /// The services, ordered by service byte.
        /// </returns>
        public static IReadOnlyList<XmsgServiceInfo> All()
        {
            List<XmsgServiceInfo> list = new List<XmsgServiceInfo>();
            Array values = Enum.GetValues(typeof(XroutService));
            HashSet<byte> seen = new HashSet<byte>();
            for (int i = 0; i < values.Length; i++)
            {
                XroutService svc = (XroutService)values.GetValue(i)!;
                byte code = (byte)svc;
                // Aliases (XSDMC/XSDSY, XSGMC/XSGSY, XSMAX/XSGSG) share a code; keep the first name.
                if (!seen.Add(code))
                {
                    continue;
                }

                string desc = Descriptions.TryGetValue(svc, out string? d) ? d : string.Empty;
                list.Add(new XmsgServiceInfo(code, svc.ToString(), desc));
            }

            return list;
        }
    }
}
