//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests
{
    /// <summary>
    /// Regression tests for the SFTP SSH-key-authentication fix: SftpVolumeProvider used to throw
    /// unconditionally whenever no password was present in the URI, even though its own XML doc
    /// comment claimed "SSH key authentication (RSA, DSA, ECDSA, ED25519)" was supported. Mount()
    /// never actually connects to a server (constructing the client is purely local key-parsing +
    /// ConnectionInfo setup), so these are safe to run without any real SFTP server.
    /// </summary>
    [TestFixture]
    public class SftpVolumeProviderKeyAuthTests
    {
        private string _tempDir = string.Empty;
        private string _validKeyPath = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "sftp_keyauth_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);

            // A real, valid RSA private key in PKCS#1 PEM — the format SSH.NET's PrivateKeyFile
            // can parse — generated locally, no network/external key material involved.
            using RSA rsa = RSA.Create(2048);
            string pem = rsa.ExportRSAPrivateKeyPem();
            _validKeyPath = Path.Combine(_tempDir, "id_rsa_test");
            File.WriteAllText(_validKeyPath, pem);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Test]
        public void Mount_PasswordInUri_StillWorks()
        {
            // Regression: the pre-existing password path must be untouched by the key-auth addition.
            using var provider = new SftpVolumeProvider();
            var info = provider.Mount("sftp://user:pass@example.com:22/", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("SFTP"));
        }

        [Test]
        public void Mount_ExplicitKeyFile_NoPassword_Succeeds()
        {
            using var provider = new SftpVolumeProvider();
            string uri = $"sftp://user@example.com:22/?keyFile={Uri.EscapeDataString(_validKeyPath)}";

            Assert.DoesNotThrow(() => provider.Mount(uri, "vol1", readOnly: true),
                "a valid, explicit SSH key should be accepted without requiring a password");
        }

        [Test]
        public void Mount_ExplicitKeyFile_NonexistentPath_ThrowsClearly()
        {
            using var provider = new SftpVolumeProvider();
            string missingPath = Path.Combine(_tempDir, "does_not_exist");
            string uri = $"sftp://user@example.com:22/?keyFile={Uri.EscapeDataString(missingPath)}";

            Assert.Throws<FileNotFoundException>(() => provider.Mount(uri, "vol1", readOnly: true));
        }

        [Test]
        public void Mount_NoPasswordNoKeyFile_UsesDefaultLocationOrThrowsClearly()
        {
            // This machine has no ~/.ssh/id_* files (verified before writing this test), so this
            // should throw. On a machine that DOES have a default key, Mount would instead succeed
            // via FindDefaultPrivateKey — both are correct behavior, so this only asserts the
            // failure message is the new, clear one (not the old blanket "provide password" error)
            // when it does throw, rather than asserting throwing is mandatory.
            using var provider = new SftpVolumeProvider();
            string uri = "sftp://user@example.com:22/";

            try
            {
                provider.Mount(uri, "vol1", readOnly: true);
                Assert.Pass("A default SSH key was found on this machine and used — also correct behavior.");
            }
            catch (ArgumentException ex)
            {
                Assert.That(ex.Message, Does.Contain("no ?keyFile="),
                    "error message should mention the new key-auth option, not just demand a password");
            }
        }
    }
}
