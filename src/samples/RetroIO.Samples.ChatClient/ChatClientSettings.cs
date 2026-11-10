//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text.Json;

namespace RetroIO.Samples.ChatClient
{
    /// <summary>
    /// The settings of <c>retro-chat</c>, read from <c>retro-chat.json</c> next to the program.
    /// Command-line flags override them for one run only.
    /// </summary>
    /// <remarks>
    /// RetroServer keeps no settings for this program; the only thing the two must agree on is
    /// <see cref="Token"/> (Ronny, 2026-11-10).
    /// </remarks>
    public sealed class ChatClientSettings
    {
        /// <summary>
        /// The file name, next to the program.
        /// </summary>
        public const string FileName = "retro-chat.json";

        /// <summary>
        /// Gets or sets RetroServer's program port. Default <c>http://127.0.0.1:5011</c>.
        /// </summary>
        public string Node { get; set; } = "http://127.0.0.1:5011";

        /// <summary>
        /// Gets or sets the secret RetroServer is configured with (<c>ProgramToken</c>), or empty when it
        /// runs without one.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name to be known by in the room. No default: it must be set.
        /// </summary>
        public string Nick { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the chat service's name. Default <c>*CHAT</c>.
        /// </summary>
        public string Service { get; set; } = "*CHAT";

        /// <summary>
        /// Gets or sets the room to join. Empty means the server's own room.
        /// </summary>
        public string Room { get; set; } = string.Empty;

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
        public static ChatClientSettings? Load(string path, out string? problem)
        {
            problem = null;
            JsonSerializerOptions json = new JsonSerializerOptions { WriteIndented = true };
            try
            {
                if (!File.Exists(path))
                {
                    ChatClientSettings fresh = new ChatClientSettings();
                    File.WriteAllText(path, JsonSerializer.Serialize(fresh, json));
                    return fresh;
                }

                return JsonSerializer.Deserialize<ChatClientSettings>(File.ReadAllText(path)) ?? new ChatClientSettings();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                problem = ex.Message;
                return null;
            }
        }
    }
}
