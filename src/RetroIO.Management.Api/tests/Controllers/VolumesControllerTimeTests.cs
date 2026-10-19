//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// Tests the last modified time that VolumesController reports. The backend gives UTC, or nothing
/// when it does not know the time. The controller shows local time, and shows nothing for an unknown time.
/// </summary>
[TestFixture]
public class VolumesControllerTimeTests
{
    private static readonly DateTime WrittenUtc = new DateTime(2023, 8, 9, 10, 11, 12, DateTimeKind.Utc);

    private Mock<IFileSystemBackend> _backend = null!;
    private VolumesController _controller = null!;
    private string _volumesPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _backend = new Mock<IFileSystemBackend>();
        _backend.Setup(b => b.DirectoryExists(It.IsAny<string>()))
            .Returns<string>(p =>
            {
                // The first path the controller asks about is its own volumes folder.
                if (_volumesPath.Length == 0)
                    _volumesPath = p;
                return true;
            });
        _backend.Setup(b => b.GetFileName(It.IsAny<string>())).Returns<string>(Path.GetFileName);
        _backend.Setup(b => b.GetFullPath(It.IsAny<string>())).Returns<string>(p => p);
        _backend.Setup(b => b.FileExists(It.IsAny<string>())).Returns(true);
        _backend.Setup(b => b.IsDirectory(It.IsAny<string>())).Returns(false);

        _controller = new VolumesController(_backend.Object, new Mock<ILogger<VolumesController>>().Object);
    }

    private string VolumeFile()
    {
        // Ask once so the controller reveals its folder, then name a file inside it.
        _backend.Setup(b => b.EnumerateFileSystemEntries(It.IsAny<string>())).Returns(Array.Empty<string>());
        _controller.GetVolumes();
        Assert.That(_volumesPath, Is.Not.Empty);
        return Path.Combine(_volumesPath, "DISK.d64");
    }

    private void BackendKnowsFile(string path, long length, DateTime? lastWriteUtc)
    {
        _backend.Setup(b => b.GetFileInfo(path)).Returns(new BackendFileInfo(length, null, lastWriteUtc, null));
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> result)
    {
        OkObjectResult? ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        ApiResponse<T>? response = ok!.Value as ApiResponse<T>;
        Assert.That(response, Is.Not.Null);
        return response!.Data!;
    }

    /// <summary>
    /// The volume list shows the time of the backend converted from UTC to local time.
    /// </summary>
    [Test]
    public void GetVolumes_ShowsTheBackendTimeAsLocalTime()
    {
        string file = VolumeFile();
        BackendKnowsFile(file, 1024, WrittenUtc);
        _backend.Setup(b => b.EnumerateFileSystemEntries(It.IsAny<string>())).Returns(new[] { file });

        VolumeListResponse data = Data(_controller.GetVolumes());

        Assert.That(data.Volumes, Has.Count.EqualTo(1));
        Assert.That(data.Volumes[0].Size, Is.EqualTo(1024));
        Assert.That(data.Volumes[0].LastModified, Is.EqualTo(WrittenUtc.ToLocalTime()));
        Assert.That(data.Volumes[0].LastModified!.Value.Kind, Is.EqualTo(DateTimeKind.Local));
    }

    /// <summary>
    /// A file whose time the backend does not know shows no time in the volume list.
    /// </summary>
    [Test]
    public void GetVolumes_UnknownTime_ShowsNoTime()
    {
        string file = VolumeFile();
        BackendKnowsFile(file, 1024, null);
        _backend.Setup(b => b.EnumerateFileSystemEntries(It.IsAny<string>())).Returns(new[] { file });

        VolumeListResponse data = Data(_controller.GetVolumes());

        Assert.That(data.Volumes, Has.Count.EqualTo(1));
        Assert.That(data.Volumes[0].LastModified, Is.Null);
    }

    /// <summary>
    /// The folder listing shows the time of the backend converted from UTC to local time, and no time when unknown.
    /// </summary>
    [Test]
    public void BrowseVolume_ShowsLocalTimeOrNone()
    {
        string file = VolumeFile();
        string unknown = Path.Combine(_volumesPath, "OTHER.d64");
        BackendKnowsFile(file, 10, WrittenUtc);
        BackendKnowsFile(unknown, 20, null);
        _backend.Setup(b => b.EnumerateFileSystemEntries(_volumesPath)).Returns(new[] { file, unknown });

        VolumeContentsResponse data = Data(_controller.BrowseVolume(new VolumeContentsRequest { Path = _volumesPath }));

        Assert.That(data.Entries, Has.Count.EqualTo(2));
        Assert.That(data.Entries[0].LastModified, Is.EqualTo(WrittenUtc.ToLocalTime()));
        Assert.That(data.Entries[1].LastModified, Is.Null);
    }

    /// <summary>
    /// The volume info shows the time of the backend converted from UTC to local time, and no time when unknown.
    /// </summary>
    /// <param name="known">
    /// True when the backend knows the write time.
    /// </param>
    [TestCase(true)]
    [TestCase(false)]
    public void GetVolumeInfo_ShowsLocalTimeOrNone(bool known)
    {
        string file = VolumeFile();
        BackendKnowsFile(file, 2048, known ? WrittenUtc : null);

        VolumeDto data = Data(_controller.GetVolumeInfo(file));

        Assert.That(data.Size, Is.EqualTo(2048));
        if (known)
            Assert.That(data.LastModified, Is.EqualTo(WrittenUtc.ToLocalTime()));
        else
            Assert.That(data.LastModified, Is.Null);
    }
}
