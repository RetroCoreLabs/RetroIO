//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Tests.VFS
{
    [TestFixture]
    public class VfsCacheConfigurationTests
    {
        [Test]
        public void Default_HasCorrectValues()
        {
            // Act
            var config = VfsCacheConfiguration.Default;

            // Assert - Dentry cache
            Assert.That(config.EnableDentryCache, Is.True);
            Assert.That(config.DentryCacheMaxEntries, Is.EqualTo(1000));
            Assert.That(config.DentryCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(60)));
            Assert.That(config.DentryCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));

            // Assert - Metadata cache
            Assert.That(config.EnableMetadataCache, Is.True);
            Assert.That(config.MetadataCacheMaxEntries, Is.EqualTo(2000));
            Assert.That(config.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(60)));
            Assert.That(config.MetadataCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void Disabled_HasCorrectValues()
        {
            // Act
            var config = VfsCacheConfiguration.Disabled;

            // Assert
            Assert.That(config.EnableDentryCache, Is.False);
            Assert.That(config.EnableMetadataCache, Is.False);
        }

        [Test]
        public void RemoteOptimized_HasCorrectValues()
        {
            // Act
            var config = VfsCacheConfiguration.RemoteOptimized;

            // Assert - Smaller cache sizes
            Assert.That(config.DentryCacheMaxEntries, Is.EqualTo(500));
            Assert.That(config.MetadataCacheMaxEntries, Is.EqualTo(1000));

            // Assert - Shorter TTL values
            Assert.That(config.DentryCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(config.DentryCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(config.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(config.MetadataCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(2)));
        }

        [Test]
        public void LocalOptimized_HasCorrectValues()
        {
            // Act
            var config = VfsCacheConfiguration.LocalOptimized;

            // Assert - Larger cache sizes
            Assert.That(config.DentryCacheMaxEntries, Is.EqualTo(2000));
            Assert.That(config.MetadataCacheMaxEntries, Is.EqualTo(5000));

            // Assert - Longer TTL values
            Assert.That(config.DentryCacheTtl, Is.EqualTo(TimeSpan.FromHours(2)));
            Assert.That(config.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromHours(2)));
        }

        [Test]
        public void Validate_ValidConfiguration_DoesNotThrow()
        {
            // Arrange
            var config = VfsCacheConfiguration.Default;

            // Act & Assert
            Assert.DoesNotThrow(() => config.Validate());
        }

        [Test]
        public void Validate_InvalidDentryCacheMaxEntries_ThrowsException()
        {
            // Arrange
            var config = new VfsCacheConfiguration { DentryCacheMaxEntries = 0 };

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());

            // Arrange negative
            config.DentryCacheMaxEntries = -1;

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }

        [Test]
        public void Validate_InvalidMetadataCacheMaxEntries_ThrowsException()
        {
            // Arrange
            var config = new VfsCacheConfiguration { MetadataCacheMaxEntries = 0 };

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());

            // Arrange negative
            config.MetadataCacheMaxEntries = -1;

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }

        [Test]
        public void Validate_InvalidDentryCacheTtl_ThrowsException()
        {
            // Arrange
            var config = new VfsCacheConfiguration { DentryCacheTtl = TimeSpan.Zero };

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());

            // Arrange negative
            config.DentryCacheTtl = TimeSpan.FromMinutes(-1);

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }

        [Test]
        public void Validate_InvalidDentryCacheRemoteTtl_ThrowsException()
        {
            // Arrange
            var config = new VfsCacheConfiguration { DentryCacheRemoteTtl = TimeSpan.Zero };

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());

            // Arrange negative
            config.DentryCacheRemoteTtl = TimeSpan.FromMinutes(-1);

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }

        [Test]
        public void Validate_InvalidMetadataCacheTtl_ThrowsException()
        {
            // Arrange
            var config = new VfsCacheConfiguration { MetadataCacheTtl = TimeSpan.Zero };

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());

            // Arrange negative
            config.MetadataCacheTtl = TimeSpan.FromMinutes(-1);

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }

        [Test]
        public void Validate_InvalidMetadataCacheRemoteTtl_ThrowsException()
        {
            // Arrange
            var config = new VfsCacheConfiguration { MetadataCacheRemoteTtl = TimeSpan.Zero };

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());

            // Arrange negative
            config.MetadataCacheRemoteTtl = TimeSpan.FromMinutes(-1);

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }

        [Test]
        public void Clone_CreatesDeepCopy()
        {
            // Arrange
            var original = new VfsCacheConfiguration
            {
                EnableDentryCache = false,
                DentryCacheMaxEntries = 500,
                DentryCacheTtl = TimeSpan.FromMinutes(30),
                DentryCacheRemoteTtl = TimeSpan.FromMinutes(3),
                EnableMetadataCache = false,
                MetadataCacheMaxEntries = 1000,
                MetadataCacheTtl = TimeSpan.FromMinutes(45),
                MetadataCacheRemoteTtl = TimeSpan.FromMinutes(4)
            };

            // Act
            var clone = original.Clone();

            // Assert - Values are copied
            Assert.That(clone.EnableDentryCache, Is.EqualTo(original.EnableDentryCache));
            Assert.That(clone.DentryCacheMaxEntries, Is.EqualTo(original.DentryCacheMaxEntries));
            Assert.That(clone.DentryCacheTtl, Is.EqualTo(original.DentryCacheTtl));
            Assert.That(clone.DentryCacheRemoteTtl, Is.EqualTo(original.DentryCacheRemoteTtl));
            Assert.That(clone.EnableMetadataCache, Is.EqualTo(original.EnableMetadataCache));
            Assert.That(clone.MetadataCacheMaxEntries, Is.EqualTo(original.MetadataCacheMaxEntries));
            Assert.That(clone.MetadataCacheTtl, Is.EqualTo(original.MetadataCacheTtl));
            Assert.That(clone.MetadataCacheRemoteTtl, Is.EqualTo(original.MetadataCacheRemoteTtl));

            // Assert - Modifying clone doesn't affect original
            clone.DentryCacheMaxEntries = 999;
            Assert.That(original.DentryCacheMaxEntries, Is.EqualTo(500));
        }

        [Test]
        public void PropertySetters_WorkCorrectly()
        {
            // Arrange
            var config = new VfsCacheConfiguration();

            // Act - Set all properties
            config.EnableDentryCache = false;
            config.DentryCacheMaxEntries = 123;
            config.DentryCacheTtl = TimeSpan.FromMinutes(15);
            config.DentryCacheRemoteTtl = TimeSpan.FromMinutes(2);
            config.EnableMetadataCache = false;
            config.MetadataCacheMaxEntries = 456;
            config.MetadataCacheTtl = TimeSpan.FromMinutes(20);
            config.MetadataCacheRemoteTtl = TimeSpan.FromMinutes(3);

            // Assert
            Assert.That(config.EnableDentryCache, Is.False);
            Assert.That(config.DentryCacheMaxEntries, Is.EqualTo(123));
            Assert.That(config.DentryCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(config.DentryCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(config.EnableMetadataCache, Is.False);
            Assert.That(config.MetadataCacheMaxEntries, Is.EqualTo(456));
            Assert.That(config.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(config.MetadataCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(3)));
        }

        [Test]
        public void Constructor_InitializesWithDefaultValues()
        {
            // Act
            var config = new VfsCacheConfiguration();

            // Assert - Should match Default static property
            Assert.That(config.EnableDentryCache, Is.True);
            Assert.That(config.DentryCacheMaxEntries, Is.EqualTo(1000));
            Assert.That(config.DentryCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(60)));
            Assert.That(config.DentryCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(config.EnableMetadataCache, Is.True);
            Assert.That(config.MetadataCacheMaxEntries, Is.EqualTo(2000));
            Assert.That(config.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(60)));
            Assert.That(config.MetadataCacheRemoteTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void StaticPresets_AreIndependent()
        {
            // Act - Get multiple instances
            var default1 = VfsCacheConfiguration.Default;
            var default2 = VfsCacheConfiguration.Default;

            // Modify one
            default1.DentryCacheMaxEntries = 9999;

            // Assert - They are different instances
            Assert.That(default2.DentryCacheMaxEntries, Is.EqualTo(1000));
        }
    }
}
