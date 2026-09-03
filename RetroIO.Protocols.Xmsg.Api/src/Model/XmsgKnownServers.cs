using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// A named XMSG server as it appears in the XROUT name table.
    /// </summary>
    /// <remarks>
    /// A server publishes itself by naming one of its ports, which is what lets a client address it
    /// without knowing its magic number. See DOC/XMSG-SERVER-NAMES-AND-LETTERS.md for how the
    /// naming and letter mechanism works end to end.
    /// </remarks>
    public sealed class XmsgServerName
    {
        /// <summary>
        /// Initialises a server-name entry.
        /// </summary>
        /// <param name="name">
        /// The registered name, including its leading asterisk.
        /// </param>
        /// <param name="observedPort">
        /// The port number this name was seen on, as listed by the operator command.
        /// </param>
        /// <param name="observedOn">
        /// Which system or image the observation came from.
        /// </param>
        /// <param name="description">
        /// What the server does.
        /// </param>
        /// <param name="portConfirmedOnWire">
        /// Whether the port has been confirmed against captured traffic, rather than only read
        /// from the live registry listing.
        /// </param>
        /// <param name="freeConnections">
        /// The free-connection count the registry reported, or -1 when the listing showed none
        /// (a plain named port rather than a connection port).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> or <paramref name="description"/> is null.
        /// </exception>
        public XmsgServerName(
            string name,
            int observedPort,
            string description,
            bool portConfirmedOnWire,
            string observedOn,
            int freeConnections)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (description == null)
            {
                throw new ArgumentNullException(nameof(description));
            }

            Name = name;
            ObservedPort = observedPort;
            ObservedOn = observedOn;
            Description = description;
            PortConfirmedOnWire = portConfirmedOnWire;
            FreeConnections = freeConnections;
        }

        /// <summary>
        /// Gets the registered name, including its leading asterisk.
        /// </summary>
        /// <remarks>
        /// ND's convention is that the names of standard products begin with two asterisks and a
        /// product code; in practice the registry shows one asterisk and a product mnemonic.
        /// </remarks>
        public string Name { get; }

        /// <summary>
        /// Gets the port number this name was OBSERVED on, in the boot named by
        /// <see cref="ObservedOn"/>.
        /// </summary>
        /// <remarks>
        /// NOT a well-known port. It is the kernel port-table index of whatever port the server
        /// happened to open, so it moves with load order - see the remarks on
        /// <see cref="XmsgKnownServers"/>. Never address a server by this number.
        /// </remarks>
        public int ObservedPort { get; }

        /// <summary>
        /// Gets a description of what the server does.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the system or image the observation came from.
        /// </summary>
        public string ObservedOn { get; }

        /// <summary>
        /// Gets the free-connection count the registry reported, or -1 for a plain named port.
        /// </summary>
        /// <remarks>
        /// Unlike the port number, this IS stable across systems: the same server reports the same
        /// count wherever it runs, because it is the maximum the server registered with through
        /// XSCRS. Observed identical on both systems for every connection port.
        /// </remarks>
        public int FreeConnections { get; }

        /// <summary>
        /// Gets a value indicating whether the port was confirmed from captured traffic.
        /// </summary>
        /// <remarks>
        /// False means the number comes from the live registry listing only. Treat those as
        /// unconfirmed when reasoning about wire bytes.
        /// </remarks>
        public bool PortConfirmedOnWire { get; }
    }

    /// <summary>
    /// Named servers seen in live XROUT registries, with the port each happened to occupy.
    /// </summary>
    /// <remarks>
    /// <para><b>The port numbers are NOT well-known</b></para>
    /// VERIFIED across two systems and two runs of one of them. The COSMOS machine reports
    /// <c>*TADADM</c> 2, <c>*XM-FIDO</c> 4 and <c>*XFTRA</c> 8; booting the BIGDISK0-L image and
    /// starting the same products reports <c>*XM-FIDO</c> 3, <c>*TADADM</c> 4 and <c>*XFTRA</c> 5.
    /// Every number differs while every name matches. Port 4 is even a different server between
    /// two runs of the SAME image - <c>*XM-ENNS0</c> when the Ethernet server is brought up,
    /// <c>*TADADM</c> when the COSMOS module is. A registry port is simply the kernel port-table
    /// index of whatever port the server opened, so it moves with what is loaded and in what
    /// order, and gaps appear wherever unnamed ports were opened first. This follows directly from
    /// the carved magic-number layout, where the port number is a 1-based table index with nothing
    /// name-specific about it.
    /// <para><b>Capacity, unlike the port, IS stable</b></para>
    /// Every connection port reports the SAME free-connection count on both systems - <c>*XFTRA</c>
    /// 1, <c>*FA-FSA</c> 2, <c>*FA-SERVER</c> 30 - so the count a server registers with (its XSCRS
    /// maximum) is a property of the server, not of the boot. Two ports also happen to coincide
    /// (<c>*FA-FSA</c> 7 and <c>*FA-SERVER</c> 11), which is what a fixed allocation ORDER inside
    /// one module looks like; it is not evidence that those two numbers are well-known either.
    /// <para><b>What that means for callers</b></para>
    /// Use this table to RECOGNISE traffic you are looking at, never to address anything. A server
    /// is addressed by sending a letter to its NAME and letting XROUT resolve it - which is the
    /// entire reason the naming mechanism exists. Any code that maps a name to a fixed port number
    /// is wrong on the next boot.
    /// </remarks>
    public static class XmsgKnownServers
    {
        private static readonly XmsgServerName[] s_all = new XmsgServerName[]
        {
            // COSMOS-installed machine, from a live list-servers.
            new XmsgServerName("*TADADM", 2, "Terminal access (TAD) - fully decoded", true, "COSMOS machine", -1),
            new XmsgServerName("*XM-FIDO", 4, "File transfer", false, "COSMOS machine", -1),
            new XmsgServerName("*COSPO", 5, "COSMOS spooling", false, "COSMOS machine", -1),
            new XmsgServerName("*FA-FSA", 7, "Remote file access - FSA control side", false, "COSMOS machine", 2),
            new XmsgServerName("*XFTRA", 8, "File transfer", false, "COSMOS machine", 1),
            new XmsgServerName("*FA-SERVER", 11, "Remote file access - bulk file server", false, "COSMOS machine", 30),

            // BIGDISK0-L image, ENNS0 bring-up run (emulated boot harness).
            new XmsgServerName("*XM-FIDO", 3, "File transfer", false, "BIGDISK0-L, ENNS0 run", -1),
            new XmsgServerName("*XM-ENNS0", 4, "Ethernet network server", false, "BIGDISK0-L, ENNS0 run", -1),

            // BIGDISK0-L image, full COSMOS bring-up run. The whole registry reproduced on an
            // independent system: four ports differ from the COSMOS machine, two coincide, and
            // EVERY free-connection count matches.
            new XmsgServerName("*XM-FIDO", 3, "File transfer", false, "BIGDISK0-L, COSMOS run", -1),
            new XmsgServerName("*TADADM", 4, "Terminal access (TAD)", false, "BIGDISK0-L, COSMOS run", -1),
            new XmsgServerName("*XFTRA", 5, "File transfer", false, "BIGDISK0-L, COSMOS run", 1),
            new XmsgServerName("*COSPO", 6, "COSMOS spooling", false, "BIGDISK0-L, COSMOS run", -1),
            new XmsgServerName("*FA-FSA", 7, "Remote file access - FSA control side", false, "BIGDISK0-L, COSMOS run", 2),
            new XmsgServerName("*FA-SERVER", 11, "Remote file access - bulk file server", false, "BIGDISK0-L, COSMOS run", 30),
        };

        /// <summary>
        /// Gets every known server entry.
        /// </summary>
        /// <returns>
        /// The observed registry entries.
        /// </returns>
        public static IReadOnlyList<XmsgServerName> All()
        {
            return s_all;
        }

        /// <summary>
        /// Finds a server by its registered name.
        /// </summary>
        /// <param name="name">
        /// The name to look for, compared case-insensitively and including its leading asterisk.
        /// </param>
        /// <returns>
        /// The entry, or <c>null</c> when the name is not one we have observed.
        /// </returns>
        public static XmsgServerName? Find(string name)
        {
            if (name == null)
            {
                return null;
            }

            for (int i = 0; i < s_all.Length; i++)
            {
                if (string.Equals(s_all[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return s_all[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Finds every observation of a registered name.
        /// </summary>
        /// <param name="name">
        /// The name to look for, compared case-insensitively and including its leading asterisk.
        /// </param>
        /// <returns>
        /// Every entry carrying that name, which may be more than one because the same server
        /// occupies different ports on different systems.
        /// </returns>
        public static IReadOnlyList<XmsgServerName> FindAll(string name)
        {
            List<XmsgServerName> matches = new List<XmsgServerName>();
            if (name == null)
            {
                return matches;
            }

            for (int i = 0; i < s_all.Length; i++)
            {
                if (string.Equals(s_all[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(s_all[i]);
                }
            }

            return matches;
        }

    }
}
