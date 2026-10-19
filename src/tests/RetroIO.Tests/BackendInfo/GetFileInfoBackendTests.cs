//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Azure;

namespace RetroIO.Tests.BackendInfo;

/// <summary>
/// Tests <c>GetFileInfo</c> of the local disk backend and of the Azure blob backend. Both return a
/// <see cref="BackendFileInfo"/>. The blob backend is run against a small web server in this process
/// that answers the blob properties request, so no cloud account or emulator is needed.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class GetFileInfoBackendTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "retroio-getfileinfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // Leaving a temp folder behind is harmless.
        }
    }

    /// <summary>
    /// A file on disk gives its real length and its real times in UTC.
    /// </summary>
    [Test]
    public void Local_ExistingFile_GivesLengthAndTimes()
    {
        string path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, new byte[37]);
        DateTime written = new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        LocalFileSystemBackend backend = new LocalFileSystemBackend();

        BackendFileInfo? info = backend.GetFileInfo(path);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Length, Is.EqualTo(37));
        Assert.That(info.LastWriteTimeUtc, Is.EqualTo(written));
        Assert.That(info.CreationTimeUtc, Is.Not.Null);
        Assert.That(info.LastAccessTimeUtc, Is.Not.Null);
    }

    /// <summary>
    /// A path with no file gives null, not an object that says the file is empty.
    /// </summary>
    [Test]
    public void Local_MissingFile_GivesNull()
    {
        LocalFileSystemBackend backend = new LocalFileSystemBackend();

        Assert.That(backend.GetFileInfo(Path.Combine(_dir, "nope.bin")), Is.Null);
    }

    /// <summary>
    /// A directory is not a file, so it gives null.
    /// </summary>
    [Test]
    public void Local_Directory_GivesNull()
    {
        LocalFileSystemBackend backend = new LocalFileSystemBackend();

        Assert.That(backend.GetFileInfo(_dir), Is.Null);
    }

    /// <summary>
    /// A path that is not a valid path gives null instead of an exception.
    /// </summary>
    /// <param name="path">
    /// The bad path.
    /// </param>
    [TestCase("")]
    [TestCase(null)]
    [TestCase("bad\0name")]
    public void Local_PathThatIsNotValid_GivesNull(string? path)
    {
        LocalFileSystemBackend backend = new LocalFileSystemBackend();

        Assert.That(backend.GetFileInfo(path!), Is.Null);
    }

    /// <summary>
    /// The blob backend takes length, creation time and last write time from the blob properties, and has no access time.
    /// </summary>
    [Test]
    public async Task Azure_ExistingBlob_GivesLengthAndTimesFromTheProperties()
    {
        DateTime created = new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime modified = new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        using FakeBlobServer server = new FakeBlobServer("PROG", 4321, created, modified);
        AzureBlobFileSystemBackend backend = server.CreateBackend(_dir);

        BackendFileInfo? info = await Task.Run(() => backend.GetFileInfo("/PROG"));

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Length, Is.EqualTo(4321));
        Assert.That(info.CreationTimeUtc, Is.EqualTo(created));
        Assert.That(info.LastWriteTimeUtc, Is.EqualTo(modified));
        Assert.That(info.LastAccessTimeUtc, Is.Null, "a blob has no access time");
    }

    /// <summary>
    /// A blob that does not exist gives null.
    /// </summary>
    [Test]
    public async Task Azure_MissingBlob_GivesNull()
    {
        using FakeBlobServer server = new FakeBlobServer("PROG", 1, DateTime.UtcNow, DateTime.UtcNow);
        AzureBlobFileSystemBackend backend = server.CreateBackend(_dir);

        BackendFileInfo? info = await Task.Run(() => backend.GetFileInfo("/OTHER"));

        Assert.That(info, Is.Null);
    }

    /// <summary>
    /// The root path names no blob, so it gives null without asking the server.
    /// </summary>
    [Test]
    public async Task Azure_RootPath_GivesNullWithoutAskingTheServer()
    {
        using FakeBlobServer server = new FakeBlobServer("PROG", 1, DateTime.UtcNow, DateTime.UtcNow);
        AzureBlobFileSystemBackend backend = server.CreateBackend(_dir);

        BackendFileInfo? info = await Task.Run(() => backend.GetFileInfo("/"));

        Assert.That(info, Is.Null);
        Assert.That(server.RequestCount, Is.EqualTo(0));
    }

    /// <summary>
    /// A web server on the loopback address that knows one blob and answers properties requests for it.
    /// </summary>
    private sealed class FakeBlobServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly int _port;
        private readonly string _blobName;
        private readonly long _length;
        private readonly DateTime _created;
        private readonly DateTime _modified;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _loop;
        private int _requests;

        /// <summary>
        /// Starts the server.
        /// </summary>
        /// <param name="blobName">
        /// The name of the one blob that exists.
        /// </param>
        /// <param name="length">
        /// Its length.
        /// </param>
        /// <param name="created">
        /// Its creation time in UTC.
        /// </param>
        /// <param name="modified">
        /// Its last write time in UTC.
        /// </param>
        public FakeBlobServer(string blobName, long length, DateTime created, DateTime modified)
        {
            _blobName = blobName;
            _length = length;
            _created = created;
            _modified = modified;

            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add("http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/");
            _listener.Start();
            _loop = Task.Run(Serve);
        }

        /// <summary>
        /// Gets the number of requests the server has answered.
        /// </summary>
        public int RequestCount => Volatile.Read(ref _requests);

        /// <summary>
        /// Builds a blob backend that talks to this server.
        /// </summary>
        /// <param name="cacheDirectory">
        /// The folder the backend may use for its temp files.
        /// </param>
        /// <returns>
        /// The backend.
        /// </returns>
        public AzureBlobFileSystemBackend CreateBackend(string cacheDirectory)
        {
            string key = Convert.ToBase64String(new byte[32]);
            AzureBlobOptions options = new AzureBlobOptions
            {
                ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" + key
                    + ";BlobEndpoint=http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/devstoreaccount1;",
                ContainerName = "retrofs",
                CreateContainerIfNotExists = false,
                TempCacheDirectory = cacheDirectory,
            };
            return new AzureBlobFileSystemBackend(options, NullLogger<AzureBlobFileSystemBackend>.Instance);
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                string expected = "/devstoreaccount1/retrofs/" + _blobName;
                HttpListenerResponse response = context.Response;
                if (context.Request.HttpMethod == "HEAD" && context.Request.Url!.AbsolutePath == expected)
                {
                    response.StatusCode = 200;
                    response.Headers["Last-Modified"] = _modified.ToString("R", CultureInfo.InvariantCulture);
                    response.Headers["x-ms-creation-time"] = _created.ToString("R", CultureInfo.InvariantCulture);
                    response.Headers["ETag"] = "\"0x8DA1\"";
                    response.Headers["x-ms-blob-type"] = "BlockBlob";
                    response.ContentLength64 = _length;
                }
                else
                {
                    response.StatusCode = 404;
                    response.Headers["x-ms-error-code"] = "BlobNotFound";
                }

                response.Close();
            }
        }

        /// <summary>
        /// Stops the server.
        /// </summary>
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The loop ends by exception when the listener closes.
            }

            _stop.Dispose();
        }
    }
}
