//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using System;
using System.Text;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class SintranTadMessageTests
    {
        [Test]
        public void ParseDummyMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7DUMM message (type 0x00, count 0x00)
            byte[] data = new byte[] { 0x00, 0x00 };

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.DUMM));
            Assert.That(message.Data.Length, Is.EqualTo(0));
            Assert.That(message.HasPadding, Is.False);
            // DUMM is not a control, data, or config message - it's just a dummy/padding message
            Assert.That(message.IsControlMessage, Is.False);
            Assert.That(message.IsDataMessage, Is.False);
            Assert.That(message.IsConfigurationMessage, Is.False);
        }

        [Test]
        public void ParseBinaryDataMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7BDAT message with "Hello" payload
            string testData = "Hello";
            byte[] payload = Encoding.ASCII.GetBytes(testData);
            byte[] data = new byte[2 + payload.Length];
            data[0] = SintranTadMessageTypes.BDAT;
            data[1] = (byte)payload.Length;
            Array.Copy(payload, 0, data, 2, payload.Length);

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.BDAT));
            Assert.That(message.Data.Length, Is.EqualTo(payload.Length));
            Assert.That(message.GetDataAsString(), Is.EqualTo(testData));
            Assert.That(message.IsDataMessage, Is.True);
        }

        [Test]
        public void ParseReadyForInputMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7RFI message with byte count 256 (0x01 0x00)
            byte[] data = new byte[] { SintranTadMessageTypes.RFI, 0x02, 0x01, 0x00 };

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.RFI));
            Assert.That(message.Data.Length, Is.EqualTo(2));
            Assert.That(message.Data[0], Is.EqualTo(0x01));
            Assert.That(message.Data[1], Is.EqualTo(0x00));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void ParseTerminalModeMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7TMOD message with mode flags
            byte[] data = new byte[] { SintranTadMessageTypes.TMOD, 0x01, 0x0F };

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.TMOD));
            Assert.That(message.Data.Length, Is.EqualTo(1));
            Assert.That(message.Data[0], Is.EqualTo(0x0F));
            Assert.That(message.IsConfigurationMessage, Is.True);
        }

        [Test]
        public void ParseTerminalTypeMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7TTYP message with terminal type
            byte[] data = new byte[] { SintranTadMessageTypes.TTYP, 0x02, 0x00, 0x01 };

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.TTYP));
            Assert.That(message.Data.Length, Is.EqualTo(2));
            Assert.That(message.IsConfigurationMessage, Is.True);
        }

        [Test]
        public void ParseMessageWithPadding_ValidData_ParsesCorrectly()
        {
            // Arrange: Message with padding byte (0x00) followed by 7BDAT
            byte[] data = new byte[] { 0x00, SintranTadMessageTypes.BDAT, 0x03, 0x41, 0x42, 0x43 };

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.BDAT));
            Assert.That(message.HasPadding, Is.True);
            Assert.That(message.Data.Length, Is.EqualTo(3));
            Assert.That(message.GetDataAsString(), Is.EqualTo("ABC"));
        }

        [Test]
        public void ParseBreakMatrixMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7BMMX message with 20-byte matrix
            byte[] data = new byte[22]; // Type + Count + 20 bytes
            data[0] = SintranTadMessageTypes.BMMX;
            data[1] = 20;
            for (int i = 0; i < 20; i++)
            {
                data[2 + i] = (byte)i;
            }

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.BMMX));
            Assert.That(message.Data.Length, Is.EqualTo(20));
            // BMMX is not categorized as a standard configuration message in the type system
            // It's a special strategy matrix message
        }

        [Test]
        public void ParseEchoMatrixMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7ECKM message with 20-byte matrix
            byte[] data = new byte[22]; // Type + Count + 20 bytes
            data[0] = SintranTadMessageTypes.ECKM;
            data[1] = 20;
            for (int i = 0; i < 20; i++)
            {
                data[2 + i] = (byte)(20 - i);
            }

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.ECKM));
            Assert.That(message.Data.Length, Is.EqualTo(20));
            // ECKM is not categorized as a standard configuration message in the type system
            // It's a special strategy matrix message
        }

        [Test]
        public void ParseDisconnectMessage_ValidData_ParsesCorrectly()
        {
            // Arrange: 7DCON message
            byte[] data = new byte[] { SintranTadMessageTypes.DCON, 0x00 };

            // Act
            var message = SintranTadMessage.Parse(data);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.DCON));
            Assert.That(message.Data.Length, Is.EqualTo(0));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void ParseMessage_TooShort_ThrowsException()
        {
            // Arrange: Data too short (only 1 byte)
            byte[] data = new byte[] { 0x00 };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => SintranTadMessage.Parse(data));
        }

        [Test]
        public void ParseMessage_InvalidByteCount_ThrowsException()
        {
            // Arrange: Byte count exceeds available data
            byte[] data = new byte[] { SintranTadMessageTypes.BDAT, 0xFF, 0x41, 0x42 };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => SintranTadMessage.Parse(data));
        }

        [Test]
        public void BuildDummyMessage_CreatesCorrectBytes()
        {
            // Arrange
            var message = SintranTadMessage.CreateDummy();

            // Act
            byte[] data = message.Build();

            // Assert
            Assert.That(data.Length, Is.EqualTo(2));
            Assert.That(data[0], Is.EqualTo(SintranTadMessageTypes.DUMM));
            Assert.That(data[1], Is.EqualTo(0x00));
        }

        [Test]
        public void BuildBinaryDataMessage_CreatesCorrectBytes()
        {
            // Arrange
            string testData = "Test123";
            var message = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes(testData));

            // Act
            byte[] data = message.Build();

            // Assert
            Assert.That(data.Length, Is.EqualTo(2 + testData.Length));
            Assert.That(data[0], Is.EqualTo(SintranTadMessageTypes.BDAT));
            Assert.That(data[1], Is.EqualTo((byte)testData.Length));

            string decoded = Encoding.ASCII.GetString(data, 2, testData.Length);
            Assert.That(decoded, Is.EqualTo(testData));
        }

        [Test]
        public void BuildMessageWithPadding_CreatesCorrectBytes()
        {
            // Arrange
            var message = SintranTadMessage.CreateDummy();

            // Act
            byte[] data = message.Build(includePadding: true);

            // Assert
            Assert.That(data.Length, Is.EqualTo(3));
            Assert.That(data[0], Is.EqualTo(0x00)); // Padding byte
            Assert.That(data[1], Is.EqualTo(SintranTadMessageTypes.DUMM));
            Assert.That(data[2], Is.EqualTo(0x00));
        }

        [Test]
        public void CreateReadyForInputWithCount_CreatesCorrectMessage()
        {
            // Arrange
            int byteCount = 1024;

            // Act
            var message = SintranTadMessage.CreateReadyForInput(byteCount);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.RFI));
            Assert.That(message.Data.Length, Is.EqualTo(2));
            int decodedCount = (message.Data[0] << 8) | message.Data[1];
            Assert.That(decodedCount, Is.EqualTo(byteCount));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void CreateTerminalMode_WithFlags_CreatesCorrectMessage()
        {
            // Arrange & Act
            var message = SintranTadMessage.CreateTerminalMode(
                echoPlex: true,
                localEdit: true,
                pageMode: false,
                formFeed: true);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.TMOD));
            Assert.That(message.Data.Length, Is.EqualTo(1));
            Assert.That(message.Data[0], Is.EqualTo(0x0B)); // 0x01 | 0x02 | 0x08
        }

        [Test]
        public void CreateTerminalType_CreatesCorrectMessage()
        {
            // Arrange
            byte terminalType = 0x01;

            // Act
            var message = SintranTadMessage.CreateTerminalType(terminalType);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.TTYP));
            Assert.That(message.Data.Length, Is.EqualTo(2));
        }

        [Test]
        public void CreateDescriptor_CreatesCorrectMessage()
        {
            // Arrange
            int lineWidth = 80;
            int pageLength = 24;
            int tabWidth = 8;

            // Act
            var message = SintranTadMessage.CreateDescriptor(lineWidth, pageLength, tabWidth);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.DESC));
            Assert.That(message.Data.Length, Is.EqualTo(6));

            int decodedWidth = (message.Data[0] << 8) | message.Data[1];
            int decodedHeight = (message.Data[2] << 8) | message.Data[3];
            int decodedTab = (message.Data[4] << 8) | message.Data[5];

            Assert.That(decodedWidth, Is.EqualTo(lineWidth));
            Assert.That(decodedHeight, Is.EqualTo(pageLength));
            Assert.That(decodedTab, Is.EqualTo(tabWidth));
        }

        [Test]
        public void CreateOptionSave_CreatesCorrectMessage()
        {
            // Arrange & Act
            var messageTrue = SintranTadMessage.CreateOptionSave(true);
            var messageFalse = SintranTadMessage.CreateOptionSave(false);

            // Assert
            Assert.That(messageTrue.MessageType, Is.EqualTo(SintranTadMessageTypes.OPSV));
            Assert.That(messageTrue.Data[0], Is.EqualTo(0x01));

            Assert.That(messageFalse.MessageType, Is.EqualTo(SintranTadMessageTypes.OPSV));
            Assert.That(messageFalse.Data[0], Is.EqualTo(0x00));
        }

        [Test]
        public void CreateBreakMatrix_ValidMatrix_CreatesCorrectMessage()
        {
            // Arrange
            byte[] matrix = new byte[20];
            for (int i = 0; i < 20; i++)
            {
                matrix[i] = (byte)i;
            }

            // Act
            var message = SintranTadMessage.CreateBreakMatrix(matrix);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.BMMX));
            Assert.That(message.Data.Length, Is.EqualTo(20));
            for (int i = 0; i < 20; i++)
            {
                Assert.That(message.Data[i], Is.EqualTo((byte)i));
            }
        }

        [Test]
        public void CreateBreakMatrix_InvalidSize_ThrowsException()
        {
            // Arrange
            byte[] matrix = new byte[10]; // Wrong size

            // Act & Assert
            Assert.Throws<ArgumentException>(() => SintranTadMessage.CreateBreakMatrix(matrix));
        }

        [Test]
        public void CreateEchoMatrix_ValidMatrix_CreatesCorrectMessage()
        {
            // Arrange
            byte[] matrix = new byte[20];
            for (int i = 0; i < 20; i++)
            {
                matrix[i] = 0xFF;
            }

            // Act
            var message = SintranTadMessage.CreateEchoMatrix(matrix);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.ECKM));
            Assert.That(message.Data.Length, Is.EqualTo(20));
            for (int i = 0; i < 20; i++)
            {
                Assert.That(message.Data[i], Is.EqualTo(0xFF));
            }
        }

        [Test]
        public void CreateEscapeAction_CreatesCorrectMessage()
        {
            // Act
            var message = SintranTadMessage.CreateEscapeAction();

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.ESCA));
            Assert.That(message.Data.Length, Is.EqualTo(0));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void CreateRelocate_CreatesCorrectMessage()
        {
            // Act
            var message = SintranTadMessage.CreateRelocate();

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.RLOC));
            Assert.That(message.Data.Length, Is.EqualTo(0));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void CreateDisconnect_CreatesCorrectMessage()
        {
            // Act
            var message = SintranTadMessage.CreateDisconnect();

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.DCON));
            Assert.That(message.Data.Length, Is.EqualTo(0));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void CreateError_CreatesCorrectMessage()
        {
            // Arrange
            string errorMsg = "Test error";

            // Act
            var message = SintranTadMessage.CreateError(errorMsg);

            // Assert
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.ERRS));
            Assert.That(message.GetDataAsString(), Is.EqualTo(errorMsg));
            Assert.That(message.Priority, Is.EqualTo(SintranTadMessageTypes.TadMessagePriority.High));
        }

        [Test]
        public void TryParse_ValidData_ReturnsTrue()
        {
            // Arrange
            byte[] data = new byte[] { SintranTadMessageTypes.DUMM, 0x00 };

            // Act
            bool result = SintranTadMessage.TryParse(data, out var message, out var error);

            // Assert
            Assert.That(result, Is.True);
            Assert.That(message, Is.Not.Null);
            Assert.That(error, Is.Empty);
            Assert.That(message.MessageType, Is.EqualTo(SintranTadMessageTypes.DUMM));
        }

        [Test]
        public void TryParse_InvalidData_ReturnsFalse()
        {
            // Arrange
            byte[] data = new byte[] { 0x00 }; // Too short

            // Act
            bool result = SintranTadMessage.TryParse(data, out var message, out var error);

            // Assert
            Assert.That(result, Is.False);
            Assert.That(message, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ToBytes_ReturnsCorrectData()
        {
            // Arrange
            var message = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes("Test"));

            // Act
            byte[] data1 = message.ToBytes();
            byte[] data2 = message.Build();

            // Assert
            Assert.That(data1.Length, Is.EqualTo(data2.Length));
            for (int i = 0; i < data1.Length; i++)
            {
                Assert.That(data1[i], Is.EqualTo(data2[i]));
            }
        }

        [Test]
        public void RoundTrip_AllMessageTypes_PreservesData()
        {
            // Arrange: Create various message types
            var messages = new[]
            {
                SintranTadMessage.CreateDummy(),
                SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes("Test")),
                SintranTadMessage.CreateReadyForInput(256),
                SintranTadMessage.CreateTerminalMode(true, false, true, false),
                SintranTadMessage.CreateTerminalType(0x01),
                SintranTadMessage.CreateDescriptor(80, 24, 8),
                SintranTadMessage.CreateOptionSave(true),
                SintranTadMessage.CreateEscapeAction(),
                SintranTadMessage.CreateDisconnect()
            };

            // Act & Assert
            for (int i = 0; i < messages.Length; i++)
            {
                var original = messages[i];
                byte[] encoded = original.Build();
                var decoded = SintranTadMessage.Parse(encoded);

                Assert.That(decoded.MessageType, Is.EqualTo(original.MessageType),
                    $"Message {i}: Type mismatch");
                Assert.That(decoded.Data.Length, Is.EqualTo(original.Data.Length),
                    $"Message {i}: Data length mismatch");
                Assert.That(decoded.Priority, Is.EqualTo(original.Priority),
                    $"Message {i}: Priority mismatch");
            }
        }

        [Test]
        public void ToString_ProducesReadableOutput()
        {
            // Arrange
            var message = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes("Hello"));

            // Act
            string output = message.ToString();

            // Assert
            Assert.That(output, Is.Not.Empty);
            Assert.That(output, Does.Contain("TAD"));
            Assert.That(output, Does.Contain("7BDAT"));
        }

        [Test]
        public void GetDataAsString_EmptyData_ReturnsEmptyString()
        {
            // Arrange
            var message = SintranTadMessage.CreateDummy();

            // Act
            string result = message.GetDataAsString();

            // Assert
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void GetDataAsString_WithData_ReturnsCorrectString()
        {
            // Arrange
            string testData = "SINTRAN TAD";
            var message = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes(testData));

            // Act
            string result = message.GetDataAsString();

            // Assert
            Assert.That(result, Is.EqualTo(testData));
        }
    }
}
