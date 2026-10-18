//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Turns a Windows file name into a legal SINTRAN name and type.
    /// </summary>
    /// <remarks>
    /// <para><b>The limits come from the manual, not from a capture</b></para>
    /// <para>
    /// <c>ND-60.050.06 SINTRAN III Users Guide</c>: "file-directory-name, owner name, and
    /// filename may consist of 1 to 16 characters, filetype may consist of 1 to 4 characters,
    /// and version-number range is from 1 to 256." <c>ND-60.128.5 SINTRAN III Reference Manual</c>
    /// adds, for a directory name, "a maximum of 16 alphanumeric characters, including the
    /// hyphen -, is legal."
    /// </para>
    /// <para>
    /// A capture would only ever show which names happened to be used, so the manual is the better
    /// source here - this is precisely the case the project rule about grepping the reference
    /// manuals first is for.
    /// </para>
    /// <para><b>A type is not an extension</b></para>
    /// <para>
    /// On SINTRAN the type is a separate field, not a suffix of the name, and it is at most four
    /// characters. <c>Program.c</c> is not <c>Program.c</c>; it is name <c>PROGRAM</c> with type
    /// <c>C</c>. Treating the whole Windows name as the SINTRAN name is the obvious mistake and
    /// produces a file the compiler cannot find by type.
    /// </para>
    /// <para><b>Truncation is refused, not performed</b></para>
    /// <para>
    /// A name over 16 characters is REJECTED rather than silently shortened. Two local files that
    /// truncate to the same SINTRAN name would overwrite each other on the machine, and the loop
    /// would look like "my edits sometimes do not arrive". The caller is told, and configures an
    /// explicit mapping for that file.
    /// </para>
    /// </remarks>
    public static class SintranFileName
    {
        /// <summary>
        /// Longest legal file name, in characters.
        /// </summary>
        public const int MaxNameLength = 16;

        /// <summary>
        /// Longest legal file type, in characters.
        /// </summary>
        public const int MaxTypeLength = 4;

        /// <summary>
        /// Converts a Windows file name into a SINTRAN name and type.
        /// </summary>
        /// <param name="windowsFileName">
        /// The local file name, with no directory part - for example <c>Program.symb</c>.
        /// </param>
        /// <param name="name">
        /// Set to the SINTRAN name, upper-cased, or an empty string when the conversion fails.
        /// </param>
        /// <param name="type">
        /// Set to the SINTRAN type, upper-cased and without the dot, or an empty string when the
        /// local name has no extension.
        /// </param>
        /// <param name="problem">
        /// Set to a sentence describing why the conversion failed, or an empty string on success.
        /// </param>
        /// <returns>
        /// <c>true</c> when a legal name and type were produced.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="windowsFileName"/> is null.
        /// </exception>
        /// <remarks>
        /// Deliberately reports a REASON rather than just failing. "PROGRAM-WITH-A-LONG-NAME:SYMB
        /// is 21 characters, over the 16 SINTRAN allows" is something a person can act on; a bare
        /// false is not.
        /// </remarks>
        public static bool TryConvert(
            string windowsFileName,
            out string name,
            out string type,
            out string problem)
        {
            if (windowsFileName == null)
            {
                throw new ArgumentNullException(nameof(windowsFileName));
            }

            name = string.Empty;
            type = string.Empty;
            problem = string.Empty;

            if (windowsFileName.Length == 0)
            {
                problem = "The file name is empty.";
                return false;
            }

            // Split on the LAST dot: "a.b.symb" is name "a.b" as far as Windows is concerned, and
            // the type is what follows the final separator.
            int dot = windowsFileName.LastIndexOf('.');
            string rawName;
            string rawType;
            if (dot < 0)
            {
                rawName = windowsFileName;
                rawType = string.Empty;
            }
            else
            {
                rawName = windowsFileName.Substring(0, dot);
                rawType = windowsFileName.Substring(dot + 1);
            }

            if (rawName.Length == 0)
            {
                problem = "'" + windowsFileName + "' has no name before its extension.";
                return false;
            }

            if (rawName.Length > MaxNameLength)
            {
                problem = "'" + rawName + "' is " + rawName.Length + " characters; SINTRAN allows "
                    + MaxNameLength + ". Configure an explicit mapping rather than truncating - two "
                    + "files shortened to the same name would overwrite each other on the machine.";
                return false;
            }

            if (rawType.Length > MaxTypeLength)
            {
                problem = "The type '" + rawType + "' is " + rawType.Length + " characters; SINTRAN "
                    + "allows " + MaxTypeLength + ".";
                return false;
            }

            string upperName = rawName.ToUpperInvariant();
            string upperType = rawType.ToUpperInvariant();

            if (!IsLegal(upperName, out char badName))
            {
                problem = "'" + rawName + "' contains '" + badName
                    + "', which is not legal in a SINTRAN name. Letters, digits and the hyphen are.";
                return false;
            }

            if (!IsLegal(upperType, out char badType))
            {
                problem = "The type '" + rawType + "' contains '" + badType + "', which is not legal.";
                return false;
            }

            name = upperName;
            type = upperType;
            return true;
        }

        /// <summary>
        /// Builds the <c>NAME:TYPE</c> text SINTRAN uses for a file.
        /// </summary>
        /// <param name="name">
        /// The SINTRAN name.
        /// </param>
        /// <param name="type">
        /// The SINTRAN type, or an empty string for none.
        /// </param>
        /// <returns>
        /// The file specification, for example <c>PROGRAM:SYMB</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> or <paramref name="type"/> is null.
        /// </exception>
        /// <remarks>
        /// <para>
        /// No quotes are added. Quoting is the CALLER's business, and there is no single rule to
        /// bake in - the two forms Ronny corrected live do NOT agree with each other:
        /// </para>
        /// <code>
        /// remote  COPY-FILE D102(SYSTEM)."BLKT7777:DATA",BRF-LINKER-C01:PROG
        ///         quotes around the NAME AND TYPE only; machine and user OUTSIDE
        /// local   BINARY-DUMP "(UTILITY)ENCOSE0-DUMP:BPUN",ENCOSE0,0,47777
        ///         quotes around the USER PREFIX as well; putting them after the ( gives
        ///         ILLEGAL CHARACTER IN PARAMETER
        /// </code>
        /// <para>
        /// Both are verified against a machine and both are corrections of a wrong guess of mine,
        /// so neither should be generalised into the other. Quotes appear ONLY when a file is
        /// being CREATED; an existing file takes none, and <c>CREATE-FILE</c> takes none either.
        /// <c>LIST-FILES</c> DISPLAYS a remote file as <c>D102.(SYSTEM)NAME</c>, which is not
        /// input syntax at all.
        /// </para>
        /// <para>
        /// The remote form is built by <see cref="SyncFolderMap.BuildFileSpec"/>. The local form
        /// has no builder yet; write one when something needs it rather than assuming this one
        /// fits.
        /// </para>
        /// </remarks>
        public static string ToFileSpec(string name, string type)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (type.Length == 0)
            {
                return name;
            }

            StringBuilder builder = new StringBuilder(name.Length + 1 + type.Length);
            builder.Append(name);
            builder.Append(':');
            builder.Append(type);
            return builder.ToString();
        }

        /// <summary>
        /// Builds the <c>NAME.TYPE</c> text Windows uses, for a file coming BACK from SINTRAN.
        /// </summary>
        /// <param name="name">
        /// The SINTRAN name.
        /// </param>
        /// <param name="type">
        /// The SINTRAN type, or an empty string for none.
        /// </param>
        /// <returns>
        /// The Windows file name, for example <c>PROGRAM.SYMB</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> or <paramref name="type"/> is null.
        /// </exception>
        /// <remarks>
        /// The separator swaps with the direction of travel - <c>:</c> on the machine, <c>.</c> on
        /// Windows - and it is the ONLY thing that changes. <c>A:MODE</c> goes out and
        /// <c>A.MODE</c> comes back, so a round trip returns the name it started with.
        /// </remarks>
        public static string ToWindowsFileName(string name, string type)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (type.Length == 0)
            {
                return name;
            }

            StringBuilder builder = new StringBuilder(name.Length + 1 + type.Length);
            builder.Append(name);
            builder.Append('.');
            builder.Append(type);
            return builder.ToString();
        }

        /// <summary>
        /// Splits a SINTRAN <c>NAME:TYPE</c> into its two fields, checking both against the limits.
        /// </summary>
        /// <param name="fileSpec">
        /// The specification as the machine reports it, for example <c>LOAD-MODE:BATC</c>. A
        /// leading user or machine prefix is NOT accepted here - strip it first.
        /// </param>
        /// <param name="name">
        /// Set to the name, upper-cased, or an empty string when the split fails.
        /// </param>
        /// <param name="type">
        /// Set to the type, upper-cased and without the colon, or an empty string when there is
        /// none.
        /// </param>
        /// <param name="problem">
        /// Set to a sentence describing why the split failed, or an empty string on success.
        /// </param>
        /// <returns>
        /// <c>true</c> when a legal name and type were produced.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The direction that brings BUILD OUTPUT back - <c>:BPUN</c>, <c>:SYMB</c>, <c>:LIST</c>
        /// - so it has to be as strict as the outgoing one. A machine can hold a name this code
        /// would not have sent, and carrying it to Windows unchecked is how a file arrives with a
        /// name nothing local can open.
        /// </para>
        /// <para>
        /// Splits on the FIRST colon, unlike the Windows direction which splits on the LAST dot. A
        /// SINTRAN name cannot contain a colon at all, so the first one is the separator; a
        /// Windows name can contain any number of dots, so only the last one is.
        /// </para>
        /// </remarks>
        public static bool TryParseFileSpec(
            string fileSpec,
            out string name,
            out string type,
            out string problem)
        {
            if (fileSpec == null)
            {
                throw new ArgumentNullException(nameof(fileSpec));
            }

            name = string.Empty;
            type = string.Empty;
            problem = string.Empty;

            string trimmed = fileSpec.Trim();
            if (trimmed.Length == 0)
            {
                problem = "The file specification is empty.";
                return false;
            }

            int colon = trimmed.IndexOf(':');
            string rawName = colon < 0 ? trimmed : trimmed.Substring(0, colon);
            string rawType = colon < 0 ? string.Empty : trimmed.Substring(colon + 1);

            if (rawName.Length == 0)
            {
                problem = "'" + fileSpec + "' has no name before its type.";
                return false;
            }

            if (rawName.Length > MaxNameLength)
            {
                problem = "'" + rawName + "' is " + rawName.Length + " characters; SINTRAN allows "
                    + MaxNameLength + ", so this is not a name the machine could have given.";
                return false;
            }

            if (rawType.Length > MaxTypeLength)
            {
                problem = "The type '" + rawType + "' is " + rawType.Length + " characters; SINTRAN "
                    + "allows " + MaxTypeLength + ".";
                return false;
            }

            string upperName = rawName.ToUpperInvariant();
            string upperType = rawType.ToUpperInvariant();

            if (!IsLegal(upperName, out char badName))
            {
                problem = "'" + rawName + "' contains '" + badName
                    + "', which is not legal in a SINTRAN name.";
                return false;
            }

            if (!IsLegal(upperType, out char badType))
            {
                problem = "The type '" + rawType + "' contains '" + badType + "', which is not legal.";
                return false;
            }

            name = upperName;
            type = upperType;
            return true;
        }

        /// <summary>
        /// Whether every character is legal in a SINTRAN name.
        /// </summary>
        /// <param name="text">
        /// The upper-cased text to check.
        /// </param>
        /// <param name="offender">
        /// Set to the first illegal character, or a space when all are legal.
        /// </param>
        /// <returns>
        /// <c>true</c> when every character is a letter, a digit or a hyphen.
        /// </returns>
        /// <remarks>
        /// The manual states "alphanumeric characters, including the hyphen" for a directory
        /// name. Applied to file names as well: nothing states a WIDER set for them, and being
        /// strict fails loudly here rather than producing a name the machine refuses later.
        /// </remarks>
        private static bool IsLegal(string text, out char offender)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok)
                {
                    offender = c;
                    return false;
                }
            }

            offender = ' ';
            return true;
        }
    }
}
