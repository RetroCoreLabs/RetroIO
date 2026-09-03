using Xunit;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    public class X25PacketParsingTest
    {
        [Fact]
        public void TestX25PacketConstructor_CallAccepted_ShouldParseLCNAndPTI()
        {
            // Arrange - simulate Call Accepted packet for LCN=1, PTI=0x0F
            byte[] packetData = new byte[] { 0x00, 0x01, 0x0F, 0x00 }; // GFI=0, LCN=1, PTI=0x0F, facilities length=0
            
            // Act
            var packet = new X25Packet(packetData);
            
            // Assert
            Assert.Equal(1, packet.LCN);
            Assert.Equal(0x0F, packet.PTI);
            Assert.Equal(X25PacketTypeIdentifier.CallAccepted, packet.PacketTypeId);
        }
        
        [Fact]
        public void TestX25PacketBuilder_CallAccepted_ShouldCreateCorrectPacket()
        {
            // Arrange
            ushort lcn = 1;
            
            // Act
            var packetData = X25PacketBuilder.CreateCallAccepted(lcn);
            var packet = new X25Packet(packetData);
            
            // Assert
            Assert.Equal(1, packet.LCN);
            Assert.Equal(0x0F, packet.PTI);
            Assert.Equal(X25PacketTypeIdentifier.CallAccepted, packet.PacketTypeId);
        }
        
        [Fact]
        public void TestX25PacketBuilder_DataPacket_ShouldCreateCorrectPacket()
        {
            // Arrange
            ushort lcn = 1;
            byte[] userData = System.Text.Encoding.ASCII.GetBytes("test");
            
            // Act
            var packetData = X25PacketBuilder.CreateDataPacket(lcn, 0, 0, userData);
            var packet = new X25Packet(packetData);
            
            // Assert
            Assert.Equal(1, packet.LCN);
            Assert.Equal(0x00, (byte)(packet.PTI & 0x01)); // Data packet has PTI bit 0 = 0
        }
    }
}