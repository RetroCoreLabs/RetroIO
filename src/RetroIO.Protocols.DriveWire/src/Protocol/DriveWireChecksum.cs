//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Protocol;

/// <summary>
/// The 16-bit checksum DriveWire sends with every sector.
/// </summary>
/// <remarks>
/// <para>
/// The checksum is the sum of all 256 sector bytes, each taken as an unsigned value, kept to 16 bits. It goes on the wire
/// high byte first ("DriveWire Protocol Version 4.0.0", "The Write Transaction": byte 261 is bits 15-8, byte 262 bits 7-0).
/// </para>
/// <para>
/// The specification's prose says the checksum is computed "on the 256 bytes of sector data", but its sample C routine in
/// Appendix B ("DriveWire Checksum Algorithm") loops with while(--count), which adds only the first 255 bytes. The client is
/// what the server must agree with, so this follows the NitrOS-9 client: dwcheck.asm DoCSum adds every byte of the
/// 256-byte sector into D (addb ,x+ then adca #0, 256 times), and the Becker port read routine dwread_becker.asm adds every
/// received byte into X with abx and returns it as the checksum. Both cover all 256 bytes.
/// </para>
/// </remarks>
public static class DriveWireChecksum
{
    /// <summary>
    /// Computes the checksum of a block of bytes.
    /// </summary>
    /// <param name="data">
    /// The bytes, normally one 256-byte sector.
    /// </param>
    /// <returns>
    /// The sum of the bytes, kept to 16 bits.
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            sum += data[i];
        }

        return (ushort)sum;
    }
}
