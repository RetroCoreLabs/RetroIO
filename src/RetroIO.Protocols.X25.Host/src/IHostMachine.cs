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

        /// <summary>The call is not a PAD call (<c>pad_calls_only</c>): its call user data does not start with the X.29
        /// protocol identifier 01 00 00 00 (X.29 (12/97) 1.3).</summary>
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

    /// <summary>One call as the operator console sees it (docs/x25host-format.md in RetroX25, "The operator console").</summary>
    /// <param name="User">The logged-in user as typed, or empty before the login.</param>
    /// <param name="State">Where the caller is: <c>login</c>, <c>shell</c>, <c>menu</c> or <c>room</c>.</param>
    /// <param name="LoggedInAt">When the login was accepted, or null before it.</param>
    /// <param name="LastInput">When the caller last typed (the call's start if never).</param>
    public readonly record struct HostSessionInfo(string User, string State, DateTime? LoggedInAt, DateTime LastInput);

    /// <summary>
    /// What the operator of the program running a machine may do to its calls. Implemented by
    /// <see cref="DescribedHost"/>; a <see cref="ScriptedHost"/> does not know its users and does not implement it.
    /// Called on the machine's own thread, as every other member.
    /// </summary>
    public interface IHostOperator
    {
        /// <summary>The state of one call.</summary>
        /// <param name="session">The session.</param>
        /// <param name="info">The call's state, when it is known.</param>
        /// <returns>False for a session the machine does not have.</returns>
        bool TryDescribe(int session, out HostSessionInfo info);

        /// <summary>An operator message: a line to the caller, then the prompt and the half-typed line again.</summary>
        /// <param name="session">The session.</param>
        /// <param name="text">The text, without the line end.</param>
        void OperatorSend(int session, string text);

        /// <summary>Logs the caller out as the logout command does: the logout text, then the call is ended.</summary>
        /// <param name="session">The session.</param>
        void OperatorLogout(int session);
    }

    /// <summary>
    /// What a machine can ask of the caller's PAD besides text: an X.29 set PAD message (X.29 (12/97) 3.1) of X.3
    /// parameter 2, echo. Implemented by the program that runs the machine, which sends it with Q = 1 (X.29 1.5.2).
    /// </summary>
    public interface IHostPadControl
    {
        /// <summary>Tell the caller's PAD to echo what is typed, or to stop (for a password).</summary>
        /// <param name="session">The session.</param>
        /// <param name="echo">True: X.3 parameter 2 = 1 (echo); false: parameter 2 = 0 (no echo).</param>
        void SetEcho(int session, bool echo);
    }
}
