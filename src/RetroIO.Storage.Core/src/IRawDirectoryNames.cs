//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// A driver that can read and write directory names, and the disk name and id, as the raw bytes
    /// that are stored, for directory art.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Directory art is a run of directory entries whose names hold graphic characters, so the
    /// machine's own directory listing draws a picture. The lines between files are separator entries
    /// with no data. A program that offers directory art finds out from <see cref="RawLayout"/> how
    /// long a name is, which byte pads it, which character set it is in and what kind of entry a
    /// separator is; it names no format itself.
    /// </para>
    /// <para>
    /// Every byte is stored exactly as given; there is no check on the name. The driver's normal
    /// name methods (<see cref="IFileSystemDriver.WriteFile"/> and the rest) keep their own rules.
    /// </para>
    /// <para><b>The rule</b></para>
    /// A test over every driver that implements this interface checks that a name, a separator and a
    /// disk header written as bytes read back as the same bytes, that the lengths agree with
    /// <see cref="RawLayout"/>, and that the files on the disk keep their data.
    /// </remarks>
    public interface IRawDirectoryNames
    {
        /// <summary>
        /// Gets the facts a program needs to write raw names on this disk.
        /// </summary>
        RawDirectoryLayout RawLayout { get; }

        /// <summary>
        /// Reads every listed directory entry with its raw name bytes, in listing order.
        /// </summary>
        /// <returns>
        /// The entries; <see cref="RawDirectoryEntry.Index"/> counts from 0 in this order.
        /// </returns>
        RawDirectoryEntry[] ReadRawDirectory();

        /// <summary>
        /// Turns typed text into the bytes this disk stores for it, by the same rule the driver uses
        /// for host file names, so a program can offer text input for raw names without knowing the
        /// disk's character set.
        /// </summary>
        /// <param name="text">
        /// The text; it is not padded or cut to a length.
        /// </param>
        /// <returns>
        /// One byte for each character of <paramref name="text"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="text"/> holds a character the disk has no byte for; the message
        /// names it.
        /// </exception>
        byte[] EncodeText(string text);

        /// <summary>
        /// Replaces the name bytes of one entry.
        /// </summary>
        /// <param name="index">
        /// The entry's <see cref="RawDirectoryEntry.Index"/>.
        /// </param>
        /// <param name="name">
        /// Exactly <see cref="RawDirectoryLayout.NameLength"/> bytes, stored as given.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> has the wrong length.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when no entry has <paramref name="index"/>.
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when the disk is mounted read-only.
        /// </exception>
        void WriteRawName(int index, ReadOnlySpan<byte> name);

        /// <summary>
        /// Puts a separator entry with a raw name at a place in the listing; the entries from that
        /// place on move down one.
        /// </summary>
        /// <param name="position">
        /// The <see cref="RawDirectoryEntry.Index"/> the new entry gets, from 0 to the number of
        /// entries (the end).
        /// </param>
        /// <param name="name">
        /// Exactly <see cref="RawDirectoryLayout.NameLength"/> bytes, stored as given.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> has the wrong length.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="position"/> is past the end.
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when the disk is mounted read-only or its directory is full.
        /// </exception>
        void InsertSeparator(int position, ReadOnlySpan<byte> name);

        /// <summary>
        /// Reads the disk name as stored.
        /// </summary>
        /// <returns>
        /// <see cref="RawDirectoryLayout.VolumeNameLength"/> bytes.
        /// </returns>
        byte[] ReadRawVolumeName();

        /// <summary>
        /// Reads the disk id as stored.
        /// </summary>
        /// <returns>
        /// <see cref="RawDirectoryLayout.VolumeIdLength"/> bytes.
        /// </returns>
        byte[] ReadRawVolumeId();

        /// <summary>
        /// Replaces the disk name and the disk id with raw bytes.
        /// </summary>
        /// <param name="name">
        /// Exactly <see cref="RawDirectoryLayout.VolumeNameLength"/> bytes, stored as given.
        /// </param>
        /// <param name="id">
        /// Exactly <see cref="RawDirectoryLayout.VolumeIdLength"/> bytes, stored as given.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when a value has the wrong length.
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when the disk is mounted read-only.
        /// </exception>
        void WriteRawVolumeHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> id);
    }

    /// <summary>
    /// What a disk's raw directory names look like, given by its driver as data.
    /// </summary>
    public sealed class RawDirectoryLayout
    {
        /// <summary>
        /// Gets the length of a directory name in bytes.
        /// </summary>
        public int NameLength { get; init; }

        /// <summary>
        /// Gets the length of the disk name in bytes.
        /// </summary>
        public int VolumeNameLength { get; init; }

        /// <summary>
        /// Gets the length of the disk id in bytes.
        /// </summary>
        public int VolumeIdLength { get; init; }

        /// <summary>
        /// Gets the byte that fills a name shorter than <see cref="NameLength"/>.
        /// </summary>
        public byte PadByte { get; init; }

        /// <summary>
        /// Gets the name of the character set the bytes are in, for a reader (for example <c>PETSCII</c>).
        /// </summary>
        public string CharacterSet { get; init; } = string.Empty;

        /// <summary>
        /// Gets the type name a separator entry is listed with (for example <c>DEL</c>).
        /// </summary>
        public string SeparatorTypeName { get; init; } = string.Empty;

        /// <summary>
        /// Gets a sentence for a reader on what these names are and what a separator is.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// Gets, for each of the 256 byte values, the glyph it is drawn with: its index in one
        /// character set of the machine's character ROM. Empty when the driver gives no drawing.
        /// </summary>
        /// <remarks>
        /// A program draws a name by reading <see cref="GlyphHeight"/> bytes at
        /// set start + index times <see cref="GlyphHeight"/> from a character ROM file the user
        /// supplies; RetroIO ships no ROM. Each byte is one row, the top bit the left pixel. An
        /// inverted glyph is a glyph of its own in the ROM and is read from there, never made by
        /// flipping bits.
        /// </remarks>
        public byte[] GlyphIndex { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Gets the width of a glyph in pixels (one row is one byte, so at most 8).
        /// </summary>
        public int GlyphWidth { get; init; }

        /// <summary>
        /// Gets the height of a glyph in pixels, which is also its number of bytes in the ROM.
        /// </summary>
        public int GlyphHeight { get; init; }

        /// <summary>
        /// Gets the size in bytes a character ROM file for this disk has.
        /// </summary>
        public int CharacterRomSize { get; init; }

        /// <summary>
        /// Gets the character sets in the ROM: where each starts and what it is called. The first
        /// is the one the machine lists a directory in by default.
        /// </summary>
        public CharacterSetPlace[] CharacterSets { get; init; } = Array.Empty<CharacterSetPlace>();

        /// <summary>
        /// Gets a sentence naming the character ROM the drawing expects, for a reader.
        /// </summary>
        public string CharacterRomDescription { get; init; } = string.Empty;
    }

    /// <summary>
    /// One character set in a character ROM.
    /// </summary>
    public sealed class CharacterSetPlace
    {
        /// <summary>
        /// Gets the byte offset in the ROM where the set starts.
        /// </summary>
        public int Offset { get; init; }

        /// <summary>
        /// Gets the set's name for a reader (for example "upper case and graphics").
        /// </summary>
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>
    /// One directory entry with its name as the raw bytes that are stored.
    /// </summary>
    public sealed class RawDirectoryEntry
    {
        /// <summary>
        /// Gets the place in the listing, from 0.
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// Gets the entry's type as the listing names it (for example <c>PRG</c> or <c>DEL</c>).
        /// </summary>
        public string TypeName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the name bytes as stored, padding included.
        /// </summary>
        public byte[] Name { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Gets the size the entry states, in the disk's own blocks.
        /// </summary>
        public int Blocks { get; init; }

        /// <summary>
        /// Gets whether the entry is a separator: a <see cref="RawDirectoryLayout.SeparatorTypeName"/>
        /// entry with no data.
        /// </summary>
        public bool IsSeparator { get; init; }
    }
}
