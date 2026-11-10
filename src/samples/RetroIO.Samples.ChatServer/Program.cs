//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Grpc.Net.Client;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg.Plugins.Client;

using Microsoft.Extensions.Logging;

namespace RetroIO.Samples.ChatServer
{
    /// <summary>
    /// Runs the chat server as an XMSG plug-in program connected to a node.
    /// </summary>
    /// <remarks>
    /// The chat server is written as a SINTRAN task: it opens a port, claims a name, receives and
    /// sends. Here those calls go to the node over gRPC (<see cref="PluginConnection"/>), so
    /// SINTRAN machines on the node's links reach this program by the name it claims. When the
    /// connection ends the node releases the name, and this program stops; start it again to come
    /// back, as a SINTRAN server program is restarted.
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// How long the loop sleeps when nothing arrived, in milliseconds.
        /// </summary>
        private const int IdleSleepMs = 10;

        /// <summary>
        /// Entry point.
        /// </summary>
        /// <param name="args">
        /// Overrides for one run of what <see cref="ChatServerSettings.FileName"/> holds:
        /// <c>--config FILE</c>, <c>--node URL</c>, <c>--token SECRET</c>, <c>--name NAME</c>,
        /// <c>--seats N</c> and <c>--greeting TEXT</c>.
        /// </param>
        /// <returns>
        /// Zero when stopped with Ctrl+C, non-zero on a bad command line, a refused connection or a
        /// name that could not be claimed.
        /// </returns>
        public static async Task<int> Main(string[] args)
        {
            // The settings file first (next to the program, or --config), then the flags on top.
            string configPath = Path.Combine(AppContext.BaseDirectory, ChatServerSettings.FileName);
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (Is(args[i], "--config"))
                {
                    configPath = args[i + 1];
                }
            }

            string? problem;
            ChatServerSettings? settings = ChatServerSettings.Load(configPath, out problem);
            if (settings == null)
            {
                Console.Error.WriteLine("retro-chat-server: cannot read " + configPath + ": " + problem);
                return 2;
            }

            string node = settings.Node;
            string token = settings.Token;
            string name = settings.Name;
            int seats = settings.Seats;
            string greeting = settings.Greeting;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                bool hasValue = i + 1 < args.Length;
                if (Is(a, "--help") || Is(a, "-h"))
                {
                    PrintUsage();
                    return 0;
                }
                else if (Is(a, "--config") && hasValue)
                {
                    i++;
                }
                else if (Is(a, "--token") && hasValue)
                {
                    token = args[++i];
                }
                else if (Is(a, "--node") && hasValue)
                {
                    node = args[++i];
                }
                else if (Is(a, "--name") && hasValue)
                {
                    name = args[++i];
                }
                else if (Is(a, "--seats") && hasValue
                    && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seats) && seats > 0)
                {
                    i++;
                }
                else if (Is(a, "--greeting") && hasValue)
                {
                    greeting = args[++i];
                }
                else
                {
                    Console.Error.WriteLine("retro-chat-server: not understood: " + a);
                    PrintUsage();
                    return 2;
                }
            }

            using CancellationTokenSource stop = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            using GrpcChannel channel = GrpcChannel.ForAddress(node);
            PluginConnection connection;
            try
            {
                connection = await PluginConnection.ConnectAsync(channel, "retro-chat-server", "1.0", token, stop.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Grpc.Core.RpcException || ex is OperationCanceledException)
            {
                Console.Error.WriteLine("retro-chat-server: could not connect to the node at " + node + ": " + ex.Message);
                return 3;
            }

            await using (connection)
            {
                RetroIO.Protocols.Xmsg.Chat.ChatServer chat = new RetroIO.Protocols.Xmsg.Chat.ChatServer(connection.Kernel, connection.Names);
                XroutError opened = chat.Open(name, seats, greeting);
                if (opened != XroutError.XRSOK)
                {
                    Console.Error.WriteLine("retro-chat-server: the node refused the name " + name + ": " + opened);
                    return 4;
                }

                Console.WriteLine("retro-chat-server: serving " + name + " with " + seats + " seats on node "
                    + connection.SystemNumber + " (" + node + "). Ctrl+C stops it.");

                // LOG LINES TO RETROSERVER. An ordinary .NET logger whose lines go over the connection
                // and show in RetroServer's log view under XmsgProgram.retro-chat-server.Room. Lines
                // below the level RetroServer asked for are not sent.
                using PluginLoggerProvider toRetroServer = new PluginLoggerProvider(connection);
                ILogger log = toRetroServer.CreateLogger("Room");
                log.LogInformation("Serving {Name} with {Seats} seats.", name, seats);
                int members = 0;

                while (!stop.IsCancellationRequested && !connection.IsClosed)
                {
                    if (chat.Poll() == 0)
                    {
                        Thread.Sleep(IdleSleepMs);
                    }

                    if (chat.MemberCount != members)
                    {
                        members = chat.MemberCount;
                        log.LogInformation("{Members} member(s) in the room.", members);
                    }
                }

                if (connection.IsClosed)
                {
                    Console.Error.WriteLine("retro-chat-server: the node closed the connection; the name is released.");
                    return 5;
                }

                log.LogInformation("Stopping; {Name} released.", name);
                chat.Close();
                Console.WriteLine("retro-chat-server: stopped; " + name + " released.");
                return 0;
            }
        }

        private static bool Is(string argument, string option)
        {
            return string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("retro-chat-server [--config FILE] [--node URL] [--token SECRET] [--name NAME] [--seats N] [--greeting TEXT]");
            Console.WriteLine("  Settings are read from " + ChatServerSettings.FileName + " next to the program (written with the");
            Console.WriteLine("  defaults when missing). The flags override it for this run only.");
            Console.WriteLine("  --config    another settings file");
            Console.WriteLine("  --node      RetroServer's program port (default http://127.0.0.1:5011)");
            Console.WriteLine("  --token     the secret RetroServer is configured with (ProgramToken); none by default");
            Console.WriteLine("  --name      the name to claim (default *CHAT, what the SINTRAN CHAT client looks up)");
            Console.WriteLine("  --seats     free connections, one per member (default 20)");
            Console.WriteLine("  --greeting  the line a new member is welcomed with");
        }
    }
}
