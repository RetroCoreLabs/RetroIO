//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Pins the exception a write method throws when the driver or file system cannot write.
    /// </summary>
    /// <remarks>
    /// Contract read in the interface comments:
    /// <c>IFileSystemDriver.WriteFile</c> and <c>DeleteFile</c> throw <c>UnauthorizedAccessException</c>
    /// "when filesystem is read-only"; <c>WriteBlock</c> throws it too (and <c>NotSupportedException</c>
    /// only when <c>HasBlockAccess</c> is false); <c>RenameFile</c> and <c>CreateDirectory</c> default to
    /// <c>NotSupportedException</c>. <c>IBlockFileSystem.WriteFile</c> and <c>DeleteFile</c> throw
    /// <c>IOException</c> (write fails, read-only), <c>CreateDirectory</c> throws <c>IOException</c> or
    /// <c>NotSupportedException</c> "if filesystem doesn't support directories", <c>DeleteDirectory</c>
    /// throws <c>DirectoryNotFoundException</c> or <c>IOException</c>, and <c>Rename</c> throws
    /// <c>FileNotFoundException</c> or <c>IOException</c>. None of them lists <c>NotImplementedException</c>.
    /// </remarks>
    [TestFixture]
    public class WriteMethodExceptionContractTests
    {
        private static Exception? Catch(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static Exception? CatchWriteBlock(IFileSystemDriver driver)
        {
            try
            {
                driver.WriteBlock(BlockAddress.FromLBA(0), new byte[] { 1 });
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static List<Type> ConcreteTypes(Type contract)
        {
            List<Type> found = new List<Type>();
            Type[] all = typeof(V7Driver).Assembly.GetTypes();
            for (int i = 0; i < all.Length; i++)
            {
                Type t = all[i];
                if (t.IsClass && !t.IsAbstract && contract.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                {
                    found.Add(t);
                }
            }
            return found;
        }

        // ------------------------------------------------------------------ every driver, found by reflection

        [Test]
        public void NoDriverWriteMethodThrowsNotImplemented()
        {
            List<Type> drivers = ConcreteTypes(typeof(IFileSystemDriver));
            Assert.That(drivers.Count, Is.GreaterThan(5), "the scan must find the drivers");

            for (int i = 0; i < drivers.Count; i++)
            {
                IFileSystemDriver driver = (IFileSystemDriver)Activator.CreateInstance(drivers[i])!;
                string name = drivers[i].Name;

                Assert.That(Catch(() => driver.WriteFile("/A", new byte[] { 1 })), Is.Not.InstanceOf<NotImplementedException>(), name + ".WriteFile");
                Assert.That(Catch(() => driver.DeleteFile("/A")), Is.Not.InstanceOf<NotImplementedException>(), name + ".DeleteFile");
                Assert.That(Catch(() => driver.RenameFile("/A", "/B")), Is.Not.InstanceOf<NotImplementedException>(), name + ".RenameFile");
                Assert.That(Catch(() => driver.CreateDirectory("/D")), Is.Not.InstanceOf<NotImplementedException>(), name + ".CreateDirectory");
                Assert.That(CatchWriteBlock(driver), Is.Not.InstanceOf<NotImplementedException>(), name + ".WriteBlock");
                driver.Dispose();
            }
        }

        // ------------------------------------------------------------------ every file system, found by reflection

        [Test]
        public void NoFileSystemWriteMethodThrowsNotImplemented()
        {
            List<Type> systems = ConcreteTypes(typeof(IBlockFileSystem));
            Assert.That(systems.Count, Is.GreaterThan(3), "the scan must find the file systems");

            for (int i = 0; i < systems.Count; i++)
            {
                IBlockFileSystem fs = (IBlockFileSystem)Activator.CreateInstance(systems[i])!;
                string name = systems[i].Name;

                Assert.That(Catch(() => fs.WriteFile("/A", new byte[] { 1 })), Is.Not.InstanceOf<NotImplementedException>(), name + ".WriteFile");
                Assert.That(Catch(() => fs.DeleteFile("/A")), Is.Not.InstanceOf<NotImplementedException>(), name + ".DeleteFile");
                Assert.That(Catch(() => fs.CreateDirectory("/D")), Is.Not.InstanceOf<NotImplementedException>(), name + ".CreateDirectory");
                Assert.That(Catch(() => fs.DeleteDirectory("/D")), Is.Not.InstanceOf<NotImplementedException>(), name + ".DeleteDirectory");
                Assert.That(Catch(() => fs.Rename("/A", "/B")), Is.Not.InstanceOf<NotImplementedException>(), name + ".Rename");
            }
        }

        // ------------------------------------------------------------------ the exact exception, per driver

        [TestCase(typeof(V7Driver))]
        [TestCase(typeof(DragonDskDriver))]
        [TestCase(typeof(DragonVdkDriver))]
        [TestCase(typeof(FfsDriver))]
        [TestCase(typeof(T64Driver))]
        public void DriverWithoutWriteSupport_WriteFileAndDeleteFile_ThrowUnauthorizedAccess(Type driverType)
        {
            IFileSystemDriver driver = (IFileSystemDriver)Activator.CreateInstance(driverType)!;

            Assert.That(Catch(() => driver.WriteFile("/A", new byte[] { 1 })), Is.InstanceOf<UnauthorizedAccessException>());
            Assert.That(Catch(() => driver.DeleteFile("/A")), Is.InstanceOf<UnauthorizedAccessException>());
            driver.Dispose();
        }

        [TestCase(typeof(V7Driver))]
        [TestCase(typeof(DragonDskDriver))]
        [TestCase(typeof(DragonVdkDriver))]
        [TestCase(typeof(FfsDriver))]
        public void DriverWithBlockAccessButNoWrite_WriteBlock_ThrowsUnauthorizedAccess(Type driverType)
        {
            IFileSystemDriver driver = (IFileSystemDriver)Activator.CreateInstance(driverType)!;

            Assert.That(driver.HasBlockAccess, Is.True);
            Assert.That(CatchWriteBlock(driver), Is.InstanceOf<UnauthorizedAccessException>());
            driver.Dispose();
        }

        // ------------------------------------------------------------------ the exact exception, per file system

        [TestCase(typeof(DecbFileSystem))]
        public void FileSystemWithoutWriteSupport_WriteFileAndDeleteFile_ThrowIOException(Type fsType)
        {
            IBlockFileSystem fs = (IBlockFileSystem)Activator.CreateInstance(fsType)!;

            Assert.That(Catch(() => fs.WriteFile("/A", new byte[] { 1 })), Is.InstanceOf<IOException>());
            Assert.That(Catch(() => fs.DeleteFile("/A")), Is.InstanceOf<IOException>());
            Assert.That(Catch(() => fs.Rename("/A", "/B")), Is.InstanceOf<IOException>());
        }

        [Test]
        public void DragonDos_CreateDirectory_ThrowsNotSupported_BecauseTheDiskHasOnlyARoot()
        {
            IBlockFileSystem fs = new DragonDosFileSystem();

            Assert.That(Catch(() => fs.CreateDirectory("/D")), Is.InstanceOf<NotSupportedException>());
        }

        [Test]
        public void DragonDos_DeleteDirectory_ThrowsDirectoryNotFound_ForAnyDirectoryButTheRoot()
        {
            IBlockFileSystem fs = new DragonDosFileSystem();

            Assert.That(Catch(() => fs.DeleteDirectory("/D")), Is.InstanceOf<DirectoryNotFoundException>());
            Assert.That(Catch(() => fs.DeleteDirectory("/")), Is.InstanceOf<IOException>());
        }
    }
}
