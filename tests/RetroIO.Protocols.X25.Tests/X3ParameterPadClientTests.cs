using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for X.3 parameter management from PAD client perspective
    /// Tests parameter initialization, validation, updates, and protocol compliance
    /// </summary>
    [TestFixture]
    public class X3ParameterPadClientTests : TestBase
    {
        [Test]
        public void TestPadClient_X3ParametersInitialization()
        {
            // Arrange & Act
            var padId = Runtime.CreateManagedPadClient("X3_Test_PAD");
            var padClient = Runtime.GetPadClient(padId)!;
            
            // Assert
            Assert.That(padClient, Is.Not.Null);
            Assert.That(padClient.X3Protocol, Is.Not.Null);
            Assert.That(padClient.X28Protocol, Is.Not.Null);
            
            var parameters = padClient.X3Protocol.GetAllParameters();
            Assert.That(parameters, Is.Not.Null);
            Assert.That(parameters.Count, Is.EqualTo(22), "Should have all 22 X.3 parameters");
            
            // Verify key default values according to ITU-T X.3
            Assert.That(parameters[1], Is.EqualTo(1), "Parameter 1 (ESCAPE) should default to 1");
            Assert.That(parameters[2], Is.EqualTo(1), "Parameter 2 (ECHO) should default to 1");
            Assert.That(parameters[3], Is.EqualTo(13), "Parameter 3 (DATA_FORWARDING) should default to 13 (CR)");
            Assert.That(parameters[4], Is.EqualTo(0), "Parameter 4 (IDLE_TIMER) should default to 0 (disabled)");
            Assert.That(parameters[15], Is.EqualTo(1), "Parameter 15 (EDITING) should default to 1");
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadClient_X3ParameterValidation()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Validation_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            
            // Act & Assert - Test valid parameters
            Assert.That(x3Protocol.SetParameter(1, 0), Is.True, "Should accept valid ESCAPE value 0");
            Assert.That(x3Protocol.SetParameter(1, 1), Is.True, "Should accept valid ESCAPE value 1");
            Assert.That(x3Protocol.SetParameter(2, 0), Is.True, "Should accept valid ECHO value 0");
            Assert.That(x3Protocol.SetParameter(2, 1), Is.True, "Should accept valid ECHO value 1");
            Assert.That(x3Protocol.SetParameter(4, 255), Is.True, "Should accept valid IDLE_TIMER value 255");
            
            // Test invalid parameters
            Assert.That(x3Protocol.SetParameter(1, 2), Is.False, "Should reject invalid ESCAPE value 2");
            Assert.That(x3Protocol.SetParameter(2, 5), Is.False, "Should reject invalid ECHO value 5");
            Assert.That(x3Protocol.SetParameter(0, 1), Is.False, "Should reject invalid parameter number 0");
            Assert.That(x3Protocol.SetParameter(23, 1), Is.False, "Should reject invalid parameter number 23");
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadClient_X3ParameterPersistence()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Persistence_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            
            // Act - Set multiple parameters
            Assert.That(x3Protocol.SetParameter(2, 0), Is.True); // Disable echo
            Assert.That(x3Protocol.SetParameter(4, 10), Is.True); // Set idle timer to 10 * 20ms = 200ms
            Assert.That(x3Protocol.SetParameter(15, 0), Is.True); // Disable editing
            
            // Assert - Verify parameters are persisted
            var parameters = x3Protocol.GetAllParameters();
            Assert.That(parameters[2], Is.EqualTo(0), "ECHO should be disabled");
            Assert.That(parameters[4], Is.EqualTo(10), "IDLE_TIMER should be set to 10");
            Assert.That(parameters[15], Is.EqualTo(0), "EDITING should be disabled");
            
            // Verify other parameters remain unchanged
            Assert.That(parameters[1], Is.EqualTo(1), "ESCAPE should remain default");
            Assert.That(parameters[3], Is.EqualTo(13), "DATA_FORWARDING should remain default");
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadClient_X3ForwardingBehavior()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Forwarding_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            var x28Protocol = padClient.X28Protocol;
            
            var forwardedData = new List<string>();
            
            // Subscribe to data forwarding events
            padClient.DataSent += (sender, args) =>
            {
                forwardedData.Add(args.Data);
                Logger.LogDebug("Data forwarded: '{Data}'", args.Data);
            };
            
            // Test 1: Default behavior - forward on CR (parameter 3 = 13)
            var processed = x28Protocol.ProcessCharacter('H');
            Assert.That(processed, Is.True, "Should process character successfully");
            
            processed = x28Protocol.ProcessCharacter('i');
            Assert.That(processed, Is.True, "Should process character successfully");
            
            processed = x28Protocol.ProcessCharacter('\r'); // CR character
            Assert.That(processed, Is.True, "Should process CR character successfully");
            // Note: In actual implementation, forwarding would be triggered here via events
            
            // Test 2: Change forwarding behavior - forward on specific character count
            Assert.That(x3Protocol.SetParameter(12, 3), Is.True); // Forward every 3 characters
            Assert.That(x3Protocol.GetParameter(12), Is.EqualTo(3), "Parameter 12 should be set to 3");
            
            // Test 3: Disable forwarding on CR
            Assert.That(x3Protocol.SetParameter(3, 0), Is.True); // Disable CR forwarding
            Assert.That(x3Protocol.GetParameter(3), Is.EqualTo(0), "Parameter 3 should be disabled");
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadClient_X3EchoControl()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Echo_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            var x28Protocol = padClient.X28Protocol;
            
            var echoedCharacters = new List<char>();
            
            // Subscribe to character echo events
            padClient.CharacterEcho += (sender, args) =>
            {
                echoedCharacters.Add((char)args.Character);
                Logger.LogDebug("Character echoed: '{Character}' (0x{Value:X2})", (char)args.Character, args.Character);
            };
            
            // Test 1: Default behavior - echo enabled (parameter 2 = 1)
            Assert.That(x3Protocol.GetParameter(2), Is.EqualTo(1), "Echo should be enabled by default");
            
            var response = x28Protocol.ProcessCharacter('A');
            // In actual implementation, echo would be generated here
            
            // Test 2: Disable echo
            Assert.That(x3Protocol.SetParameter(2, 0), Is.True, "Should disable echo");
            Assert.That(x3Protocol.GetParameter(2), Is.EqualTo(0), "Echo should be disabled");
            
            response = x28Protocol.ProcessCharacter('B');
            // No echo should be generated
            
            // Test 3: Re-enable echo
            Assert.That(x3Protocol.SetParameter(2, 1), Is.True, "Should re-enable echo");
            Assert.That(x3Protocol.GetParameter(2), Is.EqualTo(1), "Echo should be enabled");
            
            response = x28Protocol.ProcessCharacter('C');
            // Echo should be generated again
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadClient_X3EditingControl()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Editing_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            var x28Protocol = padClient.X28Protocol;
            
            // Test 1: Default editing behavior (parameter 15 = 1, enabled)
            Assert.That(x3Protocol.GetParameter(15), Is.EqualTo(1), "Editing should be enabled by default");
            Assert.That(x3Protocol.GetParameter(16), Is.EqualTo(127), "Character delete should default to DEL (127)");
            Assert.That(x3Protocol.GetParameter(17), Is.EqualTo(24), "Line delete should default to CAN (24)");
            
            // Test character delete key
            var response = x28Protocol.ProcessCharacter((char)127); // DEL character
            // Should trigger character deletion in editing mode
            
            // Test 2: Disable editing
            Assert.That(x3Protocol.SetParameter(15, 0), Is.True, "Should disable editing");
            Assert.That(x3Protocol.GetParameter(15), Is.EqualTo(0), "Editing should be disabled");
            
            response = x28Protocol.ProcessCharacter((char)127); // DEL character
            // Should be treated as normal data, not editing command
            
            // Test 3: Change editing keys
            Assert.That(x3Protocol.SetParameter(15, 1), Is.True, "Re-enable editing");
            Assert.That(x3Protocol.SetParameter(16, 8), Is.True, "Set character delete to backspace");
            Assert.That(x3Protocol.GetParameter(16), Is.EqualTo(8), "Character delete should be backspace");
            
            response = x28Protocol.ProcessCharacter((char)8); // Backspace character
            // Should trigger character deletion
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadClient_X3ParameterProfiles()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Profiles_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            
            // Act - Apply transparent profile (all parameters to minimal values)
            var transparentParams = new Dictionary<byte, byte>
            {
                [1] = 0,   // ESCAPE - disabled
                [2] = 0,   // ECHO - disabled  
                [3] = 0,   // DATA_FORWARDING - disabled
                [4] = 0,   // IDLE_TIMER - disabled
                [15] = 0   // EDITING - disabled
            };
            
            foreach (var param in transparentParams)
            {
                Assert.That(x3Protocol.SetParameter(param.Key, param.Value), Is.True, 
                    $"Should set parameter {param.Key} to {param.Value}");
            }
            
            // Verify transparent profile applied
            var allParams = x3Protocol.GetAllParameters();
            foreach (var param in transparentParams)
            {
                Assert.That(allParams[param.Key], Is.EqualTo(param.Value), 
                    $"Parameter {param.Key} should be {param.Value} in transparent profile");
            }
            
            // Act - Reset to default profile
            x3Protocol.SetDefaultProfile();
            
            // Verify defaults restored
            allParams = x3Protocol.GetAllParameters();
            Assert.That(allParams[1], Is.EqualTo(1), "ESCAPE should be restored to default");
            Assert.That(allParams[2], Is.EqualTo(1), "ECHO should be restored to default");
            Assert.That(allParams[3], Is.EqualTo(13), "DATA_FORWARDING should be restored to default");
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        [Test] 
        public void TestPadClient_X3ParameterBoundaryValues()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("X3_Boundary_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var x3Protocol = padClient.X3Protocol;
            
            // Test boundary values for each parameter type
            var boundaryTests = new Dictionary<byte, (byte min, byte max)>
            {
                [1] = (0, 1),     // ESCAPE: 0-1
                [2] = (0, 1),     // ECHO: 0-1  
                [3] = (0, 126),   // DATA_FORWARDING: 0-126
                [4] = (0, 255),   // IDLE_TIMER: 0-255
                [9] = (0, 7),     // PADDING_AFTER_CR: 0-7
                [10] = (0, 255),  // LINE_FOLDING: 0-255
                [12] = (0, 255),  // FORWARD_ON_N_CHARACTERS: 0-255
                [16] = (0, 127),  // CHARACTER_DELETE: 0-127
                [21] = (0, 3),    // PARITY_TREATMENT: 0-3
                [22] = (0, 255)   // PAGE_WAIT: 0-255
            };
            
            foreach (var test in boundaryTests)
            {
                byte paramNum = test.Key;
                byte minVal = test.Value.min;
                byte maxVal = test.Value.max;
                
                // Test minimum valid value
                Assert.That(x3Protocol.SetParameter(paramNum, minVal), Is.True, 
                    $"Parameter {paramNum} should accept minimum value {minVal}");
                Assert.That(x3Protocol.GetParameter(paramNum), Is.EqualTo(minVal),
                    $"Parameter {paramNum} should store minimum value {minVal}");
                
                // Test maximum valid value
                Assert.That(x3Protocol.SetParameter(paramNum, maxVal), Is.True,
                    $"Parameter {paramNum} should accept maximum value {maxVal}");
                Assert.That(x3Protocol.GetParameter(paramNum), Is.EqualTo(maxVal),
                    $"Parameter {paramNum} should store maximum value {maxVal}");
                
                // Test invalid value above maximum
                if (maxVal < 255)
                {
                    Assert.That(x3Protocol.SetParameter(paramNum, (byte)(maxVal + 1)), Is.False,
                        $"Parameter {paramNum} should reject value above maximum ({maxVal + 1})");
                }
            }
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
        
        /// <summary>
        /// Helper method to wait for a condition with timeout
        /// </summary>
        private new bool WaitForCondition(Func<bool> condition, int timeoutMs)
        {
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(50);
            }
            return false;
        }
    }
}