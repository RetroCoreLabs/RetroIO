using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// Registry for protocol services that can handle incoming X.25 calls
    /// Provides dynamic routing based on called address and CUD patterns
    /// </summary>
    public class ProtocolServiceRegistry
    {
        private readonly ILogger _logger;
        private readonly List<IProtocolService> _services = new();
        private readonly object _lock = new();
        
        public ProtocolServiceRegistry(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        /// <summary>
        /// Register a protocol service
        /// </summary>
        /// <param name="service">Protocol service to register</param>
        public void RegisterService(IProtocolService service)
        {
            if (service == null)
                throw new ArgumentNullException(nameof(service));
                
            lock (_lock)
            {
                // Check for duplicate service names without LINQ
                bool hasDuplicate = false;
                foreach (var s in _services)
                {
                    if (s.ServiceName == service.ServiceName)
                    {
                        hasDuplicate = true;
                        break;
                    }
                }

                if (hasDuplicate)
                {
                    _logger.LogWarning("Protocol service '{ServiceName}' is already registered - replacing", service.ServiceName);
                    // Remove existing service with same name
                    for (int i = _services.Count - 1; i >= 0; i--)
                    {
                        if (_services[i].ServiceName == service.ServiceName)
                        {
                            _services.RemoveAt(i);
                        }
                    }
                }
                
                _services.Add(service);
                
                // Sort by priority (highest first) to ensure correct lookup order
                _services.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                
                _logger.LogInformation("Registered protocol service '{ServiceName}' for addresses [{Addresses}] with priority {Priority}",
                    service.ServiceName, string.Join(", ", service.LocalAddresses), service.Priority);
            }
        }
        
        /// <summary>
        /// Unregister a protocol service
        /// </summary>
        /// <param name="serviceName">Name of service to unregister</param>
        /// <returns>True if service was found and removed</returns>
        public bool UnregisterService(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
                return false;

            lock (_lock)
            {
                // Remove services with matching name without LINQ
                int removed = 0;
                for (int i = _services.Count - 1; i >= 0; i--)
                {
                    if (_services[i].ServiceName == serviceName)
                    {
                        _services.RemoveAt(i);
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    _logger.LogInformation("Unregistered protocol service '{ServiceName}'", serviceName);
                    return true;
                }
                return false;
            }
        }
        
        /// <summary>
        /// Find a protocol service that can handle the given call
        /// </summary>
        /// <param name="calledAddress">Called X.121 address</param>
        /// <param name="cud">Call User Data</param>
        /// <returns>Protocol service that can handle the call, or null if none found</returns>
        public IProtocolService? FindServiceForCall(string calledAddress, byte[] cud)
        {
            if (string.IsNullOrEmpty(calledAddress))
                return null;
                
            lock (_lock)
            {
                _logger.LogDebug("Looking for protocol service for address '{CalledAddress}' with CUD length {CUDLength}", 
                    calledAddress, cud?.Length ?? 0);
                
                // Search services in priority order (highest priority first)
                foreach (var service in _services)
                {
                    _logger.LogTrace("Checking service '{ServiceName}' for address match", service.ServiceName);
                    
                    // Check if service handles this address
                    if (!service.LocalAddresses.Contains(calledAddress))
                    {
                        _logger.LogTrace("Service '{ServiceName}' does not handle address '{CalledAddress}'", 
                            service.ServiceName, calledAddress);
                        continue;
                    }
                    
                    // Check if service can handle this CUD
                    if (!service.CanHandleCUD(cud ?? Array.Empty<byte>()))
                    {
                        _logger.LogTrace("Service '{ServiceName}' cannot handle CUD pattern", service.ServiceName);
                        continue;
                    }
                    
                    _logger.LogInformation("Found protocol service '{ServiceName}' for address '{CalledAddress}' (priority {Priority})",
                        service.ServiceName, calledAddress, service.Priority);
                    return service;
                }
                
                _logger.LogWarning("No protocol service found for address '{CalledAddress}' with CUD pattern", calledAddress);
                return null;
            }
        }
        
        /// <summary>
        /// Get all registered services
        /// </summary>
        /// <returns>Array of registered services (sorted by priority)</returns>
        public IProtocolService[] GetRegisteredServices()
        {
            lock (_lock)
            {
                return _services.ToArray();
            }
        }
        
        /// <summary>
        /// Get service by name
        /// </summary>
        /// <param name="serviceName">Name of service to find</param>
        /// <returns>Service if found, null otherwise</returns>
        public IProtocolService? GetService(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
                return null;

            lock (_lock)
            {
                // Find service by name without LINQ
                foreach (var service in _services)
                {
                    if (service.ServiceName == serviceName)
                    {
                        return service;
                    }
                }
                return null;
            }
        }
        
        /// <summary>
        /// Clear all registered services
        /// </summary>
        public void ClearAllServices()
        {
            lock (_lock)
            {
                var count = _services.Count;
                _services.Clear();
                _logger.LogInformation("Cleared all {Count} registered protocol services", count);
            }
        }
        
        /// <summary>
        /// Get statistics about registered services
        /// </summary>
        /// <returns>Statistics object</returns>
        public ProtocolServiceRegistryStats GetStatistics()
        {
            lock (_lock)
            {
                // Build service names array without LINQ
                var serviceNames = new string[_services.Count];
                for (int i = 0; i < _services.Count; i++)
                {
                    serviceNames[i] = _services[i].ServiceName;
                }

                // Count distinct addresses without LINQ
                var uniqueAddresses = new HashSet<string>();
                foreach (var service in _services)
                {
                    foreach (var address in service.LocalAddresses)
                    {
                        uniqueAddresses.Add(address);
                    }
                }

                return new ProtocolServiceRegistryStats
                {
                    TotalServices = _services.Count,
                    ServiceNames = serviceNames,
                    TotalAddresses = uniqueAddresses.Count
                };
            }
        }
    }
    
    /// <summary>
    /// Statistics about the protocol service registry
    /// </summary>
    public class ProtocolServiceRegistryStats
    {
        public int TotalServices { get; set; }
        public string[] ServiceNames { get; set; } = Array.Empty<string>();
        public int TotalAddresses { get; set; }
        
        public override string ToString()
        {
            return $"Services: {TotalServices}, Addresses: {TotalAddresses}, Names: [{string.Join(", ", ServiceNames)}]";
        }
    }
}