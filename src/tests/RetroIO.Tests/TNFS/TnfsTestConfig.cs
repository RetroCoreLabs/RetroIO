//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Centralized configuration for all TNFS tests.
/// </summary>
public static class TnfsTestConfig
{
    /// <summary>
    /// Remote C TNFS server address (tnfsd) for manual integration tests. Sourced from the
    /// <c>RETROFS_TNFS_SERVER</c> environment variable so it is NEVER hardcoded to a specific machine IP
    /// (which changes between machines/networks); falls back to loopback when the variable is unset.
    /// Set e.g. <c>RETROFS_TNFS_SERVER=192.168.1.42</c> before running the manual remote tests.
    /// </summary>
    public static readonly string REMOTE_SERVER_ADDRESS =
        Environment.GetEnvironmentVariable("RETROFS_TNFS_SERVER") ?? "127.0.0.1";

    /// <summary>
    /// Remote C TNFS server port (tnfsd).
    /// </summary>
    public const int REMOTE_SERVER_PORT = 16384;

    /// <summary>
    /// Root directory on the remote server where test files should be placed.
    /// </summary>
    public const string REMOTE_SERVER_ROOT = "/tmp/tnfs";

    /// <summary>
    /// Port for automated integration tests (self-hosted .NET TNFS server).
    /// Uses different port to avoid conflicts with manually started servers.
    /// </summary>
    public const int AUTOMATED_TEST_PORT_UDP = 16666;

    /// <summary>
    /// TCP port for automated integration tests (self-hosted .NET TNFS server).
    /// </summary>
    public const int AUTOMATED_TEST_PORT_TCP = 16667;

    /// <summary>
    /// Root directory for automated integration tests.
    /// This directory is created/destroyed by the tests automatically.
    /// </summary>
    public const string AUTOMATED_TEST_ROOT = "/tmp/tnfs-integration-test";

    /// <summary>
    /// Connection timeout for all tests (milliseconds).
    /// </summary>
    public const int CONNECTION_TIMEOUT_MS = 5000;

    /// <summary>
    /// Whether to enable verbose logging in tests.
    /// </summary>
    public const bool VERBOSE_LOGGING = false;
}
