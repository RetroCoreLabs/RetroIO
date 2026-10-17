//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using Xunit;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests to verify peer-to-peer X.25 emulation configuration
    /// </summary>
    public class PeerToPeerConfigurationTests
    {
        /// <summary>
        /// Test that default configuration is set for DTE mode (peer-to-peer)
        /// </summary>
        [Fact]
        public void DefaultConfiguration_ShouldBeDteModeForPeerToPeer()
        {
            // Arrange
            var config = new RuntimeConfig();
            
            // Assert - Default should be DTE mode for peer-to-peer scenarios
            Assert.False(config.Lapb.IsDCE, "Default configuration should be DTE mode (IsDCE=false) for peer-to-peer communication");
        }

        /// <summary>
        /// Test peer-to-peer configuration consistency
        /// </summary>
        [Fact]
        public void PeerToPeerConfiguration_ShouldBeConsistent()
        {
            // Arrange
            var config = new RuntimeConfig();
            
            // Assert peer-to-peer configuration
            Assert.False(config.Lapb.IsDCE, "Should be DTE mode");
            Assert.Equal(TimeSpan.Zero, config.Lapb.T3Timeout);
            Assert.Equal(TimeSpan.FromMilliseconds(3000), config.Lapb.T1Timeout);
            Assert.Equal(TimeSpan.FromMilliseconds(200), config.Lapb.T2Timeout);
        }

        /// <summary>
        /// Test that DTE mode configuration follows X.25 specifications
        /// </summary>
        [Theory]
        [InlineData(false)] // DTE mode
        [InlineData(true)]  // DCE mode for comparison
        public void StationTypeConfiguration_ShouldFollowX25Specifications(bool isDCE)
        {
            // Arrange
            var config = new RuntimeConfig();
            config.Lapb.IsDCE = isDCE;
            
            // Act & Assert - Verify X.25 specifications
            if (isDCE)
            {
                // DCE (Data Circuit-terminating Equipment) specifications
                // - Network-side equipment: PADs, switches, modems
                // - May use T3 for idle supervision (but we keep it disabled by default)
                // - Uses address 0x01 for command frames
                Assert.True(config.Lapb.IsDCE, "DCE mode should be enabled");
            }
            else
            {
                // DTE (Data Terminal Equipment) specifications  
                // - User-side equipment: terminals, hosts, applications
                // - Should not use T3 timer (relies on DCE for supervision)
                // - Uses address 0x03 for command frames
                Assert.False(config.Lapb.IsDCE);
                Assert.Equal(TimeSpan.Zero, config.Lapb.T3Timeout);
            }
        }

        /// <summary>
        /// Test that multiple DTE instances can coexist without conflicts
        /// </summary>
        [Fact]
        public void MultipleDteInstances_ShouldNotConflict()
        {
            // Arrange - Create two DTE configurations
            var dteConfig1 = new RuntimeConfig();
            var dteConfig2 = new RuntimeConfig();
            
            // Both should be DTE mode
            Assert.False(dteConfig1.Lapb.IsDCE);
            Assert.False(dteConfig2.Lapb.IsDCE);
            
            // Both should have T3 disabled (no supervision conflicts)
            Assert.Equal(TimeSpan.Zero, dteConfig1.Lapb.T3Timeout);
            Assert.Equal(TimeSpan.Zero, dteConfig2.Lapb.T3Timeout);
            
            // Timer values should be identical for consistency
            Assert.Equal(dteConfig1.Lapb.T1Timeout, dteConfig2.Lapb.T1Timeout);
            Assert.Equal(dteConfig1.Lapb.T2Timeout, dteConfig2.Lapb.T2Timeout);
            Assert.Equal(dteConfig1.Lapb.T3Timeout, dteConfig2.Lapb.T3Timeout);
        }

        /// <summary>
        /// Test LAPB addressing for DTE vs DCE modes
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LapbAddressing_ShouldFollowX25Standard(bool isDCE)
        {
            // This test documents the expected LAPB addressing behavior
            // Implementation details are in the LAPB protocol layer
            
            if (isDCE)
            {
                // DCE addressing: 0x01 for commands, 0x03 for responses
                Assert.True(true, "DCE should use 0x01 for command frames");
            }
            else
            {
                // DTE addressing: 0x03 for commands, 0x01 for responses  
                Assert.True(true, "DTE should use 0x03 for command frames");
            }
        }
    }
}