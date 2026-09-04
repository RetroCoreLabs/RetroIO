using System;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// What this node believes about each peer machine it is trunked to.
    /// </summary>
    /// <remarks>
    /// <para><b>A trunk is a BELIEF, not a connection</b></para>
    /// <para>
    /// XMSG named ports are message passing. There is no session to hold and nothing that drops,
    /// so a trunk is the belief that a peer is reachable, refreshed by anything arriving from it.
    /// Neither side is the client, and ONE SIDE IS ENOUGH: an unsolicited hello brings the trunk
    /// up and the rest follows, which is how D100 and D102 first trunked with only D100 ever
    /// being told to.
    /// </para>
    /// <para><b>Rules only, no transport</b></para>
    /// <para>
    /// The same split as <see cref="ChatRoom"/>: this decides WHAT should happen and something
    /// else sends it. That is what lets the ageing and the backoff be tested without a machine,
    /// a socket or a clock - <see cref="Tick"/> is called once a second by whoever owns the loop
    /// and a test calls it in a for loop.
    /// </para>
    /// <para><b>It mirrors CHATSV.PLNC deliberately</b></para>
    /// <para>
    /// The ND server keeps exactly this table with exactly these rules, proved live on D100 on
    /// 2026-08-22: stop the peer's server and the trunk goes down in about fifty seconds taking
    /// its people with it; start it again and it comes back with nobody typing anything. Two
    /// implementations of one protocol only interoperate if they agree about state as well as
    /// bytes, so where this disagrees with the PLANC, one of them is wrong.
    /// </para>
    /// </remarks>
    public sealed class ChatTrunks
    {
        /// <summary>
        /// How many peers a node can be trunked to.
        /// </summary>
        /// <remarks>
        /// Eight, the same as the ND server. The limit is not about memory - it is that a chat
        /// spanning more than a handful of machines needs relaying, and relaying needs a hop count
        /// and an origin that this protocol deliberately does not have yet.
        /// </remarks>
        public const int MaxPeers = 8;

        /// <summary>
        /// Seconds of silence after which a peer that was up is declared down.
        /// </summary>
        /// <remarks>
        /// Sixty. Long enough that an ordinary pause in traffic is not mistaken for a dead
        /// machine, short enough that the people on it stop being listed while somebody is still
        /// looking at the screen.
        /// </remarks>
        public const int DeadAfterSeconds = 60;

        /// <summary>
        /// The first gap between greetings to a peer that is not up.
        /// </summary>
        public const int FirstWaitSeconds = 15;

        /// <summary>
        /// The longest gap between greetings, however long a peer stays away.
        /// </summary>
        /// <remarks>
        /// <para><b>The cap bounds the WAIT, not the number of tries</b></para>
        /// <para>
        /// The wait doubles 15, 30, 60, 120, 240 and then stays at 300. A machine away for a week
        /// keeps being greeted every five minutes for ever, and that is exactly what makes it
        /// rejoin on its own without anybody noticing it came back.
        /// </para>
        /// </remarks>
        public const int MaxWaitSeconds = 300;

        private readonly int[] _system = new int[MaxPeers];
        private readonly ChatTrunkState[] _state = new ChatTrunkState[MaxPeers];
        private readonly int[] _silentFor = new int[MaxPeers];
        private readonly int[] _wait = new int[MaxPeers];
        private readonly int[] _countdown = new int[MaxPeers];

        /// <summary>
        /// The system number this node is, used to refuse enrolling itself.
        /// </summary>
        private readonly int _mySystem;

        /// <summary>
        /// Creates an empty trunk table for a node.
        /// </summary>
        /// <param name="mySystem">
        /// This node's own system number.
        /// </param>
        /// <remarks>
        /// <para><b>Knowing our own number is not bookkeeping</b></para>
        /// <para>
        /// A hello sent to a peer that has no chat server BOUNCES, arrives back on our own trunk
        /// port wearing our own magic, and looks exactly like a peer saying hello. The ND server
        /// enrolled ITSELF that way and reported "100 up" on machine 100. Refusing our own number
        /// in <see cref="MarkHeard"/> is what stops it.
        /// </para>
        /// </remarks>
        public ChatTrunks(int mySystem)
        {
            _mySystem = mySystem;
        }

        /// <summary>
        /// This node's own system number.
        /// </summary>
        public int MySystem
        {
            get { return _mySystem; }
        }

        /// <summary>
        /// Starts trunking to a peer, or does nothing if it is already known.
        /// </summary>
        /// <param name="system">
        /// The peer's system number.
        /// </param>
        /// <returns>
        /// <c>true</c> when the peer is now in the table, <c>false</c> when the table is full or
        /// the number is this node's own.
        /// </returns>
        /// <remarks>
        /// A new peer starts <see cref="ChatTrunkState.Unknown"/> rather than down. The two are
        /// kept apart on purpose: unknown usually means a trunk that has never been answered and
        /// is probably a configuration mistake, down means a machine that was there and went away.
        /// </remarks>
        public bool Add(int system)
        {
            if (system == 0 || system == _mySystem) { return false; }

            int existing = IndexOf(system);
            if (existing >= 0) { return true; }

            for (int i = 0; i < MaxPeers; i++)
            {
                if (_system[i] == 0)
                {
                    _system[i] = system;
                    _state[i] = ChatTrunkState.Unknown;
                    _silentFor[i] = 0;
                    _wait[i] = FirstWaitSeconds;
                    _countdown[i] = FirstWaitSeconds;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Stops trunking to a peer.
        /// </summary>
        /// <param name="system">
        /// The peer's system number.
        /// </param>
        /// <returns>
        /// <c>true</c> when the peer was in the table.
        /// </returns>
        public bool Remove(int system)
        {
            int at = IndexOf(system);
            if (at < 0) { return false; }

            _system[at] = 0;
            _state[at] = ChatTrunkState.Unknown;
            return true;
        }

        /// <summary>
        /// Records that something arrived from a peer, which is what proves it is alive.
        /// </summary>
        /// <param name="system">
        /// The peer the message came from.
        /// </param>
        /// <returns>
        /// <c>true</c> when this changed the peer to up from something else.
        /// </returns>
        /// <remarks>
        /// <para><b>Anything counts, not just a hello</b></para>
        /// <para>
        /// A forwarded line proves a peer is there as well as a greeting does, so the trunk is
        /// refreshed by traffic and a busy pair never needs to greet at all.
        /// </para>
        /// <para><b>Our own number is refused</b></para>
        /// <para>
        /// See the note on the constructor: a bounced hello arrives looking like a peer.
        /// </para>
        /// <para><b>The backoff resets on EVERY transition to up</b></para>
        /// <para>
        /// Not just the first. A peer that flaps would otherwise inherit a five-minute wait from
        /// its last absence and take five minutes to be missed again.
        /// </para>
        /// </remarks>
        public bool MarkHeard(int system)
        {
            if (system == 0 || system == _mySystem) { return false; }

            int at = IndexOf(system);
            if (at < 0) { return false; }

            bool changed = _state[at] != ChatTrunkState.Up;

            _state[at] = ChatTrunkState.Up;
            _silentFor[at] = 0;
            _wait[at] = FirstWaitSeconds;
            _countdown[at] = FirstWaitSeconds;

            return changed;
        }

        /// <summary>
        /// Advances the table by one second and says what should be sent or forgotten.
        /// </summary>
        /// <param name="result">
        /// Filled in with what this second requires.
        /// </param>
        /// <remarks>
        /// <para><b>One tick is one second, and that has to come from an idle clock</b></para>
        /// <para>
        /// The ND server calls this from its one-second idle sleep rather than from the message
        /// loop, so a busy room cannot make peers time out faster. Anything driving this class
        /// should do the same: a tick per message would make the timeouts depend on traffic,
        /// which is precisely backwards.
        /// </para>
        /// <para><b>Two separate things happen</b></para>
        /// A peer that was up and has gone quiet is declared down AND its people are dropped, so
        /// nobody is shown chatting from a machine that is not answering. Separately, anything not
        /// up is greeted again on a doubling wait.
        /// </remarks>
        public void Tick(ChatTrunkTick result)
        {
            if (result == null) { throw new ArgumentNullException(nameof(result)); }

            result.Reset();

            for (int i = 0; i < MaxPeers; i++)
            {
                if (_system[i] == 0) { continue; }

                _silentFor[i]++;

                if (_state[i] == ChatTrunkState.Up && _silentFor[i] > DeadAfterSeconds)
                {
                    _state[i] = ChatTrunkState.Down;
                    _wait[i] = FirstWaitSeconds;
                    _countdown[i] = FirstWaitSeconds;
                    result.AddWentDown(_system[i]);
                }

                if (_state[i] != ChatTrunkState.Up)
                {
                    _countdown[i]--;
                    if (_countdown[i] <= 0)
                    {
                        result.AddGreet(_system[i]);

                        // Double THEN cap, so the cap is a ceiling on the wait and not on the
                        // number of tries.
                        _wait[i] = _wait[i] * 2;
                        if (_wait[i] > MaxWaitSeconds) { _wait[i] = MaxWaitSeconds; }
                        _countdown[i] = _wait[i];
                    }
                }
            }
        }

        /// <summary>
        /// What this node believes about one peer.
        /// </summary>
        /// <param name="system">
        /// The peer's system number.
        /// </param>
        /// <returns>
        /// The peer's state, or <see cref="ChatTrunkState.Unknown"/> when it is not in the table.
        /// </returns>
        public ChatTrunkState StateOf(int system)
        {
            int at = IndexOf(system);
            if (at < 0) { return ChatTrunkState.Unknown; }

            return _state[at];
        }

        /// <summary>
        /// The peers in the table, in slot order.
        /// </summary>
        /// <param name="into">
        /// Filled with the peer system numbers. Must hold <see cref="MaxPeers"/>.
        /// </param>
        /// <returns>
        /// How many were written.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="into"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="into"/> is too short.
        /// </exception>
        public int ListPeers(int[] into)
        {
            if (into == null) { throw new ArgumentNullException(nameof(into)); }
            if (into.Length < MaxPeers) { throw new ArgumentException("needs room for MaxPeers", nameof(into)); }

            int count = 0;
            for (int i = 0; i < MaxPeers; i++)
            {
                if (_system[i] != 0)
                {
                    into[count] = _system[i];
                    count++;
                }
            }

            return count;
        }

        private int IndexOf(int system)
        {
            for (int i = 0; i < MaxPeers; i++)
            {
                if (_system[i] == system && system != 0) { return i; }
            }

            return -1;
        }
    }

    /// <summary>
    /// What a node believes about a peer it is trunked to.
    /// </summary>
    public enum ChatTrunkState
    {
        /// <summary>
        /// Configured, and never heard from.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="Down"/> because the two mean different things to whoever is
        /// looking: this one usually means the trunk was never answered and the configuration is
        /// wrong, where <see cref="Down"/> means a machine that was there and went away.
        /// Collapsing them would hide the difference exactly when an operator needs it.
        /// </remarks>
        Unknown = 0,

        /// <summary>
        /// Something has arrived from it recently.
        /// </summary>
        Up = 1,

        /// <summary>
        /// It was up and has gone quiet.
        /// </summary>
        Down = 2,
    }
}
