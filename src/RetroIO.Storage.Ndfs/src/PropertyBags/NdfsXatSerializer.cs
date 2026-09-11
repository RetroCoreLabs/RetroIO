//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RetroIO.Storage.Ndfs.PropertyBags
{
    /// <summary>
    /// Serializes and deserializes NDFS PropertyBag to/from .xat JSON files.
    /// Used for preserving NDFS metadata when copying files to filesystems that don't support extended attributes.
    /// </summary>
    public static class NdfsXatSerializer
    {
        /// <summary>
        /// Serializes an NDFS PropertyBag to JSON format for .xat file.
        /// </summary>
        public static string SerializeToJson(IDictionary<string, object> propertyBag)
        {
            if (propertyBag == null)
                throw new ArgumentNullException(nameof(propertyBag));

            var jsonDict = new Dictionary<string, object>();

            // Convert PropertyBag to JSON-serializable format
            for (int i = 0; i < propertyBag.Count; i++)
            {
                var kvp = GetKeyValuePairAtIndex(propertyBag, i);
                string key = kvp.Key;
                object value = kvp.Value;

                // Convert byte arrays to base64 strings
                if (value is byte[] byteArray)
                {
                    jsonDict[key] = Convert.ToBase64String(byteArray);
                }
                else if (value is DateTime dt)
                {
                    jsonDict[key] = dt.ToString("O"); // ISO 8601 format
                }
                else
                {
                    jsonDict[key] = value;
                }
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = null // Preserve exact key names
            };

            return JsonSerializer.Serialize(jsonDict, options);
        }

        /// <summary>
        /// Deserializes JSON from .xat file back to PropertyBag format.
        /// </summary>
        public static IDictionary<string, object> DeserializeFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentNullException(nameof(json));

            var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (jsonDict == null)
                throw new InvalidOperationException("Failed to deserialize XAT JSON");

            var propertyBag = new Dictionary<string, object>();

            for (int i = 0; i < jsonDict.Count; i++)
            {
                var kvp = GetKeyValuePairAtIndex(jsonDict, i);
                string key = kvp.Key;
                JsonElement value = kvp.Value;

                // Convert back to appropriate types
                object convertedValue;
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        string strValue = value.GetString() ?? string.Empty;

                        // Check if this is a known byte array field (more reliable than heuristic)
                        if (IsKnownByteArrayField(key))
                        {
                            try
                            {
                                convertedValue = Convert.FromBase64String(strValue);
                            }
                            catch
                            {
                                convertedValue = strValue; // Keep as string if not valid base64
                            }
                        }
                        // Try to parse as DateTime (ISO 8601 format with T separator)
                        else if (strValue.Contains("T") && DateTime.TryParse(strValue, out DateTime dt))
                        {
                            convertedValue = dt;
                        }
                        else
                        {
                            convertedValue = strValue;
                        }
                        break;

                    case JsonValueKind.Number:
                        if (value.TryGetInt32(out int intValue))
                            convertedValue = intValue;
                        else if (value.TryGetInt64(out long longValue))
                            convertedValue = longValue;
                        else if (value.TryGetUInt32(out uint uintValue))
                            convertedValue = uintValue;
                        else if (value.TryGetUInt64(out ulong ulongValue))
                            convertedValue = ulongValue;
                        else
                            convertedValue = value.GetDouble();
                        break;

                    case JsonValueKind.True:
                        convertedValue = true;
                        break;

                    case JsonValueKind.False:
                        convertedValue = false;
                        break;

                    case JsonValueKind.Null:
                        convertedValue = null!;
                        break;

                    default:
                        convertedValue = value.ToString();
                        break;
                }

                propertyBag[key] = convertedValue;
            }

            return propertyBag;
        }

        /// <summary>
        /// Creates a .xat filename from a data filename.
        /// Examples:
        ///   "FILE.DAT" -> "FILE.XAT"
        ///   "TESTPROG.PROG" -> "TESTPROG.XAT"
        /// </summary>
        public static string GetXatFileName(string dataFileName, string xatExtension = ".xat")
        {
            if (string.IsNullOrEmpty(dataFileName))
                throw new ArgumentNullException(nameof(dataFileName));

            // Remove extension from data file
            int lastDot = dataFileName.LastIndexOf('.');
            string baseName = lastDot > 0 ? dataFileName.Substring(0, lastDot) : dataFileName;

            // Append .xat extension
            return baseName + xatExtension;
        }

        /// <summary>
        /// Checks if a filename is a .xat file.
        /// </summary>
        public static bool IsXatFile(string fileName, string xatExtension = ".xat")
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            return fileName.EndsWith(xatExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the original data filename from a .xat filename.
        /// Note: Cannot determine original extension, caller must provide or enumerate.
        /// </summary>
        public static string GetBaseFileName(string xatFileName, string xatExtension = ".xat")
        {
            if (string.IsNullOrEmpty(xatFileName))
                throw new ArgumentNullException(nameof(xatFileName));

            if (!xatFileName.EndsWith(xatExtension, StringComparison.OrdinalIgnoreCase))
                return xatFileName;

            return xatFileName.Substring(0, xatFileName.Length - xatExtension.Length);
        }

        // Helper method to get KeyValuePair at index (avoiding LINQ)
        private static KeyValuePair<TKey, TValue> GetKeyValuePairAtIndex<TKey, TValue>(
            IDictionary<TKey, TValue> dictionary, int index)
        {
            int currentIndex = 0;
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (currentIndex == index)
                    return new KeyValuePair<TKey, TValue>(enumerator.Current.Key, enumerator.Current.Value);
                currentIndex++;
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        // Helper to check if a property key is a known byte array field
        private static bool IsKnownByteArrayField(string key)
        {
            // NDFS fields that are byte arrays
            return key == "ndfs:FriendList" ||
                   key == "ndfs:DataBlockIds" ||
                   key == "ndfs:SparseHoleIndices";
        }
    }
}
