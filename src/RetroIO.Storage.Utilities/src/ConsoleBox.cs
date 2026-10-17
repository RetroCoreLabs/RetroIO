//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Utilities
{
    /// <summary>
    /// Renders fixed-width Unicode box borders for console output.
    /// All content lines are padded or truncated to the configured inner width
    /// so the right border always aligns with the top and bottom edges.
    /// </summary>
    public static class ConsoleBox
    {
        /// <summary>
        /// The default inner width of a box in characters, which is 71.
        /// </summary>
        public const int InnerWidth = 71;

        private const char TopLeft = '╔';
        private const char TopRight = '╗';
        private const char BottomLeft = '╚';
        private const char BottomRight = '╝';
        private const char Side = '║';
        private const char Horizontal = '═';
        private const char DividerLeft = '╠';
        private const char DividerRight = '╣';

        /// <summary>
        /// Writes the top border line of a box to the console.
        /// </summary>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void Top(int width = InnerWidth) => WriteBorder(TopLeft, Horizontal, TopRight, width);

        /// <summary>
        /// Writes the bottom border line of a box to the console.
        /// </summary>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void Bottom(int width = InnerWidth) => WriteBorder(BottomLeft, Horizontal, BottomRight, width);

        /// <summary>
        /// Writes a divider line of a box to the console.
        /// </summary>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void Divider(int width = InnerWidth) => WriteBorder(DividerLeft, Horizontal, DividerRight, width);

        /// <summary>
        /// Writes a centered title line of a box to the console.
        /// </summary>
        /// <param name="title">
        /// The title text. It is cut to the width when it is longer.
        /// </param>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void Title(string title, int width = InnerWidth) => Console.WriteLine(FormatTitle(title, width));

        /// <summary>
        /// Writes a content line of a box to the console.
        /// </summary>
        /// <param name="content">
        /// The text. It is padded to the width, or cut when it is longer.
        /// </param>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void Line(string content, int width = InnerWidth) => Console.WriteLine(FormatLine(content, width));

        /// <summary>
        /// Writes a content line made of a label followed by a value.
        /// </summary>
        /// <remarks>
        /// The value is cut with three dots when the line would be longer than the width. A null value is written as an empty text.
        /// </remarks>
        /// <param name="label">
        /// The text before the value.
        /// </param>
        /// <param name="value">
        /// The value. Its ToString text is used.
        /// </param>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void LabelValue(string label, object? value, int width = InnerWidth)
        {
            string valueText = value?.ToString() ?? string.Empty;
            int maxValueLength = width - label.Length;

            if (maxValueLength < 0)
            {
                Line(Truncate(label, width), width);
                return;
            }

            if (valueText.Length > maxValueLength)
                valueText = Truncate(valueText, maxValueLength);

            Line(label + valueText, width);
        }

        /// <summary>
        /// Writes a content line with a bullet, a name and a value.
        /// </summary>
        /// <remarks>
        /// The value starts at the given column, or one space after the name when the name is longer.
        /// </remarks>
        /// <param name="name">
        /// The name after the bullet.
        /// </param>
        /// <param name="value">
        /// The value. Its ToString text is used. A null value is written as an empty text.
        /// </param>
        /// <param name="valueColumn">
        /// The column where the value starts.
        /// </param>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        public static void Bullet(string name, object? value, int valueColumn = 24, int width = InnerWidth)
        {
            string bullet = $"    • {name}:";
            int spaces = valueColumn - bullet.Length;
            if (spaces < 1)
                spaces = 1;

            Line(bullet + new string(' ', spaces) + (value?.ToString() ?? string.Empty), width);
        }

        /// <summary>
        /// Builds a title line with the title centered between the side characters.
        /// </summary>
        /// <param name="title">
        /// The title text. It is cut to the width when it is longer.
        /// </param>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        /// <returns>
        /// The line.
        /// </returns>
        public static string FormatTitle(string title, int width = InnerWidth)
        {
            string content = Truncate(title, width);
            int padTotal = width - content.Length;
            int padLeft = padTotal / 2;
            int padRight = padTotal - padLeft;
            return $"{Side}{new string(' ', padLeft)}{content}{new string(' ', padRight)}{Side}";
        }

        /// <summary>
        /// Builds a content line with the text padded to the width between the side characters.
        /// </summary>
        /// <param name="content">
        /// The text. It is cut to the width when it is longer.
        /// </param>
        /// <param name="width">
        /// The inner width in characters.
        /// </param>
        /// <returns>
        /// The line.
        /// </returns>
        public static string FormatLine(string content, int width = InnerWidth)
        {
            return $"{Side}{Truncate(content, width).PadRight(width)}{Side}";
        }

        /// <summary>
        /// Builds a border line from a left character, a repeated middle character and a right character.
        /// </summary>
        /// <param name="left">
        /// The first character.
        /// </param>
        /// <param name="middle">
        /// The character repeated for the width.
        /// </param>
        /// <param name="right">
        /// The last character.
        /// </param>
        /// <param name="width">
        /// The number of middle characters.
        /// </param>
        /// <returns>
        /// The line.
        /// </returns>
        public static string FormatBorder(char left, char middle, char right, int width = InnerWidth)
        {
            return $"{left}{new string(middle, width)}{right}";
        }

        private static void WriteBorder(char left, char middle, char right, int width)
        {
            Console.WriteLine(FormatBorder(left, middle, right, width));
        }

        private static string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength)
                return text;

            if (maxLength <= 3)
                return text.Substring(0, maxLength);

            return text.Substring(0, maxLength - 3) + "...";
        }
    }
}
