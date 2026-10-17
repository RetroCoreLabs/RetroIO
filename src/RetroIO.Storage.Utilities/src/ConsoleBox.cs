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
        public const int InnerWidth = 71;

        private const char TopLeft = '╔';
        private const char TopRight = '╗';
        private const char BottomLeft = '╚';
        private const char BottomRight = '╝';
        private const char Side = '║';
        private const char Horizontal = '═';
        private const char DividerLeft = '╠';
        private const char DividerRight = '╣';

        public static void Top(int width = InnerWidth) => WriteBorder(TopLeft, Horizontal, TopRight, width);

        public static void Bottom(int width = InnerWidth) => WriteBorder(BottomLeft, Horizontal, BottomRight, width);

        public static void Divider(int width = InnerWidth) => WriteBorder(DividerLeft, Horizontal, DividerRight, width);

        public static void Title(string title, int width = InnerWidth) => Console.WriteLine(FormatTitle(title, width));

        public static void Line(string content, int width = InnerWidth) => Console.WriteLine(FormatLine(content, width));

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

        public static void Bullet(string name, object? value, int valueColumn = 24, int width = InnerWidth)
        {
            string bullet = $"    • {name}:";
            int spaces = valueColumn - bullet.Length;
            if (spaces < 1)
                spaces = 1;

            Line(bullet + new string(' ', spaces) + (value?.ToString() ?? string.Empty), width);
        }

        public static string FormatTitle(string title, int width = InnerWidth)
        {
            string content = Truncate(title, width);
            int padTotal = width - content.Length;
            int padLeft = padTotal / 2;
            int padRight = padTotal - padLeft;
            return $"{Side}{new string(' ', padLeft)}{content}{new string(' ', padRight)}{Side}";
        }

        public static string FormatLine(string content, int width = InnerWidth)
        {
            return $"{Side}{Truncate(content, width).PadRight(width)}{Side}";
        }

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
