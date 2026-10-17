//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace RetroIO.Tests
{
    /// <summary>
    /// Makes the Azure tests self-sufficient: starts the Azurite storage emulator if it is not
    /// already running, and lets the tests skip cleanly when it cannot be started.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// The Azure tests talk to Azurite on <c>127.0.0.1:10000</c> via the well-known
    /// <c>UseDevelopmentStorage=true</c> connection string. With nothing listening there, each test
    /// used to spend about 37 seconds working through the Azure SDK's retry ladder before failing —
    /// turning a plain <c>dotnet test</c> into a 23-minute run ending in dozens of failures that say
    /// nothing about RetroFS.
    /// <para><b>What it does, in order</b></para>
    ///  - If something is already listening on the blob port, use it and leave it alone.
    ///  - Otherwise try to launch <c>npx azurite</c> and wait for the port to open.
    ///  - If that does not work, report unavailable so the fixtures call <c>Assert.Ignore</c>.
    /// <para><b>It never stops an emulator it did not start.</b></para>
    /// A developer who is already running Azurite — from <c>start-azurite.bat</c>, an IDE, or
    /// Docker — keeps it running after the tests finish. Only a process this class launched itself
    /// is stopped, and only at the very end of the run.
    /// </remarks>
    public static class AzuriteSupport
    {
        /// <summary>
        /// The Azurite blob-service port, as used by <c>UseDevelopmentStorage=true</c>.
        /// </summary>
        public const int BlobPort = 10000;

        /// <summary>
        /// The Azurite queue-service port.
        /// </summary>
        private const int QueuePort = 10001;

        /// <summary>
        /// The Azurite table-service port.
        /// </summary>
        private const int TablePort = 10002;

        /// <summary>
        /// How long to wait for a launched Azurite to start listening, in milliseconds.
        /// </summary>
        /// <remarks>
        /// A cold <c>npx</c> start has to resolve the package before node even boots. Azurite 3.35.0
        /// is already in this machine's npx cache, so it is normally listening in a couple of
        /// seconds; the allowance is generous so a slow first run does not skip the whole suite.
        /// </remarks>
        private const int StartupTimeoutMs = 30000;

        /// <summary>
        /// How long a single connect probe may take, in milliseconds.
        /// </summary>
        /// <remarks>
        /// A loopback connect either succeeds or is refused almost instantly, so this only bounds
        /// the pathological case. It must stay short: it runs before every fixture.
        /// </remarks>
        private const int ProbeTimeoutMs = 500;

        /// <summary>
        /// Guards the one-time start attempt against parallel fixtures.
        /// </summary>
        private static readonly object Gate = new object();

        /// <summary>
        /// The Azurite process this class launched, or null when it did not launch one.
        /// </summary>
        /// <remarks>
        /// Null both when Azurite was already running and when the launch failed — in either case
        /// there is nothing for this class to stop.
        /// </remarks>
        private static Process? _started;

        /// <summary>
        /// True once the start attempt has run, so it is not repeated per fixture.
        /// </summary>
        private static bool _resolved;

        /// <summary>
        /// Whether Azurite is usable for this test run.
        /// </summary>
        private static bool _available;

        /// <summary>
        /// Why Azurite is unavailable, shown in the skip message.
        /// </summary>
        private static string _unavailableReason = string.Empty;

        /// <summary>
        /// The data directory given to the Azurite this class launched.
        /// </summary>
        /// <remarks>
        /// Doubles as the marker that identifies our own emulator process on teardown: it contains
        /// this process's id, so no other Azurite on the machine can carry the same path.
        /// </remarks>
        private static string _workDir = string.Empty;

        /// <summary>
        /// Ensures Azurite is running, starting it if necessary, and reports whether it is usable.
        /// </summary>
        /// <returns>
        /// True when the blob service is listening and the Azure tests can run.
        /// </returns>
        /// <remarks>
        /// Safe to call from every fixture: the work happens once and later calls return the cached
        /// answer.
        /// </remarks>
        public static bool EnsureAvailable()
        {
            lock (Gate)
            {
                if (_resolved)
                    return _available;

                _resolved = true;

                // Already running — someone else's emulator. Use it, never manage it.
                if (IsListening(BlobPort))
                {
                    _available = true;
                    return true;
                }

                _available = TryStart(out _unavailableReason);
                return _available;
            }
        }

        /// <summary>
        /// Skips the calling test or fixture when Azurite is not available.
        /// </summary>
        /// <remarks>
        /// Call from a fixture's <c>OneTimeSetUp</c>. When Azurite cannot be reached this throws
        /// NUnit's ignore exception, so the tests are reported as skipped rather than failed —
        /// a missing emulator is a missing prerequisite, not a defect in RetroFS.
        /// </remarks>
        public static void SkipIfUnavailable()
        {
            if (EnsureAvailable())
                return;

            Assert.Ignore(
                $"Azurite is not available on 127.0.0.1:{BlobPort} and could not be started "
                + $"automatically ({_unavailableReason}). Start it yourself with start-azurite.bat, "
                + "or `npx azurite --silent`, then re-run.");
        }

        /// <summary>
        /// Stops Azurite, but only if this class started it.
        /// </summary>
        /// <remarks>
        /// Called once at the end of the whole test run. An emulator the developer started stays
        /// up: stopping someone else's process because the tests happened to borrow it would be a
        /// surprising side effect of running a test suite.
        /// </remarks>
        public static void StopIfStartedHere()
        {
            lock (Gate)
            {
                if (_started == null)
                    return;

                try
                {
                    if (!_started.HasExited)
                    {
                        _started.Kill(entireProcessTree: true);
                        _started.WaitForExit(5000);
                    }
                }
                catch
                {
                    // Best effort; the sweep below is the real safety net.
                }
                finally
                {
                    _started.Dispose();
                    _started = null;
                }

                // Killing the launcher is not enough. On Windows the chain is
                // cmd.exe -> npx-cli node -> azurite node, and the intermediate links exit as soon
                // as they have handed off. Once cmd has gone, Kill(entireProcessTree) has no tree
                // left to walk and the real emulator keeps running — which is exactly how an
                // earlier version of this leaked a node process past the end of the run.
                //
                // So sweep for it directly, matched on our per-run data directory. That path
                // contains this process's id, so only the emulator THIS run started can match: an
                // Azurite the developer started has a different --location and is left untouched.
                KillStrayAzurite();
            }
        }

        /// <summary>
        /// Launches Azurite through npx and waits for the blob port to open.
        /// </summary>
        /// <param name="reason">
        /// Receives a description of the failure when this returns false.
        /// </param>
        /// <returns>
        /// True when Azurite is listening.
        /// </returns>
        private static bool TryStart(out string reason)
        {
            // Unique per run. Two reasons: concurrent runs do not fight over one metadata database,
            // and — more importantly — the path becomes a marker that identifies OUR emulator on
            // teardown, so we can never stop one somebody else started.
            _workDir = Path.Combine(
                Path.GetTempPath(),
                "retrofs-azurite-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            string workDir = _workDir;
            try
            {
                Directory.CreateDirectory(workDir);
            }
            catch (Exception ex)
            {
                reason = "could not create its data directory: " + ex.Message;
                return false;
            }

            // --location keeps __blobstorage__ and the metadata database out of whatever directory
            // the test runner happens to start in; --silent keeps the console readable.
            string azuriteArgs =
                $"azurite --silent --blobPort {BlobPort} --queuePort {QueuePort} "
                + $"--tablePort {TablePort} --location \"{workDir}\"";

            // On Windows npx is a .cmd shim, and Process.Start with UseShellExecute=false cannot
            // launch a batch file directly - it throws Win32Exception "The specified executable is
            // not a valid application for this OS platform". Go through the command interpreter so
            // the shim resolves. Elsewhere npx is a real executable and can be started directly.
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npx --yes {azuriteArgs}",
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "npx",
                    Arguments = $"--yes {azuriteArgs}",
                };
            }

            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            try
            {
                _started = Process.Start(psi);
            }
            catch (Exception ex)
            {
                // npx missing, node missing, or blocked. Not an error worth failing tests over.
                _started = null;
                reason = "npx could not be launched: " + ex.Message;
                return false;
            }

            if (_started == null)
            {
                reason = "npx could not be launched";
                return false;
            }

            // Drain the pipes. A redirected stream nobody reads will eventually fill its buffer and
            // block the child, which would look like a hang rather than a failure. Keep what it
            // said, too: when startup fails, Azurite's own message ("port already in use", a node
            // stack trace) is the only useful diagnostic, and discarding it leaves nothing to go on.
            StringBuilder log = new StringBuilder();
            DataReceivedEventHandler collect = (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (log)
                {
                    if (log.Length < 2000) log.Append(e.Data).Append(" | ");
                }
            };
            _started.OutputDataReceived += collect;
            _started.ErrorDataReceived += collect;
            _started.BeginOutputReadLine();
            _started.BeginErrorReadLine();

            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < StartupTimeoutMs)
            {
                if (_started.HasExited)
                {
                    int code = _started.ExitCode;
                    _started.Dispose();
                    _started = null;
                    reason = $"npx azurite exited immediately with code {code}: {Describe(log)}";
                    return false;
                }

                if (IsListening(BlobPort))
                {
                    reason = string.Empty;
                    return true;
                }

                Thread.Sleep(250);
            }

            // Timed out. Stop what was started rather than leaving a stray node process behind.
            StopIfStartedHere();
            reason = $"it did not start listening within {StartupTimeoutMs / 1000} seconds: {Describe(log)}";
            return false;
        }

        /// <summary>
        /// Stops any Azurite process still running against this run's own data directory.
        /// </summary>
        /// <remarks>
        /// <para><b>Only ever our own process</b></para>
        /// Matching is on the <c>--location</c> path, which embeds this process's id. An emulator
        /// started by the developer, by <c>start-azurite.bat</c>, or by another test run has a
        /// different path and is never touched — stopping someone else's process because the tests
        /// borrowed the port would be an unpleasant surprise.
        /// <para>
        /// Uses WMI to read command lines, which is Windows-only; elsewhere the process tree kill
        /// already works, because there is no <c>cmd.exe</c> shim in the chain.
        /// </para>
        /// </remarks>
        private static void KillStrayAzurite()
        {
            if (string.IsNullOrEmpty(_workDir)) return;
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'");

                foreach (ManagementBaseObject row in searcher.Get())
                {
                    string? commandLine = row["CommandLine"] as string;
                    if (string.IsNullOrEmpty(commandLine)) continue;
                    if (commandLine.IndexOf(_workDir, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    int pid = Convert.ToInt32(row["ProcessId"], CultureInfo.InvariantCulture);
                    try
                    {
                        using Process stray = Process.GetProcessById(pid);
                        stray.Kill(entireProcessTree: true);
                        stray.WaitForExit(5000);
                    }
                    catch
                    {
                        // Already gone, or not ours to stop. Either way, nothing more to do.
                    }
                }
            }
            catch
            {
                // WMI unavailable or blocked. A leftover emulator is harmless: the next run finds
                // the port open and reuses it rather than starting a second one.
            }
        }

        /// <summary>
        /// Renders whatever Azurite printed, for a failure message.
        /// </summary>
        /// <param name="log">
        /// The collected output.
        /// </param>
        /// <returns>
        /// The output, or a note that it printed nothing.
        /// </returns>
        private static string Describe(StringBuilder log)
        {
            lock (log)
            {
                return log.Length == 0 ? "it printed nothing" : log.ToString();
            }
        }

        /// <summary>
        /// Probes whether anything is accepting connections on a local port.
        /// </summary>
        /// <param name="port">
        /// The TCP port to probe on the loopback interface.
        /// </param>
        /// <returns>
        /// True when the connection was accepted.
        /// </returns>
        private static bool IsListening(int port)
        {
            try
            {
                using TcpClient client = new TcpClient();
                IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(ProbeTimeoutMs))
                    return false;

                client.EndConnect(result);
                return true;
            }
            catch
            {
                // Refused, unreachable, or timed out - all mean "not available".
                return false;
            }
        }
    }

    /// <summary>
    /// Stops an Azurite instance started by the test run, once everything has finished.
    /// </summary>
    /// <remarks>
    /// A <see cref="SetUpFixtureAttribute"/> outside any namespace-scoped fixture runs once for the
    /// whole assembly. Azurite is started lazily by the first Azure fixture that needs it rather
    /// than here, so a run that touches no Azure test never launches anything.
    /// </remarks>
    [SetUpFixture]
    public class AzuriteLifetime
    {
        /// <summary>
        /// Stops Azurite if this test run started it.
        /// </summary>
        [OneTimeTearDown]
        public void StopAzurite()
        {
            AzuriteSupport.StopIfStartedHere();
        }
    }
}
