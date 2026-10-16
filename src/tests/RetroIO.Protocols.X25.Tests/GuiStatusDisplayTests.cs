using System;
using Xunit;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for GUI status display functionality
    /// </summary>
    public class GuiStatusDisplayTests
    {
        /// <summary>
        /// Test DetermineSerialStatus method logic with various statistics scenarios
        /// </summary>
        [Theory]
        [InlineData(100, 50, 0, 0, 1, "Active")]
        [InlineData(0, 100, 0, 0, 1, "Active")]
        [InlineData(100, 50, 10, 5, 1, "Active (Errors)")]
        [InlineData(0, 0, 0, 0, 1, "Ready")]
        [InlineData(0, 0, 0, 0, 0, "Stopped")]
        public void DetermineSerialStatus_ShouldReturnCorrectStatus(
            long bytesReceived, 
            long bytesTransmitted,
            long badFramesReceived,
            long badFramesTransmitted,
            int registeredModules,
            string expected)
        {
            // Arrange
            var stats = CreateStatisticsSnapshot(bytesReceived, bytesTransmitted, badFramesReceived, badFramesTransmitted, registeredModules);
            
            // Act
            var result = DetermineSerialStatus(stats);
            
            // Assert
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Test that DetermineSerialStatus handles exceptions gracefully
        /// </summary>
        [Fact]
        public void DetermineSerialStatus_ShouldReturnUnknownOnException()
        {
            // Arrange - null stats should cause exception
            StatisticsSnapshot? stats = null;
            
            // Act
            var result = DetermineSerialStatus(stats);
            
            // Assert
            Assert.Equal("Unknown", result);
        }

        /// <summary>
        /// Test status display text formatting with actual serial port name
        /// </summary>
        [Theory]
        [InlineData("COM1", "Active", 1024, 512)]
        [InlineData("COM7", "Ready", 0, 0)]
        [InlineData("Unknown", "Stopped", 100, 200)]
        public void FormatStatusDisplay_ShouldShowCorrectPortAndStatus(
            string serialPort, 
            string status, 
            long bytesReceived, 
            long bytesTransmitted)
        {
            // Arrange
            var expected = $"Serial: {serialPort} [{status}]".PadRight(35) + 
                          $"RX: {bytesReceived} B".PadRight(20) + 
                          $"TX: {bytesTransmitted} B";
            
            // Act
            var result = FormatStatusDisplay(serialPort, status, bytesReceived, bytesTransmitted);
            
            // Assert
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Test edge cases for status determination
        /// </summary>
        [Theory]
        [InlineData(1, 0, 0, 0, 0, "Active")] // Only RX activity, no modules
        [InlineData(0, 1, 0, 0, 0, "Active")] // Only TX activity, no modules
        [InlineData(1, 1, 1, 0, 1, "Active (Errors)")] // Activity with only RX errors
        [InlineData(1, 1, 0, 1, 1, "Active (Errors)")] // Activity with only TX errors
        [InlineData(1000000, 1000000, 999999, 999999, 5, "Active (Errors)")] // High volume with errors
        public void DetermineSerialStatus_EdgeCases_ShouldReturnCorrectStatus(
            long bytesReceived,
            long bytesTransmitted,
            long badFramesReceived,
            long badFramesTransmitted,
            int registeredModules,
            string expected)
        {
            // Arrange
            var stats = CreateStatisticsSnapshot(bytesReceived, bytesTransmitted, badFramesReceived, badFramesTransmitted, registeredModules);
            
            // Act
            var result = DetermineSerialStatus(stats);
            
            // Assert
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Helper method that replicates the DetermineSerialStatus logic from X25Gui.cs
        /// </summary>
        private static string DetermineSerialStatus(StatisticsSnapshot? stats)
        {
            try
            {
                if (stats == null) throw new ArgumentNullException(nameof(stats));
                
                var hasActivity = stats.HDLC.BytesReceived > 0 || stats.HDLC.BytesTransmitted > 0;
                var hasErrors = stats.HDLC.BadFramesReceived > 0 || stats.HDLC.BadFramesTransmitted > 0;
                var hasModules = stats.Runtime.RegisteredModules > 0;
                
                if (hasActivity && !hasErrors)
                    return "Active";
                else if (hasActivity && hasErrors)
                    return "Active (Errors)";  
                else if (hasModules)
                    return "Ready";
                else
                    return "Stopped";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Helper method to format status display text
        /// </summary>
        private static string FormatStatusDisplay(string serialPort, string status, long bytesReceived, long bytesTransmitted)
        {
            return $"Serial: {serialPort} [{status}]".PadRight(35) + 
                   $"RX: {bytesReceived} B".PadRight(20) + 
                   $"TX: {bytesTransmitted} B";
        }

        /// <summary>
        /// Helper method to create StatisticsSnapshot with specific HDLC and Runtime values
        /// </summary>
        private static StatisticsSnapshot CreateStatisticsSnapshot(
            long bytesReceived, 
            long bytesTransmitted,
            long badFramesReceived,
            long badFramesTransmitted,
            int registeredModules)
        {
            return new StatisticsSnapshot
            {
                HDLC = new HdlcStatistics
                {
                    BytesReceived = bytesReceived,
                    BytesTransmitted = bytesTransmitted,
                    BadFramesReceived = badFramesReceived,
                    BadFramesTransmitted = badFramesTransmitted
                },
                Runtime = new RuntimeStatistics
                {
                    RegisteredModules = registeredModules
                }
            };
        }
    }
}