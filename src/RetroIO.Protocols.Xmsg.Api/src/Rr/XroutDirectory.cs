using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The name table and letter-forwarding behaviour of XROUT, for kernels running in this process.
    /// </summary>
    /// <remarks>
    /// <para><b>It forwards, it does not resolve</b></para>
    /// The whole point of XROUT is that it never hands a caller somebody else's magic number
    /// (section 1.3). This class keeps that property: <see cref="SendLetter"/> takes a NAME and
    /// delivers the letter into the named port's kernel with the sender's magic number attached.
    /// The caller gets a status, never an address. The server then learns who wrote to it the way
    /// the manual says - by asking its own kernel about the arrived message - and decides whether
    /// to answer.
    /// <para><b>Named ports and connection ports</b></para>
    /// <see cref="RegisterName"/> is XSNAM: one name, one port, and a second registration of the
    /// same name is refused. <see cref="RegisterConnectionPort"/> is XSCRS: it adds a
    /// free-connection counter, forwards a letter only while that counter is above zero, and
    /// decrements it on each forward. <see cref="AdjustFreeConnections"/> is XSNSP, which is how a
    /// server gives capacity back when a session ends.
    /// <para><b>Scope</b></para>
    /// This is the local half of XROUT - enough to build and test servers and clients against.
    /// Routing a letter to another SYSTEM is the node layer's job and is not modelled here; a
    /// letter for an unknown name fails with XRUNN exactly as XROUT would.
    /// </remarks>
    public sealed class XroutDirectory
    {
        private readonly Dictionary<string, Entry> _names;

        /// <summary>
        /// Initialises an empty name table.
        /// </summary>
        public XroutDirectory()
        {
            _names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Names a port, as the XSNAM service does.
        /// </summary>
        /// <param name="name">
        /// The name to register, conventionally starting with an asterisk.
        /// </param>
        /// <param name="kernel">
        /// The kernel owning the port.
        /// </param>
        /// <param name="magic">
        /// The magic number of the port being named.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/>, or <see cref="XroutError.XRDDF"/> when the name is taken.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> or <paramref name="kernel"/> is null.
        /// </exception>
        public XroutError RegisterName(string name, XmsgKernel kernel, XmsgMagicNumber magic)
        {
            return Register(name, kernel, magic, -1, true);
        }

        /// <summary>
        /// Creates a connection port with a capacity, as the XSCRS service does.
        /// </summary>
        /// <param name="name">
        /// The connection name.
        /// </param>
        /// <param name="kernel">
        /// The kernel owning the port.
        /// </param>
        /// <param name="magic">
        /// The magic number of the port being named.
        /// </param>
        /// <param name="maximumConnections">
        /// The initial free-connection count.
        /// </param>
        /// <param name="unique">
        /// True to demand the name be unique; false lets several connection ports share it.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/>, or <see cref="XroutError.XRDDF"/> when uniqueness was
        /// demanded and the name is taken.
        /// </returns>
        public XroutError RegisterConnectionPort(
            string name,
            XmsgKernel kernel,
            XmsgMagicNumber magic,
            int maximumConnections,
            bool unique)
        {
            return Register(name, kernel, magic, maximumConnections, unique);
        }

        /// <summary>
        /// Copies out every registered name with how many free connections it still has.
        /// </summary>
        /// <returns>
        /// One entry per name, in no particular order. A fresh array each time.
        /// </returns>
        /// <remarks>
        /// <para><b>This is a convenience of the STAND-IN, not a captured wire service</b></para>
        /// <para>
        /// A real XROUT can be asked things over the wire, and this class stands in for it in
        /// process. Enumerating the table is what lets a client on THIS node offer a list of chat
        /// rooms without having to be told their names in advance.
        /// </para>
        /// <para>
        /// It is NOT a claim that a remote machine can ask for this list. No capture in the corpus
        /// shows a name-listing exchange, so how one would look on the wire is UNKNOWN and is not
        /// invented here. A SINTRAN user discovering our rooms is a separate, unmeasured problem.
        /// </para>
        /// </remarks>
        public XroutNameEntry[] CopyNames()
        {
            XroutNameEntry[] entries = new XroutNameEntry[_names.Count];

            int at = 0;
            Dictionary<string, Entry>.Enumerator e = _names.GetEnumerator();
            while (e.MoveNext())
            {
                KeyValuePair<string, Entry> pair = e.Current;
                entries[at] = new XroutNameEntry(pair.Key, pair.Value.FreeConnections);
                at++;
            }

            return entries;
        }

        /// <summary>
        /// Adjusts a connection port's free-connection count, as the XSNSP service does.
        /// </summary>
        /// <param name="name">
        /// The registered connection name.
        /// </param>
        /// <param name="delta">
        /// Positive to release connections back, negative to withdraw them.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/>, <see cref="XroutError.XRUNN"/> for an unknown name, or
        /// <see cref="XroutError.XRNSP"/> when the count would go negative.
        /// </returns>
        public XroutError AdjustFreeConnections(string name, int delta)
        {
            Entry? entry = Find(name);
            if (entry == null)
            {
                return XroutError.XRUNN;
            }

            if (entry.FreeConnections < 0)
            {
                // A plain named port has no counter to adjust.
                return XroutError.XRIPT;
            }

            if (entry.FreeConnections + delta < 0)
            {
                return XroutError.XRNSP;
            }

            entry.FreeConnections += delta;
            return XroutError.XRSOK;
        }

        /// <summary>
        /// Clears a name, as the XSCNM service does.
        /// </summary>
        /// <param name="name">
        /// The name to remove.
        /// </param>
        /// <returns>
        /// True when a name was removed.
        /// </returns>
        public bool ClearName(string name)
        {
            return name != null && _names.Remove(name);
        }

        /// <summary>
        /// Forwards a letter to a named port, as the XSLET service does.
        /// </summary>
        /// <param name="name">
        /// The destination port or connection name.
        /// </param>
        /// <param name="sender">
        /// The magic number of the sending port, which the recipient will read from the message.
        /// </param>
        /// <param name="letter">
        /// The letter body - whatever the two programs have agreed on, typically an identification.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/> when the letter was forwarded,
        /// <see cref="XroutError.XRUNN"/> for an unknown name, or
        /// <see cref="XroutError.XRNSP"/> when a connection port has no free connections left.
        /// </returns>
        /// <remarks>
        /// The letter is delivered with the FORWARD option so the recipient sees the original
        /// sender rather than XROUT - which is what lets the server answer the caller directly.
        /// </remarks>
        public XroutError SendLetter(string name, XmsgMagicNumber sender, ReadOnlySpan<byte> letter)
        {
            Entry? entry = Find(name);
            if (entry == null)
            {
                return XroutError.XRUNN;
            }

            if (entry.FreeConnections == 0)
            {
                return XroutError.XRNSP;
            }

            XmsgStatus delivered = entry.Kernel.Deliver(
                entry.Magic, sender, letter, XmsgSendFlags.Forward);

            if (delivered.IsError)
            {
                return XroutError.XRUNM;
            }

            if (entry.FreeConnections > 0)
            {
                entry.FreeConnections--;
            }

            return XroutError.XRSOK;
        }

        /// <summary>
        /// Reports how many free connections a registered connection port has left.
        /// </summary>
        /// <param name="name">
        /// The registered name.
        /// </param>
        /// <returns>
        /// The free-connection count, -1 for a plain named port with no counter, or -2 when the
        /// name is not registered.
        /// </returns>
        public int FreeConnections(string name)
        {
            Entry? entry = Find(name);
            return entry == null ? -2 : entry.FreeConnections;
        }

        private XroutError Register(string name, XmsgKernel kernel, XmsgMagicNumber magic, int capacity, bool unique)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (kernel == null)
            {
                throw new ArgumentNullException(nameof(kernel));
            }

            if (unique && _names.ContainsKey(name))
            {
                return XroutError.XRDDF;
            }

            _names[name] = new Entry(kernel, magic, capacity);
            return XroutError.XRSOK;
        }

        private Entry? Find(string name)
        {
            if (name == null)
            {
                return null;
            }

            return _names.TryGetValue(name, out Entry? entry) ? entry : null;
        }

        private sealed class Entry
        {
            internal Entry(XmsgKernel kernel, XmsgMagicNumber magic, int freeConnections)
            {
                Kernel = kernel;
                Magic = magic;
                FreeConnections = freeConnections;
            }

            internal XmsgKernel Kernel { get; }

            internal XmsgMagicNumber Magic { get; }

            internal int FreeConnections { get; set; }
        }
    }
}
