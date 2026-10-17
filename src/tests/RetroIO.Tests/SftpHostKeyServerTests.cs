//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests
{
    /// <summary>
    /// Host key checking against a REAL SFTP server. The tests skip themselves unless these environment
    /// variables name a server (any OpenSSH sshd with the sftp subsystem and public-key login will do):
    ///  - RETROIO_TEST_SFTP_HOST, RETROIO_TEST_SFTP_PORT, RETROIO_TEST_SFTP_USER
    ///  - RETROIO_TEST_SFTP_KEYFILE: a private key the server accepts for that user.
    ///  - RETROIO_TEST_SFTP_FINGERPRINT: the server's host key as ssh-keygen -lf prints it, for example SHA256:abc...
    /// They prove what the policy tests cannot: that SSH.NET reports the fingerprint in the form ssh prints,
    /// and that a refused key really stops the connection.
    /// </summary>
    [TestFixture]
    [Category("RealNetwork")]
    public class SftpHostKeyServerTests
    {
        private string _host = null!;
        private string _port = null!;
        private string _user = null!;
        private string _keyFile = null!;
        private string _fingerprint = null!;
        private string _dir = null!;
        private string _knownHosts = null!;

        [SetUp]
        public void SetUp()
        {
            string? host = Environment.GetEnvironmentVariable("RETROIO_TEST_SFTP_HOST");
            string? port = Environment.GetEnvironmentVariable("RETROIO_TEST_SFTP_PORT");
            string? user = Environment.GetEnvironmentVariable("RETROIO_TEST_SFTP_USER");
            string? key = Environment.GetEnvironmentVariable("RETROIO_TEST_SFTP_KEYFILE");
            string? fingerprint = Environment.GetEnvironmentVariable("RETROIO_TEST_SFTP_FINGERPRINT");
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(user) ||
                string.IsNullOrEmpty(key) || string.IsNullOrEmpty(fingerprint))
            {
                Assert.Ignore("Set RETROIO_TEST_SFTP_HOST, _PORT, _USER, _KEYFILE and _FINGERPRINT to run these tests against a real server.");
            }

            _host = host!;
            _port = port!;
            _user = user!;
            _keyFile = key!;
            _fingerprint = fingerprint!;
            _dir = Path.Combine(Path.GetTempPath(), "retroio-sftp-server-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _knownHosts = Path.Combine(_dir, "known-hosts");
        }

        [TearDown]
        public void TearDown()
        {
            if (_dir != null)
            {
                try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Mounts "/" of the server with the given host key options and tries to list it, which opens the connection.
        private void ListRoot(string hostKeyOptions)
        {
            string uri = "sftp://" + _user + "@" + _host + ":" + _port + "/?keyFile=" + Uri.EscapeDataString(_keyFile) +
                         "&knownHosts=" + Uri.EscapeDataString(_knownHosts) + hostKeyOptions;
            using SftpVolumeProvider provider = new SftpVolumeProvider();
            provider.Mount(uri, "vol1", readOnly: true);
            provider.ListDirectory(new UrnContext { VolumeId = "vol1" }, "/");
        }

        [Test]
        public void Strict_UnknownServer_IsRefused_AndTheMessageShowsTheFingerprintThatSshPrints()
        {
            IOException ex = Assert.Throws<IOException>(() => ListRoot(""))!;

            Assert.That(ex.Message, Does.Contain("refused"));
            Assert.That(ex.Message, Does.Contain(_fingerprint), "SSH.NET must report the fingerprint in the form ssh-keygen prints");
            Assert.That(File.Exists(_knownHosts), Is.False);
        }

        [Test]
        public void PinnedCorrectFingerprint_Connects()
        {
            Assert.DoesNotThrow(() => ListRoot("&hostKey=" + Uri.EscapeDataString(_fingerprint)));
        }

        [Test]
        public void PinnedWrongFingerprint_IsRefused()
        {
            string wrong = "SHA256:" + new string('A', 43);

            IOException ex = Assert.Throws<IOException>(() => ListRoot("&hostKey=" + Uri.EscapeDataString(wrong)))!;

            Assert.That(ex.Message, Does.Contain("does not match"));
        }

        [Test]
        public void AcceptNew_RemembersTheServer_ThenStrictConnects_ThenAChangedKeyIsRefused()
        {
            ListRoot("&hostKeyCheck=accept-new");
            Assert.That(File.ReadAllText(_knownHosts), Does.Contain(_fingerprint));

            Assert.DoesNotThrow(() => ListRoot(""), "strict, now that the server is known");

            // Pretend the server's key changed: the recorded fingerprint is no longer the one it presents.
            string recorded = File.ReadAllText(_knownHosts);
            File.WriteAllText(_knownHosts, recorded.Replace(_fingerprint, "SHA256:" + new string('B', 43)));
            IOException ex = Assert.Throws<IOException>(() => ListRoot(""))!;
            Assert.That(ex.Message, Does.Contain("HAS CHANGED"));
        }
    }
}
