//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;

namespace RetroIO.Protocols.Xmsg.Programs.Running
{
    /// <summary>
    /// The stop signal of a program's run loop: Ctrl+C, or <see cref="Stop"/> from a command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ctrl+C does not end the process. It only sets <see cref="Token"/>, so the loop can leave its
    /// rooms, release its names and close its ports before it returns: the clean stop every program
    /// owes RetroServer (plan, Phase 3). A second Ctrl+C is treated the same way.
    /// </para>
    /// <para>
    /// Dispose unhooks Ctrl+C, so a test or a second instance is not left with an old handler.
    /// </para>
    /// </remarks>
    public sealed class ProgramStop : IDisposable
    {
        private readonly CancellationTokenSource _source = new CancellationTokenSource();
        private readonly bool _hooked;
        private int _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgramStop"/> class.
        /// </summary>
        /// <param name="hookCtrlC">
        /// True to stop on Ctrl+C (a program); false for a test, which calls <see cref="Stop"/>.
        /// </param>
        public ProgramStop(bool hookCtrlC = true)
        {
            if (hookCtrlC)
            {
                Console.CancelKeyPress += OnCancelKeyPress;
                _hooked = true;
            }
        }

        /// <summary>
        /// Gets the token the run loop watches.
        /// </summary>
        public CancellationToken Token
        {
            get { return _source.Token; }
        }

        /// <summary>
        /// Gets a value indicating whether a stop was asked for.
        /// </summary>
        public bool IsStopping
        {
            get { return Volatile.Read(ref _disposed) == 0 && _source.IsCancellationRequested; }
        }

        /// <summary>
        /// Asks the run loop to stop, as Ctrl+C does. Does nothing after <see cref="Dispose"/>.
        /// </summary>
        public void Stop()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _source.Cancel();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (_hooked)
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }

            _source.Dispose();
        }

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            // Keep the process alive; the loop stops cleanly on the token.
            e.Cancel = true;
            Stop();
        }
    }
}
