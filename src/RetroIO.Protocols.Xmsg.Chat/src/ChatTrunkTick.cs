using System;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// What one second of trunk ageing requires the caller to do.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the tick returns work instead of doing it</b></para>
    /// <para>
    /// <see cref="ChatTrunks"/> holds rules and no transport, so it cannot send a greeting or
    /// drop a peer's people itself. It says what is needed and the caller acts, which is what
    /// lets every timeout and every backoff step be tested without a socket or a real clock.
    /// </para>
    /// <para><b>Reused, not reallocated</b></para>
    /// <para>
    /// One of these is created once and passed to every tick. A tick happens once a second for as
    /// long as the server runs, and allocating a result object each time would be a garbage
    /// collection a second for information that is read immediately and thrown away.
    /// </para>
    /// </remarks>
    public sealed class ChatTrunkTick
    {
        private readonly int[] _greet = new int[ChatTrunks.MaxPeers];
        private readonly int[] _wentDown = new int[ChatTrunks.MaxPeers];

        private int _greetCount;
        private int _wentDownCount;

        /// <summary>
        /// How many peers should be greeted this second.
        /// </summary>
        public int GreetCount
        {
            get { return _greetCount; }
        }

        /// <summary>
        /// How many peers have just been declared down.
        /// </summary>
        public int WentDownCount
        {
            get { return _wentDownCount; }
        }

        /// <summary>
        /// A peer that should be greeted this second.
        /// </summary>
        /// <param name="index">
        /// Zero-based, less than <see cref="GreetCount"/>.
        /// </param>
        /// <returns>
        /// The peer's system number.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="index"/> is outside the greetings this tick produced.
        /// </exception>
        public int GreetAt(int index)
        {
            if (index < 0 || index >= _greetCount) { throw new ArgumentOutOfRangeException(nameof(index)); }

            return _greet[index];
        }

        /// <summary>
        /// A peer that has just been declared down, and whose people should be forgotten.
        /// </summary>
        /// <param name="index">
        /// Zero-based, less than <see cref="WentDownCount"/>.
        /// </param>
        /// <returns>
        /// The peer's system number.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="index"/> is outside the losses this tick produced.
        /// </exception>
        public int WentDownAt(int index)
        {
            if (index < 0 || index >= _wentDownCount) { throw new ArgumentOutOfRangeException(nameof(index)); }

            return _wentDown[index];
        }

        /// <summary>
        /// Empties the result so it can carry another second's work.
        /// </summary>
        internal void Reset()
        {
            _greetCount = 0;
            _wentDownCount = 0;
        }

        /// <summary>
        /// Records that a peer should be greeted.
        /// </summary>
        /// <param name="system">
        /// The peer's system number.
        /// </param>
        internal void AddGreet(int system)
        {
            if (_greetCount < _greet.Length)
            {
                _greet[_greetCount] = system;
                _greetCount++;
            }
        }

        /// <summary>
        /// Records that a peer has just gone down.
        /// </summary>
        /// <param name="system">
        /// The peer's system number.
        /// </param>
        internal void AddWentDown(int system)
        {
            if (_wentDownCount < _wentDown.Length)
            {
                _wentDown[_wentDownCount] = system;
                _wentDownCount++;
            }
        }
    }
}
