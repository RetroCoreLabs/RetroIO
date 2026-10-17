//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Sintran.Services;
using Xunit;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for ProtocolServiceRegistry functionality
    /// Tests service registration, discovery, and priority handling
    /// </summary>
    public class ProtocolServiceRegistryTests : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ProtocolServiceRegistry _registry;
        private readonly ProtocolRuntime _runtime;

        public ProtocolServiceRegistryTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<ProtocolServiceRegistry>();
            
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";
            _runtime = new ProtocolRuntime(loggerFactory.CreateLogger<ProtocolRuntime>(), config);
            
            _registry = new ProtocolServiceRegistry(_logger);
        }

        public void Dispose()
        {
            _runtime?.Dispose();
        }

        [Fact]
        public void RegisterService_ValidService_ShouldRegisterSuccessfully()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");

            // Act
            _registry.RegisterService(tadService);

            // Assert
            var registeredServices = _registry.GetRegisteredServices();
            Assert.Single(registeredServices);
            Assert.Equal("TAD", registeredServices[0].ServiceName);
        }

        [Fact]
        public void RegisterService_DuplicateServiceName_ShouldReplaceExisting()
        {
            // Arrange
            var tadProtocol1 = new LocalTadProtocol(_logger, "200");
            var tadService1 = new TadProtocolService(_logger, tadProtocol1, _runtime, "200");
            
            var tadProtocol2 = new LocalTadProtocol(_logger, "200");
            var tadService2 = new TadProtocolService(_logger, tadProtocol2, _runtime, "200");

            // Act
            _registry.RegisterService(tadService1);
            _registry.RegisterService(tadService2); // Should replace first service

            // Assert
            var registeredServices = _registry.GetRegisteredServices();
            Assert.Single(registeredServices);
            Assert.Same(tadService2, registeredServices[0]);
        }

        [Fact]
        public void RegisterService_MultipleServices_ShouldSortByPriority()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200"); // Priority 100
            
            var xmsgProtocol = new LocalXMSGProtocol(_logger, "200");
            var xmsgService = new XmsgProtocolService(_logger, xmsgProtocol, "200"); // Priority 200

            // Act
            _registry.RegisterService(tadService);   // Register lower priority first
            _registry.RegisterService(xmsgService);  // Register higher priority second

            // Assert
            var registeredServices = _registry.GetRegisteredServices();
            Assert.Equal(2, registeredServices.Length);
            
            // Should be sorted by priority (highest first)
            Assert.Equal("XMSG", registeredServices[0].ServiceName); // Priority 200
            Assert.Equal("TAD", registeredServices[1].ServiceName);   // Priority 100
        }

        [Fact]
        public void FindServiceForCall_ExistingAddress_ShouldReturnCorrectService()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            _registry.RegisterService(tadService);

            // Act
            var foundService = _registry.FindServiceForCall("200", Array.Empty<byte>());

            // Assert
            Assert.NotNull(foundService);
            Assert.Equal("TAD", foundService.ServiceName);
            Assert.Contains("200", foundService.LocalAddresses);
        }

        [Fact]
        public void FindServiceForCall_NonExistentAddress_ShouldReturnNull()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            _registry.RegisterService(tadService);

            // Act
            var foundService = _registry.FindServiceForCall("999", Array.Empty<byte>());

            // Assert
            Assert.Null(foundService);
        }

        [Fact]
        public void FindServiceForCall_WithCUD_ShouldRespectCUDHandling()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            _registry.RegisterService(tadService);

            var testCud = new byte[] { 0x01, 0x02, 0x03 };

            // Act
            var foundService = _registry.FindServiceForCall("200", testCud);

            // Assert - TAD service should handle any CUD
            Assert.NotNull(foundService);
            Assert.Equal("TAD", foundService.ServiceName);
        }

        [Fact]
        public void FindServiceForCall_MultipleServicesWithSameAddress_ShouldReturnHighestPriority()
        {
            // Arrange - Both services handle address "200"
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200"); // Priority 100
            
            var xmsgProtocol = new LocalXMSGProtocol(_logger, "200");
            var xmsgService = new XmsgProtocolService(_logger, xmsgProtocol, "200"); // Priority 200

            _registry.RegisterService(tadService);
            _registry.RegisterService(xmsgService);

            // Act - Use valid XMSG CUD pattern so both services can handle it
            var xmsgCud = new byte[] { 0x4C, 0x03, 0x00, 0x00, 0x10, 0x01 }; // XMSG L03 with port
            var foundService = _registry.FindServiceForCall("200", xmsgCud);

            // Assert - Should return XMSG (higher priority)
            Assert.NotNull(foundService);
            Assert.Equal("XMSG", foundService.ServiceName);
            Assert.True(foundService.Priority > tadService.Priority);
        }

        [Fact]
        public void UnregisterService_ExistingService_ShouldRemoveService()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            _registry.RegisterService(tadService);

            // Verify service is registered
            Assert.Single(_registry.GetRegisteredServices());

            // Act
            var removed = _registry.UnregisterService("TAD");

            // Assert
            Assert.True(removed);
            Assert.Empty(_registry.GetRegisteredServices());
        }

        [Fact]
        public void UnregisterService_NonExistentService_ShouldReturnFalse()
        {
            // Arrange - Empty registry

            // Act
            var removed = _registry.UnregisterService("NonExistent");

            // Assert
            Assert.False(removed);
        }

        [Fact]
        public void GetService_ExistingService_ShouldReturnService()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            _registry.RegisterService(tadService);

            // Act
            var foundService = _registry.GetService("TAD");

            // Assert
            Assert.NotNull(foundService);
            Assert.Same(tadService, foundService);
        }

        [Fact]
        public void GetService_NonExistentService_ShouldReturnNull()
        {
            // Act
            var foundService = _registry.GetService("NonExistent");

            // Assert
            Assert.Null(foundService);
        }

        [Fact]
        public void ClearAllServices_ShouldRemoveAllServices()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            
            var xmsgProtocol = new LocalXMSGProtocol(_logger, "200");
            var xmsgService = new XmsgProtocolService(_logger, xmsgProtocol, "200");

            _registry.RegisterService(tadService);
            _registry.RegisterService(xmsgService);

            // Verify services are registered
            Assert.Equal(2, _registry.GetRegisteredServices().Length);

            // Act
            _registry.ClearAllServices();

            // Assert
            Assert.Empty(_registry.GetRegisteredServices());
        }

        [Fact]
        public void GetStatistics_ShouldReturnCorrectStatistics()
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            
            var xmsgProtocol = new LocalXMSGProtocol(_logger, "200");
            var xmsgService = new XmsgProtocolService(_logger, xmsgProtocol, "200");

            _registry.RegisterService(tadService);
            _registry.RegisterService(xmsgService);

            // Act
            var stats = _registry.GetStatistics();

            // Assert
            Assert.Equal(2, stats.TotalServices);
            Assert.Equal(2, stats.ServiceNames.Length);
            Assert.Contains("TAD", stats.ServiceNames);
            Assert.Contains("XMSG", stats.ServiceNames);
            Assert.Equal(1, stats.TotalAddresses); // Both services handle "200"
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FindServiceForCall_InvalidAddress_ShouldReturnNull(string? calledAddress)
        {
            // Arrange
            var tadProtocol = new LocalTadProtocol(_logger, "200");
            var tadService = new TadProtocolService(_logger, tadProtocol, _runtime, "200");
            _registry.RegisterService(tadService);

            // Act
            var foundService = _registry.FindServiceForCall(calledAddress!, Array.Empty<byte>());

            // Assert
            Assert.Null(foundService);
        }

        [Fact]
        public void RegisterService_NullService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _registry.RegisterService(null!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void UnregisterService_InvalidServiceName_ShouldReturnFalse(string? serviceName)
        {
            // Act
            var removed = _registry.UnregisterService(serviceName!);

            // Assert
            Assert.False(removed);
        }
    }
}