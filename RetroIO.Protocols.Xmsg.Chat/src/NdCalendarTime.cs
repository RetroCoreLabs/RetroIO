using System;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// A moment as the ND-100 itself reports it, straight from the SINTRAN clock.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not a DateTime, a tick count or a string</b></para>
    /// <para>
    /// This is the ND's own calendar and nothing else. <c>MN113</c> (CLOCK) fills a seven-element
    /// array - basic units, second, minute, hour, day, month, year - and the six that matter are
    /// carried and stored exactly as the machine gave them. Ronny's call, 2026-08-28, and it is
    /// also the only form that cannot drift: an epoch needs a zero point the ND does not share,
    /// and text needs a format both ends must agree on for ever.
    /// </para>
    /// <para>
    /// Each ND runs its own clock and they are not synchronised, so a time is only meaningful
    /// alongside the machine that produced it. That is why history does not cross a trunk.
    /// </para>
    /// <para><b>The year is the FULL year</b></para>
    /// <para>
    /// <c>MN113</c> answers 1998, not 98. Halving it into a byte was a real defect once - a
    /// three-digit value indexed past the end of a ten-byte literal and printed a blank where a
    /// digit belonged - so the year is carried as two bytes and never truncated.
    /// </para>
    /// <para><b>Unknown is a real answer</b></para>
    /// <para>
    /// A history block written before the time was recorded has no time in it, and saying so is
    /// better than inventing one. An all-zero value means exactly that, and
    /// <see cref="IsKnown"/> is how to ask.
    /// </para>
    /// </remarks>
    public readonly struct NdCalendarTime : IEquatable<NdCalendarTime>
    {
        /// <summary>
        /// A time that was never recorded.
        /// </summary>
        public static readonly NdCalendarTime Unknown = default;

        /// <summary>
        /// Initialises a moment from the ND calendar fields.
        /// </summary>
        /// <param name="year">
        /// The full year, for example 1998.
        /// </param>
        /// <param name="month">
        /// The month, 1 to 12.
        /// </param>
        /// <param name="day">
        /// The day of the month, 1 to 31.
        /// </param>
        /// <param name="hour">
        /// The hour, 0 to 23.
        /// </param>
        /// <param name="minute">
        /// The minute, 0 to 59.
        /// </param>
        /// <param name="second">
        /// The second, 0 to 59.
        /// </param>
        public NdCalendarTime(ushort year, byte month, byte day, byte hour, byte minute, byte second)
        {
            Year = year;
            Month = month;
            Day = day;
            Hour = hour;
            Minute = minute;
            Second = second;
        }

        /// <summary>
        /// The full year as the ND reports it, for example 1998.
        /// </summary>
        public ushort Year { get; }

        /// <summary>
        /// The month, 1 to 12.
        /// </summary>
        public byte Month { get; }

        /// <summary>
        /// The day of the month, 1 to 31.
        /// </summary>
        public byte Day { get; }

        /// <summary>
        /// The hour, 0 to 23.
        /// </summary>
        public byte Hour { get; }

        /// <summary>
        /// The minute, 0 to 59.
        /// </summary>
        public byte Minute { get; }

        /// <summary>
        /// The second, 0 to 59.
        /// </summary>
        public byte Second { get; }

        /// <summary>
        /// Whether this is a time at all, rather than the absence of one.
        /// </summary>
        /// <remarks>
        /// The year is the test, not the whole value. Midnight exactly - hour, minute and second
        /// all zero - is a perfectly good time and must not read as unknown, while a year of zero
        /// is not a date any ND ever produced.
        /// </remarks>
        public bool IsKnown
        {
            get { return Year != 0; }
        }

        /// <summary>
        /// How many bytes <see cref="WriteTo"/> writes.
        /// </summary>
        public const int ByteCount = 7;

        /// <summary>
        /// Writes the time in wire order.
        /// </summary>
        /// <param name="destination">
        /// The buffer to write into. Must hold at least <see cref="ByteCount"/> bytes.
        /// </param>
        /// <remarks>
        /// Second first and the year last, big-endian like every other two-byte field in this
        /// protocol. The order matches the words the ND itself hands back, smallest unit first,
        /// so a trace reads the same way the machine does.
        /// </remarks>
        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < ByteCount)
            {
                throw new ArgumentException(
                    "an ND calendar time needs " + ByteCount + " bytes", nameof(destination));
            }

            destination[0] = Second;
            destination[1] = Minute;
            destination[2] = Hour;
            destination[3] = Day;
            destination[4] = Month;
            destination[5] = (byte)(Year >> 8);
            destination[6] = (byte)(Year & 0xFF);
        }

        /// <summary>
        /// Reads a time written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="source">
        /// The buffer to read from. Must hold at least <see cref="ByteCount"/> bytes.
        /// </param>
        /// <returns>
        /// The time, or <see cref="Unknown"/> when the year reads as zero.
        /// </returns>
        public static NdCalendarTime ReadFrom(ReadOnlySpan<byte> source)
        {
            if (source.Length < ByteCount)
            {
                throw new ArgumentException(
                    "an ND calendar time needs " + ByteCount + " bytes", nameof(source));
            }

            ushort year = (ushort)((source[5] << 8) | source[6]);

            return new NdCalendarTime(
                year, source[4], source[3], source[2], source[1], source[0]);
        }

        /// <inheritdoc />
        public bool Equals(NdCalendarTime other)
        {
            return Year == other.Year
                && Month == other.Month
                && Day == other.Day
                && Hour == other.Hour
                && Minute == other.Minute
                && Second == other.Second;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is NdCalendarTime other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(Year, Month, Day, Hour, Minute, Second);
        }

        /// <summary>
        /// Renders the time the way the chat screen shows it.
        /// </summary>
        /// <returns>
        /// <c>HH:MM</c>, or an empty string when the time is not known.
        /// </returns>
        /// <remarks>
        /// Empty rather than a placeholder like <c>--:--</c>: the column is fixed width on the
        /// screen and the caller pads it, so an empty string leaves a blank where a wrong time
        /// used to be.
        /// </remarks>
        public override string ToString()
        {
            if (!IsKnown)
            {
                return string.Empty;
            }

            return Hour.ToString("00") + ":" + Minute.ToString("00");
        }
    }
}
