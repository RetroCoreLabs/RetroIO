//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Grpc.Net.Client;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg.Plugins.Client;

namespace RetroIO.Samples.ChatClient
{
    /// <summary>
    /// A terminal chat client connected to RetroServer.
    /// </summary>
    /// <remarks>
    /// RetroServer is the XMSG machine. This program opens its port on RetroServer's node over the
    /// plug-in connection (<see cref="PluginConnection"/>) and joins the chat service there by name,
    /// as a SINTRAN user on that machine would. It is never an XMSG machine of its own.
    /// Typed lines are said in the room; <c>/who</c>, <c>/nick NAME</c> and <c>/quit</c> are commands.
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// How long the loop sleeps when nothing happened, in milliseconds.
        /// </summary>
        private const int IdleSleepMs = 10;

        /// <summary>
        /// How long to wait for the server's welcome or refusal after joining.
        /// </summary>
        private static readonly TimeSpan JoinPatience = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long to keep listening after the last typed line went out, when the input has ended.
        /// </summary>
        private static readonly TimeSpan AnswerGrace = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Entry point.
        /// </summary>
        /// <param name="args">
        /// Overrides for one run of what <see cref="ChatClientSettings.FileName"/> holds:
        /// <c>--config FILE</c>, <c>--node URL</c>, <c>--token SECRET</c>, <c>--nick NAME</c>,
        /// <c>--service NAME</c> and <c>--room NAME</c>.
        /// </param>
        /// <returns>
        /// 0 on /quit, Ctrl+C or end of input; 2 bad command line; 3 no connection; 4 join refused or
        /// not delivered; 5 RetroServer closed the connection; 6 no answer to the join.
        /// </returns>
        public static async Task<int> Main(string[] args)
        {
            // The settings file first (next to the program, or --config), then the flags on top.
            string configPath = Path.Combine(AppContext.BaseDirectory, ChatClientSettings.FileName);
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (Is(args[i], "--config"))
                {
                    configPath = args[i + 1];
                }
            }

            string? problem;
            ChatClientSettings? settings = ChatClientSettings.Load(configPath, out problem);
            if (settings == null)
            {
                Console.Error.WriteLine("retro-chat: cannot read " + configPath + ": " + problem);
                return 2;
            }

            string node = settings.Node;
            string token = settings.Token;
            string nick = settings.Nick;
            string service = settings.Service;
            string room = settings.Room;

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
                else if (Is(a, "--nick") && hasValue)
                {
                    nick = args[++i];
                }
                else if (Is(a, "--service") && hasValue)
                {
                    service = args[++i];
                }
                else if (Is(a, "--room") && hasValue)
                {
                    room = args[++i];
                }
                else
                {
                    Console.Error.WriteLine("retro-chat: not understood: " + a);
                    PrintUsage();
                    return 2;
                }
            }

            if (nick.Length == 0)
            {
                Console.Error.WriteLine("retro-chat: no nickname. Set Nick in " + configPath + ", or pass --nick.");
                PrintUsage();
                return 2;
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
                connection = await PluginConnection.ConnectAsync(channel, "retro-chat", "1.0", token, stop.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Grpc.Core.RpcException || ex is OperationCanceledException)
            {
                Console.Error.WriteLine("retro-chat: could not connect to RetroServer at " + node + ": " + ex.Message);
                return 3;
            }

            await using (connection)
            {
                RetroIO.Protocols.Xmsg.Chat.ChatClient chat =
                    new RetroIO.Protocols.Xmsg.Chat.ChatClient(connection.Kernel, connection.Names, nick);
                XroutError joined = chat.Join(service, room);
                if (joined != XroutError.XRSOK)
                {
                    Console.Error.WriteLine("retro-chat: RetroServer could not deliver the join to " + service + ": " + joined);
                    return 4;
                }

                // Typed lines come from a reader thread; only this loop touches the client.
                ConcurrentQueue<string> typed = new ConcurrentQueue<string>();
                InputState input = new InputState();
                Thread reader = new Thread(() => ReadLines(typed, input, stop)) { IsBackground = true, Name = "keyboard" };
                reader.Start();
                DateTime? drainedAt = null;

                DateTime joinSent = DateTime.UtcNow;
                bool refused = false;
                while (!stop.IsCancellationRequested && !connection.IsClosed && !refused)
                {
                    bool busy = false;
                    IReadOnlyList<ChatMessage> got = chat.Poll();
                    for (int i = 0; i < got.Count; i++)
                    {
                        busy = true;
                        refused |= got[i].Kind == ChatMessageKind.Reject;
                        Print(got[i]);
                    }

                    if (!chat.IsJoined && !refused && DateTime.UtcNow - joinSent > JoinPatience)
                    {
                        Console.Error.WriteLine("retro-chat: no answer from " + service + " within "
                            + JoinPatience.TotalSeconds + " seconds.");
                        return 6;
                    }

                    // Lines wait until the server has let us in: said before the welcome they would be
                    // refused, and a script piping its lines in sends them before any answer can arrive.
                    string? line;
                    while (chat.IsJoined && typed.TryDequeue(out line))
                    {
                        busy = true;
                        if (!Obey(chat, line))
                        {
                            stop.Cancel();
                        }
                    }

                    // End of input (a script, or Ctrl+Z): leave once every line is sent and the
                    // answers had a moment to come back.
                    if (input.Ended && chat.IsJoined && typed.IsEmpty)
                    {
                        drainedAt ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - drainedAt.Value > AnswerGrace)
                        {
                            break;
                        }
                    }

                    if (!busy)
                    {
                        Thread.Sleep(IdleSleepMs);
                    }
                }

                if (refused)
                {
                    return 4;
                }

                if (connection.IsClosed)
                {
                    Console.Error.WriteLine("retro-chat: RetroServer closed the connection.");
                    return 5;
                }

                chat.Leave();
                return 0;
            }
        }

        /// <summary>
        /// Carries out one typed line.
        /// </summary>
        /// <param name="chat">
        /// The client.
        /// </param>
        /// <param name="line">
        /// What was typed.
        /// </param>
        /// <returns>
        /// False when the user asked to quit.
        /// </returns>
        private static bool Obey(RetroIO.Protocols.Xmsg.Chat.ChatClient chat, string line)
        {
            if (line.Length == 0)
            {
                return true;
            }

            if (Is(line, "/quit"))
            {
                return false;
            }

            if (Is(line, "/who"))
            {
                chat.Who();
                return true;
            }

            if (line.StartsWith("/nick ", StringComparison.OrdinalIgnoreCase))
            {
                chat.Rename(line.Substring(6).Trim());
                return true;
            }

            if (!chat.Say(line))
            {
                Console.Error.WriteLine("retro-chat: not sent - not in the room yet.");
            }

            return true;
        }

        /// <summary>
        /// Prints one message from the server.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        private static void Print(ChatMessage message)
        {
            if (message.Nickname.Length > 0)
            {
                Console.WriteLine(message.Kind + " " + message.Nickname + ": " + message.Text);
            }
            else
            {
                Console.WriteLine(message.Kind + ": " + message.Text);
            }
        }

        /// <summary>
        /// Reads keyboard lines until the input ends or the program stops.
        /// </summary>
        /// <param name="typed">
        /// Where the lines go.
        /// </param>
        /// <param name="input">
        /// Marked when the input ends.
        /// </param>
        /// <param name="stop">
        /// Ends the reading when the program stops.
        /// </param>
        private static void ReadLines(ConcurrentQueue<string> typed, InputState input, CancellationTokenSource stop)
        {
            while (!stop.IsCancellationRequested)
            {
                string? line = Console.ReadLine();
                if (line == null)
                {
                    input.Ended = true;
                    return;
                }

                typed.Enqueue(line);
            }
        }

        /// <summary>
        /// Whether the keyboard input has ended, shared between the reader thread and the loop.
        /// </summary>
        private sealed class InputState
        {
            private volatile bool _ended;

            /// <summary>
            /// Gets or sets a value indicating whether the input has ended.
            /// </summary>
            public bool Ended
            {
                get { return _ended; }
                set { _ended = value; }
            }
        }

        private static bool Is(string argument, string option)
        {
            return string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("retro-chat [--config FILE] [--nick NAME] [--node URL] [--token SECRET] [--service NAME] [--room NAME]");
            Console.WriteLine("  Settings are read from " + ChatClientSettings.FileName + " next to the program (written with the");
            Console.WriteLine("  defaults when missing). The flags override it for this run only.");
            Console.WriteLine("  --config   another settings file");
            Console.WriteLine("  --nick     the name to be known by in the room (required, here or in the file)");
            Console.WriteLine("  --node     RetroServer's program port (default http://127.0.0.1:5011)");
            Console.WriteLine("  --token    the secret RetroServer is configured with (ProgramToken); none by default");
            Console.WriteLine("  --service  the chat service's name (default *CHAT)");
            Console.WriteLine("  --room     the room to join (default: the server's own room)");
            Console.WriteLine("Typed lines are said in the room. /who lists members, /nick NAME renames, /quit leaves.");
        }
    }
}
