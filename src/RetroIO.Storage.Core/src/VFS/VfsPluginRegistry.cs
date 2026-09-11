//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// Registry for VFS plugins (archive handlers, special formats).
    /// Manages plugin discovery, loading, and lookup.
    /// </summary>
    public class VfsPluginRegistry
    {
        private readonly List<IExtFsPlugin> _plugins;
        private readonly Dictionary<string, IExtFsPlugin> _handlerMap;

        /// <summary>
        /// Initializes a new plugin registry.
        /// </summary>
        public VfsPluginRegistry()
        {
            _plugins = new List<IExtFsPlugin>();
            _handlerMap = new Dictionary<string, IExtFsPlugin>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Registers a plugin with the registry.
        /// </summary>
        /// <param name="plugin">Plugin to register</param>
        /// <exception cref="ArgumentNullException">Plugin is null</exception>
        /// <exception cref="ArgumentException">Handler name already registered</exception>
        public void RegisterPlugin(IExtFsPlugin plugin)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            string handlerName = plugin.HandlerName;
            if (string.IsNullOrWhiteSpace(handlerName))
                throw new ArgumentException("Plugin handler name cannot be null or empty.", nameof(plugin));

            string normalizedName = handlerName.ToLowerInvariant();

            if (_handlerMap.ContainsKey(normalizedName))
                throw new ArgumentException($"Plugin handler '{handlerName}' is already registered.", nameof(plugin));

            _plugins.Add(plugin);
            _handlerMap[normalizedName] = plugin;
        }

        /// <summary>
        /// Unregisters a plugin from the registry.
        /// </summary>
        /// <param name="plugin">Plugin to unregister</param>
        /// <returns>True if plugin was unregistered, false if not found</returns>
        public bool UnregisterPlugin(IExtFsPlugin plugin)
        {
            if (plugin == null)
                return false;

            bool removed = _plugins.Remove(plugin);
            if (removed)
            {
                _handlerMap.Remove(plugin.HandlerName.ToLowerInvariant());
            }
            return removed;
        }

        /// <summary>
        /// Gets a plugin by handler name.
        /// </summary>
        /// <param name="handlerName">Handler name (case-insensitive)</param>
        /// <returns>Plugin instance, or null if not found</returns>
        public IExtFsPlugin? GetPluginByHandler(string handlerName)
        {
            if (string.IsNullOrWhiteSpace(handlerName))
                return null;

            return _handlerMap.TryGetValue(handlerName.ToLowerInvariant(), out IExtFsPlugin? plugin) ? plugin : null;
        }

        /// <summary>
        /// Gets a plugin by file extension.
        /// Returns the first plugin that supports the given extension.
        /// </summary>
        /// <param name="path">File path to check</param>
        /// <returns>Plugin instance, or null if no plugin supports this extension</returns>
        public IExtFsPlugin? GetPluginByExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            for (int i = 0; i < _plugins.Count; i++)
            {
                if (_plugins[i].CanHandle(path))
                    return _plugins[i];
            }

            return null;
        }

        /// <summary>
        /// Detects which plugin can handle a stream based on magic number.
        /// Returns the first plugin that recognizes the stream format.
        /// Stream position will be reset to 0.
        /// </summary>
        /// <param name="stream">Stream to inspect (must be seekable)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Plugin instance, or null if no plugin recognizes the format</returns>
        /// <exception cref="ArgumentException">Stream is not seekable</exception>
        public async Task<IExtFsPlugin?> DetectPluginAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanSeek)
                throw new ArgumentException("Stream must be seekable for plugin detection.", nameof(stream));

            long originalPosition = stream.Position;

            try
            {
                for (int i = 0; i < _plugins.Count; i++)
                {
                    stream.Position = 0; // Reset for each plugin
                    if (await _plugins[i].CanHandleAsync(stream, cancellationToken))
                    {
                        return _plugins[i];
                    }
                }

                return null;
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }

        /// <summary>
        /// Gets all registered plugins.
        /// </summary>
        /// <returns>Array of plugin instances</returns>
        public IExtFsPlugin[] GetAllPlugins()
        {
            IExtFsPlugin[] result = new IExtFsPlugin[_plugins.Count];
            _plugins.CopyTo(result);
            return result;
        }

        /// <summary>
        /// Gets all registered handler names.
        /// </summary>
        /// <returns>Array of handler names</returns>
        public string[] GetHandlerNames()
        {
            string[] result = new string[_handlerMap.Count];
            int index = 0;

            Dictionary<string, IExtFsPlugin>.Enumerator enumerator = _handlerMap.GetEnumerator();
            while (enumerator.MoveNext())
            {
                result[index++] = enumerator.Current.Key;
            }
            enumerator.Dispose();

            return result;
        }

        /// <summary>
        /// Loads plugins from assemblies in a directory.
        /// Scans all .dll files and searches for IExtFsPlugin implementations.
        /// </summary>
        /// <param name="pluginPath">Path to plugins directory</param>
        /// <returns>Number of plugins loaded</returns>
        public int LoadPluginsFromDirectory(string pluginPath)
        {
            if (string.IsNullOrWhiteSpace(pluginPath))
                throw new ArgumentException("Plugin path cannot be null or empty.", nameof(pluginPath));

            if (!Directory.Exists(pluginPath))
                return 0;

            int loadedCount = 0;
            string[] dllFiles = Directory.GetFiles(pluginPath, "*.dll");

            for (int i = 0; i < dllFiles.Length; i++)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(dllFiles[i]);
                    Type[] types = assembly.GetTypes();

                    for (int j = 0; j < types.Length; j++)
                    {
                        Type type = types[j];
                        if (typeof(IExtFsPlugin).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                        {
                            try
                            {
                                IExtFsPlugin? plugin = Activator.CreateInstance(type) as IExtFsPlugin;
                                if (plugin != null)
                                {
                                    RegisterPlugin(plugin);
                                    loadedCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                // Log but continue loading other plugins
                                Console.WriteLine($"Failed to create plugin instance from {type.FullName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log but continue loading other DLLs
                    Console.WriteLine($"Failed to load plugin assembly {dllFiles[i]}: {ex.Message}");
                }
            }

            return loadedCount;
        }

        /// <summary>
        /// Clears all registered plugins.
        /// </summary>
        public void Clear()
        {
            _plugins.Clear();
            _handlerMap.Clear();
        }

        /// <summary>
        /// Gets the number of registered plugins.
        /// </summary>
        public int Count => _plugins.Count;
    }
}
