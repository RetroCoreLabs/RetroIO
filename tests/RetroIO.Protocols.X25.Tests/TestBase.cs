using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Base class for all X.25 Emulator NUnit tests
    /// Provides common setup/teardown and logging infrastructure
    /// </summary>
    public abstract class TestBase
    {
        protected ILogger Logger { get; private set; }
        protected ILoggerFactory LoggerFactory { get; private set; }
        protected ProtocolRuntime Runtime { get; private set; }
        protected RuntimeConfig Config { get; private set; }
        
        [SetUp]
        public virtual void Setup()
        {
            // Create logger factory with debug level
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            });
            
            // Create logger for derived test class
            Logger = LoggerFactory.CreateLogger(GetType());
            
            // Create default runtime config
            Config = new RuntimeConfig();
            Config.X25.LocalMachineId = "200"; // Default local machine ID for TAD server
            Config.TestMode = true; // Enable mock serial ports for testing
            
            // Create protocol runtime
            Runtime = new ProtocolRuntime(Logger, Config);
            
            Logger.LogInformation("Test setup complete for {TestClass}", GetType().Name);
        }
        
        [TearDown]
        public virtual void TearDown()
        {
            Runtime?.Dispose();
            LoggerFactory?.Dispose();
            Logger?.LogInformation("Test teardown complete for {TestClass}", GetType().Name);
        }
        
        /// <summary>
        /// Helper method to wait for a condition with timeout
        /// </summary>
        protected bool WaitForCondition(Func<bool> condition, int timeoutMs = 1000)
        {
            var startTime = DateTime.UtcNow;
            while (!condition() && (DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                System.Threading.Thread.Sleep(10);
            }
            return condition();
        }
    }
}