using System;
using Xunit;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests to verify default runtime configuration matches ITU-T specifications
    /// </summary>
    public class RoutingConfigValidationTests
    {
        /// <summary>
        /// Test that default RuntimeConfig uses correct DTE mode configuration
        /// </summary>
        [Fact]
        public void DefaultRuntimeConfig_ShouldUseDteModeConfiguration()
        {
            // Arrange
            var config = new RuntimeConfig();
            
            // Assert DTE configuration
            Assert.False(config.Lapb.IsDCE);
            Assert.Equal(TimeSpan.Zero, config.Lapb.T3Timeout);
        }

        /// <summary>
        /// Test that default RuntimeConfig follows ITU-T timer specifications
        /// </summary>
        [Fact]
        public void DefaultRuntimeConfig_ShouldFollowItuTTimerSpecs()
        {
            // Arrange
            var config = new RuntimeConfig();
            
            // Assert ITU-T compliance
            Assert.Equal(TimeSpan.FromMilliseconds(3000), config.Lapb.T1Timeout);
            Assert.Equal(TimeSpan.FromMilliseconds(200), config.Lapb.T2Timeout);
            
            // T2 ≤ T1/2 constraint
            var maxT2 = config.Lapb.T1Timeout.TotalMilliseconds / 2;
            Assert.True(config.Lapb.T2Timeout.TotalMilliseconds <= maxT2, 
                $"T2 ({config.Lapb.T2Timeout.TotalMilliseconds}ms) must be ≤ T1/2 ({maxT2}ms)");
        }

        /// <summary>
        /// Test that default RuntimeConfig produces protocol-compliant behavior
        /// </summary>
        [Fact]
        public void DefaultRuntimeConfig_ShouldProduceCompliantBehavior()
        {
            // Arrange
            var config = new RuntimeConfig();
            
            // Assert no automatic supervision (DTE mode + T3 disabled)
            Assert.False(config.Lapb.IsDCE);
            Assert.Equal(TimeSpan.Zero, config.Lapb.T3Timeout);
            
            // Assert proper window size and retry count
            Assert.Equal(7, config.Lapb.WindowSize);
            Assert.Equal(5, config.Lapb.MaxRetries);
        }
    }
}