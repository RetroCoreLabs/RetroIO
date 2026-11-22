//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text.Json;

namespace RetroIO.Protocols.Xmsg.Programs.Settings
{
    /// <summary>
    /// A program's settings file: JSON next to the program, written with the defaults on the first
    /// run, read on every later one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every XMSG program keeps its settings this way (plan, Phase 3). Command-line flags then
    /// override the values for one run only; that part stays in each program, because only the
    /// program knows its flags.
    /// </para>
    /// <para>
    /// Until 2026-11-22 the same reading code was copied in both chat samples.
    /// </para>
    /// </remarks>
    public static class ProgramSettingsFile
    {
        /// <summary>
        /// Reads the settings file, writing one with the defaults when there is none.
        /// </summary>
        /// <typeparam name="T">
        /// The settings type; a new instance holds the defaults.
        /// </typeparam>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <param name="problem">
        /// Why the file could not be used, or null.
        /// </param>
        /// <returns>
        /// The settings, or null when the file exists but cannot be read, or the defaults cannot be
        /// written.
        /// </returns>
        public static T? Load<T>(string path, out string? problem)
            where T : class, new()
        {
            problem = null;
            JsonSerializerOptions json = new JsonSerializerOptions { WriteIndented = true };
            try
            {
                if (!File.Exists(path))
                {
                    T fresh = new T();
                    File.WriteAllText(path, JsonSerializer.Serialize(fresh, json));
                    return fresh;
                }

                // A file holding only "null" gives the defaults rather than nothing.
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                problem = ex.Message;
                return null;
            }
        }
    }
}
