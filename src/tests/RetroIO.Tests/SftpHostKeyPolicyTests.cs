//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using Renci.SshNet;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for <see cref="SftpHostKeyPolicy"/> and for how <see cref="SftpVolumeProvider"/> uses it.
    /// The policy is pure decision logic, so no SFTP server is needed.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class SftpHostKeyPolicyTests
    {
        private const string KeyA = "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og";
        private const string KeyB = "uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s";

        private string _dir = null!;
        private string _knownHosts = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "retroio-sftp-hostkey-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _knownHosts = Path.Combine(_dir, "known-hosts");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private SftpHostKeyPolicy Policy(SftpHostKeyMode mode, params string[] pins) => new SftpHostKeyPolicy(mode, pins, _knownHosts);

        [Test]
        public void Strict_UnknownHost_IsRefused_AndTheMessageSaysHowToTrustIt()
        {
            SftpHostKeyDecision d = Policy(SftpHostKeyMode.Strict).Evaluate("files.example", 22, KeyA);

            Assert.That(d.Trust, Is.False);
            Assert.That(d.Reason, Does.Contain("SHA256:" + KeyA));
            Assert.That(d.Reason, Does.Contain("hostKey=").And.Contain("accept-new"));
            Assert.That(File.Exists(_knownHosts), Is.False, "a refusal must not record anything");
        }

        [Test]
        public void PinnedFingerprint_Matches_IsTrusted_WithOrWithoutPrefixAndPadding()
        {
            Assert.That(Policy(SftpHostKeyMode.Strict, KeyA).Evaluate("h", 22, KeyA).Trust, Is.True);
            Assert.That(Policy(SftpHostKeyMode.Strict, "SHA256:" + KeyA).Evaluate("h", 22, KeyA).Trust, Is.True);
            Assert.That(Policy(SftpHostKeyMode.Strict, KeyA + "=").Evaluate("h", 22, "SHA256:" + KeyA).Trust, Is.True);
        }

        [Test]
        public void PinnedFingerprint_DoesNotMatch_IsRefused_EvenInAcceptNewMode()
        {
            SftpHostKeyDecision d = Policy(SftpHostKeyMode.AcceptNew, KeyA).Evaluate("h", 22, KeyB);

            Assert.That(d.Trust, Is.False);
            Assert.That(File.Exists(_knownHosts), Is.False);
        }

        [Test]
        public void SeveralPins_AnyOneMatchingIsEnough()
        {
            Assert.That(Policy(SftpHostKeyMode.Strict, KeyB, KeyA).Evaluate("h", 22, KeyA).Trust, Is.True);
        }

        [Test]
        public void AcceptNew_RemembersTheHostOnFirstUse_AndThenRequiresTheSameKey()
        {
            SftpHostKeyPolicy policy = Policy(SftpHostKeyMode.AcceptNew);

            Assert.That(policy.Evaluate("Files.Example", 2222, KeyA).Trust, Is.True, "first use");
            Assert.That(File.ReadAllText(_knownHosts), Does.Contain("files.example:2222 SHA256:" + KeyA));

            Assert.That(policy.Evaluate("files.example", 2222, KeyA).Trust, Is.True, "same key again");
            Assert.That(policy.Evaluate("files.example", 2222, KeyB).Trust, Is.False, "different key");
        }

        [Test]
        public void ChangedKey_IsRefused_InEveryMode_AndTheFileIsLeftAlone()
        {
            File.WriteAllText(_knownHosts, "# my servers\nfiles.example:22 SHA256:" + KeyA + "\n");
            string before = File.ReadAllText(_knownHosts);

            SftpHostKeyDecision strict = Policy(SftpHostKeyMode.Strict).Evaluate("files.example", 22, KeyB);
            SftpHostKeyDecision acceptNew = Policy(SftpHostKeyMode.AcceptNew).Evaluate("files.example", 22, KeyB);

            Assert.That(strict.Trust, Is.False);
            Assert.That(strict.Reason, Does.Contain("HAS CHANGED"));
            Assert.That(acceptNew.Trust, Is.False);
            Assert.That(File.ReadAllText(_knownHosts), Is.EqualTo(before));
        }

        [Test]
        public void Strict_ListedHostWithTheListedKey_IsTrusted()
        {
            File.WriteAllText(_knownHosts, "files.example:22 SHA256:" + KeyA + "\n");

            Assert.That(Policy(SftpHostKeyMode.Strict).Evaluate("files.example", 22, KeyA).Trust, Is.True);
            Assert.That(Policy(SftpHostKeyMode.Strict).Evaluate("files.example", 23, KeyA).Trust, Is.False, "the port is part of the entry");
        }

        [Test]
        public void Off_TrustsAnything()
        {
            Assert.That(Policy(SftpHostKeyMode.Off).Evaluate("h", 22, KeyB).Trust, Is.True);
        }

        [Test]
        public void FromQuery_WithNoOptions_IsStrict()
        {
            SftpHostKeyPolicy policy = SftpHostKeyPolicy.FromQuery("");

            Assert.That(policy.Mode, Is.EqualTo(SftpHostKeyMode.Strict));
            Assert.That(policy.KnownHostsPath, Does.EndWith(Path.Combine(".retroio", "sftp-known-hosts")));
        }

        [Test]
        public void FromQuery_ReadsMode_Pins_AndKnownHostsPath()
        {
            string query = "?keyFile=x&hostKeyCheck=accept-new&hostKey=SHA256:" + KeyA + "," + KeyB + "&knownHosts=" + Uri.EscapeDataString(_knownHosts);

            SftpHostKeyPolicy policy = SftpHostKeyPolicy.FromQuery(query);

            Assert.That(policy.Mode, Is.EqualTo(SftpHostKeyMode.AcceptNew));
            Assert.That(policy.KnownHostsPath, Is.EqualTo(_knownHosts));
            Assert.That(policy.Evaluate("h", 22, KeyB).Trust, Is.True, "the second pin");
        }

        [Test]
        public void FromQuery_RejectsAnUnknownMode_AndPinsCombinedWithOff()
        {
            Assert.Throws<ArgumentException>(() => SftpHostKeyPolicy.FromQuery("?hostKeyCheck=maybe"));
            Assert.Throws<ArgumentException>(() => SftpHostKeyPolicy.FromQuery("?hostKeyCheck=off&hostKey=" + KeyA));
        }

        [Test]
        public void Mount_AttachesTheHostKeyHandler_SoTheClientNoLongerTrustsEveryServer()
        {
            // SSH.NET trusts any host key when nothing is subscribed to HostKeyReceived. Mount does not
            // connect, so look for the subscriber on the client it built.
            string keyPath = Path.Combine(_dir, "id_rsa_test");
            using (RSA rsa = RSA.Create(2048))
                File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());

            using SftpVolumeProvider provider = new SftpVolumeProvider();
            provider.Mount("sftp://tester@files.example:22/data?keyFile=" + Uri.EscapeDataString(keyPath), "vol1", readOnly: true);

            Delegate? subscribers = HostKeyReceivedSubscribers(provider, "vol1");

            Assert.That(subscribers, Is.Not.Null, "no handler is attached to HostKeyReceived");
        }

        [Test]
        public void Mount_WithABadHostKeyOption_FailsBeforeAnythingIsCreated()
        {
            using SftpVolumeProvider provider = new SftpVolumeProvider();

            Assert.Throws<ArgumentException>(() => provider.Mount("sftp://tester:pw@files.example/data?hostKeyCheck=maybe", "vol1", readOnly: true));
            Assert.That(provider.ListMounts(), Is.Empty);
        }

        // Finds the SftpClient inside the provider's mount table and returns its HostKeyReceived delegate.
        private static Delegate? HostKeyReceivedSubscribers(SftpVolumeProvider provider, string volumeId)
        {
            FieldInfo? mountsField = typeof(SftpVolumeProvider).GetField("_mounts", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(mountsField, Is.Not.Null, "SftpVolumeProvider no longer has a _mounts field; update this test");
            object mounts = mountsField!.GetValue(provider)!;

            object?[] args = new object?[] { volumeId, null };
            MethodInfo tryGet = mounts.GetType().GetMethod("TryGetValue")!;
            Assert.That((bool)tryGet.Invoke(mounts, args)!, Is.True);

            object mount = args[1]!;
            SftpClient client = (SftpClient)mount.GetType().GetProperty("Client")!.GetValue(mount)!;

            FieldInfo? eventField = typeof(BaseClient).GetField("HostKeyReceived", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(eventField, Is.Not.Null, "SSH.NET no longer has a HostKeyReceived backing field; update this test");
            return (Delegate?)eventField!.GetValue(client);
        }
    }
}
