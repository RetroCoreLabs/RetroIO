//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Client;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Manual test to debug file read failure.
/// </summary>
[TestFixture]
[Category("Manual")]
[Explicit]  // Must be run explicitly
public class TnfsFileReadManualTest
{
    [Test]
    public async Task ManualTest_FileRead_WithConsoleLogging()
    {
        const int TEST_PORT = 19999;
        string testRoot = Path.Combine(Path.GetTempPath(), "tnfs_manual_test");

        try
        {
            // Create test files
            Directory.CreateDirectory(testRoot);
            File.WriteAllText(Path.Combine(testRoot, "test.txt"), "Hello World");

            Console.WriteLine($"Test root: {testRoot}");
            Console.WriteLine($"Test file: {Path.Combine(testRoot, "test.txt")}");
            Console.WriteLine($"File exists: {File.Exists(Path.Combine(testRoot, "test.txt"))}");

            // Create server with console logging
            var host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();

                    services.AddSingleton<SessionManager>(sp =>
                        new SessionManager(
                            sp.GetRequiredService<ILogger<SessionManager>>(),
                            TimeSpan.FromMinutes(10),
                            maxSessionsPerIP: 10));

                    services.AddSingleton<SessionCommandHandler>(sp =>
                        new SessionCommandHandler(
                            sp.GetRequiredService<SessionManager>(),
                            sp.GetRequiredService<ILogger<SessionCommandHandler>>(),
                            testRoot,
                            sp.GetRequiredService<IFileSystemBackend>()));

                    services.AddSingleton<DirectoryCommandHandler>(sp =>
                        new DirectoryCommandHandler(
                            sp.GetRequiredService<SessionManager>(),
                            sp.GetRequiredService<ILogger<DirectoryCommandHandler>>(),
                            sp.GetRequiredService<IFileSystemBackend>()));

                    services.AddSingleton<FileCommandHandler>(sp =>
                        new FileCommandHandler(
                            sp.GetRequiredService<SessionManager>(),
                            sp.GetRequiredService<ILogger<FileCommandHandler>>(),
                            sp.GetRequiredService<IFileSystemBackend>()));

                    services.AddSingleton<ITransport>(sp =>
                        new TcpTransport(TEST_PORT,
                            sp.GetRequiredService<ILogger<TcpTransport>>()));

                    services.AddHostedService<RetroIO.Protocols.Tnfs.Server.TnfsServerService>();

                    services.AddLogging(builder =>
                    {
                        builder.ClearProviders();
                        builder.AddConsole();
                        builder.SetMinimumLevel(LogLevel.Trace);
                    });
                })
                .Build();

            await host.StartAsync();
            await Task.Delay(1000);  // Give server time to start

            Console.WriteLine("=== SERVER STARTED ===");

            // Create client
            var logger = host.Services.GetRequiredService<ILogger<TnfsClient>>();
            using var client = new TnfsClient("127.0.0.1", TEST_PORT, useTcp: true, logger: logger);

            // MOUNT
            Console.WriteLine("\n=== CLIENT: MOUNT ===");
            var mountResult = client.Mount("/");
            Console.WriteLine($"MOUNT result: {mountResult.Success}, SessionId: 0x{client.SessionId:X4}");
            Assert.That(mountResult.Success, Is.True);

            // OPEN
            Console.WriteLine("\n=== CLIENT: OPEN ===");
            var openResult = client.Open("/test.txt", TnfsOpenFlags.O_RDONLY);
            Console.WriteLine($"OPEN result: {openResult.Success}, Handle: {openResult.Data}");
            Assert.That(openResult.Success, Is.True);

            ushort handle = openResult.Data;

            // Wait a bit
            await Task.Delay(500);

            // READ
            Console.WriteLine("\n=== CLIENT: READ ===");
            var readResult = client.Read(handle, 1024);
            Console.WriteLine($"READ result: {readResult.Success}, ReturnCode: {readResult.ReturnCode}, Error: {readResult.ErrorMessage}");

            if (readResult.Success)
            {
                Assert.That(readResult.Data, Is.Not.Null);
                string content = System.Text.Encoding.ASCII.GetString(readResult.Data!);
                Console.WriteLine($"Content: {content}");
                Assert.That(content, Is.EqualTo("Hello World"));
            }
            else
            {
                Console.WriteLine($"❌ READ FAILED!");
                Assert.Fail($"Read failed with {readResult.ReturnCode}: {readResult.ErrorMessage}");
            }

            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
