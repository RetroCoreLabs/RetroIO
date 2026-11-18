//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Host
{
    /// <summary>
    /// Whether a machine takes a call, asked before the call is accepted.
    /// </summary>
    public enum HostAdmission
    {
        /// <summary>The machine takes the call.</summary>
        Accept,

        /// <summary>The machine already serves as many calls as it may (<c>sessions</c> in host.ini).</summary>
        Busy,

        /// <summary>The call is not a PAD call (<c>pad_calls_only</c>): its call user data does not start 01 00 00 00.</summary>
        NotPadCall,
    }

    /// <summary>
    /// One fake machine that answers X.25 calls: a <see cref="ScriptedHost"/> (a JavaScript program) or a
    /// <see cref="DescribedHost"/> (a host.ini folder). The program that runs it gives it the time and the calls; what
    /// the machine sends comes out through an <see cref="IHostOutput"/>. Not thread safe: call it from one thread.
    /// </summary>
    public interface IHostMachine
    {
        /// <summary>Calls open now.</summary>
        int SessionCount { get; }

        /// <summary>The machine starts.</summary>
        /// <param name="now">The wall-clock time now.</param>
        void Start(DateTime now);

        /// <summary>Give the machine the time (timers, idle calls).</summary>
        /// <param name="now">The wall-clock time now.</param>
        void Tick(DateTime now);

        /// <summary>Asked before a call is accepted: does the machine take it?</summary>
        /// <param name="callUserData">The call user data of the Incoming Call (empty when none).</param>
        /// <returns>Whether it is taken, and if not, why.</returns>
        HostAdmission Admit(ReadOnlySpan<byte> callUserData);

        /// <summary>A call has been accepted.</summary>
        /// <param name="session">A number unique while the call lasts.</param>
        /// <param name="login">The name the caller is known by (empty when none).</param>
        /// <param name="calling">The caller's X.121 address, or null when unknown.</param>
        void Connect(int session, string login, string? calling);

        /// <summary>Bytes the caller typed.</summary>
        /// <param name="session">The session.</param>
        /// <param name="data">The bytes.</param>
        void Input(int session, ReadOnlySpan<byte> data);

        /// <summary>The caller or the network cleared the call.</summary>
        /// <param name="session">The session.</param>
        void Disconnect(int session);

        /// <summary>Bytes of this session's output have been delivered.</summary>
        /// <param name="session">The session.</param>
        /// <param name="bytes">How many.</param>
        void OutputDrained(int session, int bytes);
    }

    /// <summary>
    /// What a machine can ask of the caller's PAD besides text: an X.29 Set (X.29 (11/88) 3.5.4, parameter 2 "echo",
    /// X.3 2.2). Implemented by the program that runs the machine, which sends it as a Q-bit data packet.
    /// </summary>
    public interface IHostPadControl
    {
        /// <summary>Tell the caller's PAD to echo what is typed, or to stop (for a password).</summary>
        /// <param name="session">The session.</param>
        /// <param name="echo">True: X.3 parameter 2 = 1 (echo); false: parameter 2 = 0 (no echo).</param>
        void SetEcho(int session, bool echo);
    }
}
