//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RetroIO.Identity;

/// <summary>
/// Argon2id password hashing (PHC string format).
/// </summary>
public static class Argon2idPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 3;
    private const int MemorySizeKiB = 64 * 1024;
    private const int Parallelism = 4;
    private const string Prefix = "$argon2id$v=19$";

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = ComputeHash(password, salt);
        return string.Concat(
            Prefix,
            "m=", MemorySizeKiB.ToString(CultureInfo.InvariantCulture),
            ",t=", Iterations.ToString(CultureInfo.InvariantCulture),
            ",p=", Parallelism.ToString(CultureInfo.InvariantCulture),
            "$",
            Convert.ToBase64String(salt),
            "$",
            Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string encodedHash, out bool upgradedFromLegacy)
    {
        upgradedFromLegacy = false;

        if (string.IsNullOrEmpty(encodedHash))
            return false;

        if (encodedHash.StartsWith(Prefix, StringComparison.Ordinal))
            return VerifyArgon2id(password, encodedHash);

        if (VerifyLegacy(password, encodedHash))
        {
            upgradedFromLegacy = true;
            return true;
        }

        return false;
    }

    public static bool IsLegacyHash(string encodedHash)
    {
        return !string.IsNullOrEmpty(encodedHash) &&
               !encodedHash.StartsWith(Prefix, StringComparison.Ordinal);
    }

    private static bool VerifyArgon2id(string password, string encodedHash)
    {
        string[] parts = encodedHash.Split('$');
        if (parts.Length != 6)
            return false;

        if (!TryParseParameters(parts[3], out int memorySizeKiB, out int iterations, out int parallelism))
            return false;

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            expectedHash = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actualHash = ComputeHash(password, salt, memorySizeKiB, iterations, parallelism);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static bool VerifyLegacy(string password, string encodedHash)
    {
        string legacyHash = Convert.ToBase64String(Encoding.UTF8.GetBytes(password + "_HASHED"));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(legacyHash),
            Encoding.UTF8.GetBytes(encodedHash));
    }

    private static bool TryParseParameters(string segment, out int memorySizeKiB, out int iterations, out int parallelism)
    {
        memorySizeKiB = 0;
        iterations = 0;
        parallelism = 0;

        string[] values = segment.Split(',');
        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i];
            int separator = value.IndexOf('=');
            if (separator <= 0)
                return false;

            string key = value.Substring(0, separator);
            string number = value.Substring(separator + 1);
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return false;

            if (key == "m")
                memorySizeKiB = parsed;
            else if (key == "t")
                iterations = parsed;
            else if (key == "p")
                parallelism = parsed;
        }

        return memorySizeKiB > 0 && iterations > 0 && parallelism > 0;
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        return ComputeHash(password, salt, MemorySizeKiB, Iterations, Parallelism);
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memorySizeKiB, int iterations, int parallelism)
    {
        Argon2id argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memorySizeKiB,
            Iterations = iterations,
        };

        return argon2.GetBytes(HashSize);
    }
}
