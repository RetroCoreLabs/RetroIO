//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests
{
    /// <summary>
    /// Regression tests for the FTPS certificate-validation fix: FtpVolumeProvider used to
    /// hardcode ValidateAnyCertificate = true unconditionally for every FTPS mount (a standing
    /// MITM exposure). Now defaults to real validation, with an explicit, per-mount, URI-visible
    /// opt-out (?allowInvalidCertificate=true) rather than a global "trust everything" setting.
    /// Mount() never actually connects to a server (AsyncFtpClient's constructor only configures
    /// the client), so these are safe to run without any real FTP server.
    /// </summary>
    [TestFixture]
    public class FtpVolumeProviderCertificateTests
    {
        [Test]
        public void Mount_Ftps_DefaultsToSecure_NoQueryFlag()
        {
            using var provider = new FtpVolumeProvider();
            var info = provider.Mount("ftps://user:pass@example.com:21/", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("FTPS"),
                "default FTPS mount must not report itself as unverified");
        }

        [Test]
        public void Mount_Ftps_AllowInvalidCertificateTrue_ReportsUnverified()
        {
            using var provider = new FtpVolumeProvider();
            var info = provider.Mount(
                "ftps://user:pass@example.com:21/?allowInvalidCertificate=true", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("FTPS (UNVERIFIED CERTIFICATE)"),
                "explicit opt-out must be visible in the returned MountInfo, not silent");
        }

        [Test]
        public void Mount_Ftps_AllowInvalidCertificateOne_AlsoOptsOut()
        {
            using var provider = new FtpVolumeProvider();
            var info = provider.Mount(
                "ftps://user:pass@example.com:21/?allowInvalidCertificate=1", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("FTPS (UNVERIFIED CERTIFICATE)"));
        }

        [Test]
        public void Mount_Ftps_AllowInvalidCertificateFalse_StaysSecure()
        {
            using var provider = new FtpVolumeProvider();
            var info = provider.Mount(
                "ftps://user:pass@example.com:21/?allowInvalidCertificate=false", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("FTPS"));
        }

        [Test]
        public void Mount_Ftps_UnrelatedQueryParam_StaysSecure()
        {
            using var provider = new FtpVolumeProvider();
            var info = provider.Mount(
                "ftps://user:pass@example.com:21/?somethingElse=true", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("FTPS"),
                "an unrelated query parameter must not accidentally opt out of certificate validation");
        }

        [Test]
        public void Mount_PlainFtp_NeverReportsCertificateState()
        {
            using var provider = new FtpVolumeProvider();
            var info = provider.Mount(
                "ftp://user:pass@example.com:21/?allowInvalidCertificate=true", "vol1", readOnly: true);

            Assert.That(info.FileSystemType, Is.EqualTo("FTP"),
                "plain (non-TLS) FTP has no certificate to validate, the flag should be a no-op");
        }
    }
}
