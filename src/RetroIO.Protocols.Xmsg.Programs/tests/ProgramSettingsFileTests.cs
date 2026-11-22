//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

using RetroIO.Protocols.Xmsg.Programs.Settings;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Programs.Tests
{
    /// <summary>
    /// The settings file every program keeps next to itself.
    /// </summary>
    public sealed class ProgramSettingsFileTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "settings-test-" + Guid.NewGuid().ToString("N") + ".json");

        /// <summary>
        /// A settings type with one default.
        /// </summary>
        public sealed class Sample
        {
            /// <summary>Gets or sets the name.</summary>
            public string Name { get; set; } = "*CHAT";

            /// <summary>Gets or sets the seats.</summary>
            public int Seats { get; set; } = 20;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        /// <summary>
        /// No file: the defaults are returned AND written, so the user has a file to edit.
        /// </summary>
        [Fact]
        public void MissingFile_WritesAndReturnsTheDefaults()
        {
            Sample? s = ProgramSettingsFile.Load<Sample>(_path, out string? problem);

            Assert.Null(problem);
            Assert.NotNull(s);
            Assert.Equal("*CHAT", s!.Name);
            Assert.True(File.Exists(_path));
            Assert.Contains("\"Seats\": 20", File.ReadAllText(_path));
        }

        /// <summary>
        /// A file is read; a value left out keeps its default.
        /// </summary>
        [Fact]
        public void ExistingFile_IsRead_MissingValuesKeepTheirDefaults()
        {
            File.WriteAllText(_path, "{ \"Name\": \"*TALK\" }");

            Sample? s = ProgramSettingsFile.Load<Sample>(_path, out string? problem);

            Assert.Null(problem);
            Assert.Equal("*TALK", s!.Name);
            Assert.Equal(20, s.Seats);
        }

        /// <summary>
        /// A broken file is not replaced; the program is told why it cannot be used.
        /// </summary>
        [Fact]
        public void BrokenFile_GivesAProblem_AndIsLeftAlone()
        {
            File.WriteAllText(_path, "{ not json");

            Sample? s = ProgramSettingsFile.Load<Sample>(_path, out string? problem);

            Assert.Null(s);
            Assert.NotNull(problem);
            Assert.Equal("{ not json", File.ReadAllText(_path));
        }
    }
}
