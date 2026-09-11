//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// VFS resolver for URN-based provider selection with recursive nesting support.
    /// Maps URI schemes to appropriate IVolumeProvider implementations.
    /// Supports nested URNs for archives within archives (e.g., ftp://host/file.zip#zip#/inner.txt).
    /// </summary>
    /// <remarks>
    /// Usage:
    /// <code>
    /// var resolver = new VfsResolver(pluginRegistry);
    /// resolver.Register(new NativeVolumeProvider());
    /// resolver.Register(new FtpVolumeProvider());
    ///
    /// // Simple resolution
    /// IVolumeProvider? provider = resolver.Resolve("ftp://server/path");
    ///
    /// // Recursive nesting
    /// Stream stream = await resolver.OpenRecursiveAsync("ftp://server/archive.zip#zip#/file.txt");
    /// </code>
    /// </remarks>
    public class VfsResolver
    {
        private readonly Dictionary<string, IVolumeProvider> _providers;
        private readonly VfsPluginRegistry? _pluginRegistry;

        /// <summary>
        /// Initializes a new VFS resolver.
        /// </summary>
        /// <param name="pluginRegistry">Optional plugin registry for archive/container support</param>
        public VfsResolver(VfsPluginRegistry? pluginRegistry = null)
        {
            _providers = new Dictionary<string, IVolumeProvider>(StringComparer.OrdinalIgnoreCase);
            _pluginRegistry = pluginRegistry;
        }

        /// <summary>
        /// Registers a volume provider with the resolver.
        /// Provider will be mapped by all schemes it supports.
        /// </summary>
        /// <param name="provider">Volume provider to register</param>
        /// <exception cref="ArgumentNullException">Provider is null</exception>
        public void Register(IVolumeProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            string[] schemes = provider.SupportedSchemes;
            if (schemes == null || schemes.Length == 0)
                throw new ArgumentException($"Provider '{provider.Type}' does not declare any supported schemes.", nameof(provider));

            for (int i = 0; i < schemes.Length; i++)
            {
                string scheme = schemes[i];
                if (string.IsNullOrWhiteSpace(scheme))
                    continue;

                string normalizedScheme = scheme.ToLowerInvariant();
                _providers[normalizedScheme] = provider;
            }
        }

        /// <summary>
        /// Unregisters a volume provider and all its schemes.
        /// </summary>
        /// <param name="provider">Provider to unregister</param>
        public void Unregister(IVolumeProvider provider)
        {
            if (provider == null)
                return;

            string[] schemes = provider.SupportedSchemes;
            if (schemes == null)
                return;

            for (int i = 0; i < schemes.Length; i++)
            {
                string scheme = schemes[i];
                if (string.IsNullOrWhiteSpace(scheme))
                    continue;

                string normalizedScheme = scheme.ToLowerInvariant();
                if (_providers.TryGetValue(normalizedScheme, out IVolumeProvider? registered) && registered == provider)
                {
                    _providers.Remove(normalizedScheme);
                }
            }
        }

        /// <summary>
        /// Resolves a URN to the appropriate volume provider based on scheme.
        /// </summary>
        /// <param name="urn">URN string (e.g., "ftp://server/path" or "file:///C:/path")</param>
        /// <returns>Volume provider that handles the URN scheme, or null if no provider found</returns>
        /// <exception cref="ArgumentException">URN is null, empty, or invalid</exception>
        public IVolumeProvider? Resolve(string urn)
        {
            if (string.IsNullOrWhiteSpace(urn))
                throw new ArgumentException("URN cannot be null or empty.", nameof(urn));

            // Parse URN to extract scheme
            Uri uri;
            try
            {
                uri = new Uri(urn);
            }
            catch (UriFormatException ex)
            {
                throw new ArgumentException($"Invalid URN format: {urn}", nameof(urn), ex);
            }

            string scheme = uri.Scheme.ToLowerInvariant();

            // Lookup provider by scheme
            if (_providers.TryGetValue(scheme, out IVolumeProvider? provider))
                return provider;

            return null;
        }

        /// <summary>
        /// Gets all registered providers.
        /// </summary>
        /// <returns>Array of registered providers (duplicates removed)</returns>
        public IVolumeProvider[] GetRegisteredProviders()
        {
            // Use HashSet to remove duplicates (same provider can handle multiple schemes)
            HashSet<IVolumeProvider> uniqueProviders = new HashSet<IVolumeProvider>();

            Dictionary<string, IVolumeProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                uniqueProviders.Add(enumerator.Current.Value);
            }
            enumerator.Dispose();

            IVolumeProvider[] result = new IVolumeProvider[uniqueProviders.Count];
            uniqueProviders.CopyTo(result);
            return result;
        }

        /// <summary>
        /// Gets all registered schemes.
        /// </summary>
        /// <returns>Array of scheme names (e.g., ["file", "ftp", "sftp", "tnfs"])</returns>
        public string[] GetRegisteredSchemes()
        {
            string[] schemes = new string[_providers.Count];
            int index = 0;

            Dictionary<string, IVolumeProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                schemes[index++] = enumerator.Current.Key;
            }
            enumerator.Dispose();

            return schemes;
        }

        /// <summary>
        /// Checks if a scheme is supported.
        /// </summary>
        /// <param name="scheme">Scheme name (e.g., "ftp", "file")</param>
        /// <returns>True if scheme is registered</returns>
        public bool IsSchemeSupported(string scheme)
        {
            if (string.IsNullOrWhiteSpace(scheme))
                return false;

            return _providers.ContainsKey(scheme.ToLowerInvariant());
        }

        /// <summary>
        /// Clears all registered providers.
        /// </summary>
        public void Clear()
        {
            _providers.Clear();
        }

        /// <summary>
        /// Opens a file with recursive nesting support.
        /// Supports nested URNs like: ftp://host/archive.zip#zip#/file.txt
        /// </summary>
        /// <param name="urn">URN with optional fragments for nesting</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Stream to the requested file</returns>
        /// <exception cref="ArgumentException">URN is invalid</exception>
        /// <exception cref="NotSupportedException">No provider or plugin found for scheme/handler</exception>
        /// <exception cref="FileNotFoundException">File not found</exception>
        public async Task<Stream> OpenRecursiveAsync(string urn, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(urn))
                throw new ArgumentException("URN cannot be null or empty.", nameof(urn));

            // Split by fragment (#)
            string[] parts = urn.Split('#');

            // Part 0: Base URN (scheme://authority/path)
            string baseUrn = parts[0];
            Uri uri = new Uri(baseUrn);

            IVolumeProvider? provider = Resolve(baseUrn);
            if (provider == null)
                throw new NotSupportedException($"No provider registered for scheme '{uri.Scheme}'");

            // No nesting - simple case
            if (parts.Length == 1)
            {
                UrnContext context = new UrnContext { VolumeId = uri.LocalPath, ReadOnly = true };
                return await provider.OpenReadAsync(context, uri.LocalPath, cancellationToken);
            }

            // Nesting detected - use plugin system
            if (_pluginRegistry == null)
                throw new NotSupportedException("Plugin registry not configured for nested URN support.");

            // Get outer stream from base provider
            UrnContext baseContext = new UrnContext { VolumeId = uri.LocalPath, ReadOnly = true };
            Stream currentStream = await provider.OpenReadAsync(baseContext, uri.LocalPath, cancellationToken);

            // Process nested handlers
            // Format: base-urn#handler1#handler2#...#inner-path
            // Example: ftp://host/file.zip#zip#/readme.txt
            //   parts[0] = ftp://host/file.zip
            //   parts[1] = zip (handler)
            //   parts[2] = /readme.txt (inner path)

            for (int i = 1; i < parts.Length - 1; i++)
            {
                string handlerName = parts[i];
                IExtFsPlugin? plugin = _pluginRegistry.GetPluginByHandler(handlerName);

                if (plugin == null)
                    throw new NotSupportedException($"No plugin registered for handler '{handlerName}'");

                // Mount the current stream in the plugin
                await plugin.MountAsync(currentStream, cancellationToken);

                // If there are more layers, we need the intermediate stream
                // For now, plugins handle the inner path directly in the last iteration
            }

            // Last part is the inner path
            string innerPath = parts[^1];
            string lastHandler = parts[^2];

            IExtFsPlugin? finalPlugin = _pluginRegistry.GetPluginByHandler(lastHandler);
            if (finalPlugin == null)
                throw new NotSupportedException($"No plugin registered for handler '{lastHandler}'");

            // Return stream to final file
            UrnContext pluginContext = new UrnContext { VolumeId = lastHandler, ReadOnly = true };
            return await finalPlugin.OpenReadAsync(pluginContext, innerPath, cancellationToken);
        }

        /// <summary>
        /// Parses a nested URN into its components.
        /// </summary>
        /// <param name="urn">Nested URN (e.g., "ftp://host/file.zip#zip#/inner.txt")</param>
        /// <returns>Tuple of (base URN, handler chain, inner path)</returns>
        public (string BaseUrn, string[] Handlers, string InnerPath) ParseNestedUrn(string urn)
        {
            if (string.IsNullOrWhiteSpace(urn))
                throw new ArgumentException("URN cannot be null or empty.", nameof(urn));

            string[] parts = urn.Split('#');

            if (parts.Length == 1)
            {
                // No nesting
                return (parts[0], Array.Empty<string>(), string.Empty);
            }

            string baseUrn = parts[0];
            string innerPath = parts[^1];

            // Middle parts are handlers
            string[] handlers = new string[parts.Length - 2];
            for (int i = 1; i < parts.Length - 1; i++)
            {
                handlers[i - 1] = parts[i];
            }

            return (baseUrn, handlers, innerPath);
        }
    }
}
