//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg.Programs.Running;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Programs.Tests
{
    /// <summary>
    /// The run loop's stop signal and the exit codes.
    /// </summary>
    public sealed class ProgramStopTests
    {
        /// <summary>
        /// Stop sets the token; before it, nothing is stopping.
        /// </summary>
        [Fact]
        public void Stop_SetsTheToken()
        {
            using ProgramStop stop = new ProgramStop(hookCtrlC: false);
            Assert.False(stop.IsStopping);
            Assert.False(stop.Token.IsCancellationRequested);

            stop.Stop();

            Assert.True(stop.IsStopping);
            Assert.True(stop.Token.IsCancellationRequested);
        }

        /// <summary>
        /// Stop after Dispose does nothing and does not throw (a late STOP command at shutdown).
        /// </summary>
        [Fact]
        public void Stop_AfterDispose_DoesNotThrow()
        {
            ProgramStop stop = new ProgramStop(hookCtrlC: true);
            stop.Dispose();
            stop.Stop();
            stop.Dispose();
            Assert.False(stop.IsStopping);
        }

        /// <summary>
        /// The codes keep the numbers the chat samples have always returned.
        /// </summary>
        [Fact]
        public void ExitCodes_KeepTheirNumbers()
        {
            Assert.Equal(0, ProgramExitCode.Done);
            Assert.Equal(2, ProgramExitCode.BadSettings);
            Assert.Equal(3, ProgramExitCode.CannotConnect);
            Assert.Equal(4, ProgramExitCode.Refused);
            Assert.Equal(5, ProgramExitCode.ConnectionClosed);
            Assert.Equal(6, ProgramExitCode.NoAnswer);
        }
    }
}
