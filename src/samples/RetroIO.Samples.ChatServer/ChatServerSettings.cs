//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text.Json;

namespace RetroIO.Samples.ChatServer
{
    /// <summary>
    /// The settings of <c>retro-chat-server</c>, read from <c>retro-chat-server.json</c> next to the
    /// program. Command-line flags override them for one run only.
    /// </summary>
    /// <remarks>
    /// RetroServer keeps no settings for this program. The program tells RetroServer itself what it
    /// needs when it connects (the name to claim, the seats), and the only thing the two must agree
    /// on is <see cref="Token"/> (Ronny, 2026-11-10).
    /// </remarks>
    public sealed class ChatServerSettings
    {
        /// <summary>
        /// The file name, next to the program.
        /// </summary>
        public const string FileName = "retro-chat-server.json";

        /// <summary>
        /// Gets or sets RetroServer's program port. Default <c>http://127.0.0.1:5011</c>, RetroServer's
        /// default for <c>ProgramPort</c> in <c>xmsgnode-config.json</c>.
        /// </summary>
        public string Node { get; set; } = "http://127.0.0.1:5011";

        /// <summary>
        /// Gets or sets the secret RetroServer is configured with (<c>ProgramToken</c>), or empty when it
        /// runs without one.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name to claim. Default <c>*CHAT</c>, what the SINTRAN CHAT client looks up.
        /// </summary>
        public string Name { get; set; } = "*CHAT";

        /// <summary>
        /// Gets or sets the free connections, one per member. Default 20.
        /// </summary>
        public int Seats { get; set; } = 20;

        /// <summary>
        /// Gets or sets the line a new member is welcomed with.
        /// </summary>
        public string Greeting { get; set; } = "Welcome to chat on RetroServer.";

        /// <summary>
        /// Reads the settings file, writing one with the defaults when there is none.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <param name="problem">
        /// Why the file could not be used, or null.
        /// </param>
        /// <returns>
        /// The settings, or null when the file exists but cannot be read.
        /// </returns>
        public static ChatServerSettings? Load(string path, out string? problem)
        {
            problem = null;
            JsonSerializerOptions json = new JsonSerializerOptions { WriteIndented = true };
            try
            {
                if (!File.Exists(path))
                {
                    ChatServerSettings fresh = new ChatServerSettings();
                    File.WriteAllText(path, JsonSerializer.Serialize(fresh, json));
                    return fresh;
                }

                return JsonSerializer.Deserialize<ChatServerSettings>(File.ReadAllText(path)) ?? new ChatServerSettings();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                problem = ex.Message;
                return null;
            }
        }
    }
}
