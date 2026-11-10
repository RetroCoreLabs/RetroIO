//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// RetroServer's start-up file, appsettings.json in the program's folder: log levels, file storage, Azure and the
/// management addresses. Editable from RetroAdmin since 2026-11-10 (Ronny's decision).
/// </summary>
/// <remarks>
/// A save changes only the keys below and keeps every other key in the file as it is.
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
public class StartupController : ControllerBase
{
    private static readonly string[] LogLevelNames = { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" };

    private readonly string _filePath;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="environment">
    /// The host, whose content folder holds appsettings.json (the same folder the host reads it from).
    /// </param>
    /// <param name="folder">
    /// Tests pass a temp folder here instead; it wins over <paramref name="environment"/>.
    /// </param>
    public StartupController(IHostEnvironment? environment = null, string? folder = null)
    {
        string root = folder ?? environment?.ContentRootPath ?? AppContext.BaseDirectory;
        _filePath = Path.Combine(root, "appsettings.json");
    }

    /// <summary>
    /// Gets the start-up settings as the file holds them. The Azure connection string is never sent back.
    /// </summary>
    /// <returns>
    /// The settings.
    /// </returns>
    [HttpGet]
    public ActionResult<ApiResponse<StartupSettingsDto>> GetSettings()
    {
        return Ok(ApiResponse<StartupSettingsDto>.Ok(ToDto(Load())));
    }

    /// <summary>
    /// Writes the start-up settings into appsettings.json, keeping every key this page does not show.
    /// </summary>
    /// <param name="request">
    /// Every setting on the page.
    /// </param>
    /// <returns>
    /// The settings as saved, or why they were refused.
    /// </returns>
    [HttpPut]
    public ActionResult<ApiResponse<StartupSettingsDto>> PutSettings([FromBody] StartupSettingsDto request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<StartupSettingsDto>.Fail("No settings were sent."));
        }

        string? problem = Check(request);
        if (problem != null)
        {
            return BadRequest(ApiResponse<StartupSettingsDto>.Fail(problem));
        }

        JsonObject root = Load();

        JsonObject logLevel = new JsonObject();
        foreach (KeyValuePair<string, string> pair in request.LogLevels)
        {
            logLevel[pair.Key.Trim()] = CanonicalLevel(pair.Value);
        }

        Child(root, "Logging")["LogLevel"] = logLevel;
        Child(root, "FileSystem")["Backend"] = string.Equals(request.FileSystemBackend, "Azure", StringComparison.OrdinalIgnoreCase) ? "Azure" : "Local";

        JsonObject storage = Child(Child(root, "Azure"), "Storage");
        if (request.NewAzureConnectionString != null)
        {
            if (request.NewAzureConnectionString.Length == 0)
            {
                storage.Remove("ConnectionString");
            }
            else
            {
                storage["ConnectionString"] = request.NewAzureConnectionString;
            }
        }

        storage["ContainerName"] = request.AzureContainerName.Trim();
        storage["UseManagedIdentity"] = request.AzureUseManagedIdentity;
        storage["StorageAccountUri"] = request.AzureStorageAccountUri.Trim();
        storage["CreateContainerIfNotExists"] = request.AzureCreateContainerIfNotExists;

        JsonObject management = Child(root, "Management");
        if (string.IsNullOrWhiteSpace(request.ManagementUrls))
        {
            management.Remove("Urls");
        }
        else
        {
            management["Urls"] = request.ManagementUrls.Trim();
        }

        System.IO.File.WriteAllText(_filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Ok(ApiResponse<StartupSettingsDto>.Ok(ToDto(root)));
    }

    private static string? Check(StartupSettingsDto request)
    {
        if (request.LogLevels == null || request.LogLevels.Count == 0)
        {
            return "At least the Default log level is needed.";
        }

        foreach (KeyValuePair<string, string> pair in request.LogLevels)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return "A log level has no category name.";
            }

            if (CanonicalLevel(pair.Value) == null)
            {
                return "The log level for " + pair.Key + " must be one of: " + string.Join(", ", LogLevelNames) + ".";
            }
        }

        if (!string.Equals(request.FileSystemBackend, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.FileSystemBackend, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            return "File storage must be Local or Azure.";
        }

        string[] urls = (request.ManagementUrls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < urls.Length; i++)
        {
            if (!Uri.TryCreate(urls[i], UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "'" + urls[i] + "' is not an http:// or https:// address.";
            }
        }

        return null;
    }

    private static string? CanonicalLevel(string? level)
    {
        for (int i = 0; i < LogLevelNames.Length; i++)
        {
            if (string.Equals(LogLevelNames[i], level?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return LogLevelNames[i];
            }
        }

        return null;
    }

    private JsonObject Load()
    {
        if (!System.IO.File.Exists(_filePath))
        {
            return new JsonObject();
        }

        JsonNode? node = JsonNode.Parse(System.IO.File.ReadAllText(_filePath),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node as JsonObject ?? new JsonObject();
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        JsonObject made = new JsonObject();
        parent[name] = made;
        return made;
    }

    private static string Text(JsonObject? parent, string name, string fallback)
    {
        JsonNode? node = parent?[name];
        return node is JsonValue value && value.TryGetValue(out string? text) && text != null ? text : fallback;
    }

    private static bool Flag(JsonObject? parent, string name, bool fallback)
    {
        JsonNode? node = parent?[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool b))
            {
                return b;
            }

            if (value.TryGetValue(out string? s) && bool.TryParse(s, out bool parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private StartupSettingsDto ToDto(JsonObject root)
    {
        // Defaults are what the server uses when a key is missing (RetroServerHostingExtensions, Program.cs).
        JsonObject? storage = (root["Azure"] as JsonObject)?["Storage"] as JsonObject;
        StartupSettingsDto dto = new StartupSettingsDto
        {
            FilePath = _filePath,
            DataFolder = PathHelper.GetRetroFSPath(),
            FileSystemBackend = Text(root["FileSystem"] as JsonObject, "Backend", "Local"),
            AzureConnectionStringSet = !string.IsNullOrEmpty(Text(storage, "ConnectionString", string.Empty)),
            AzureContainerName = Text(storage, "ContainerName", "retrofs"),
            AzureUseManagedIdentity = Flag(storage, "UseManagedIdentity", false),
            AzureStorageAccountUri = Text(storage, "StorageAccountUri", string.Empty),
            AzureCreateContainerIfNotExists = Flag(storage, "CreateContainerIfNotExists", true),
            ManagementUrls = Text(root["Management"] as JsonObject, "Urls", string.Empty),
        };

        if ((root["Logging"] as JsonObject)?["LogLevel"] is JsonObject levels)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in levels)
            {
                dto.LogLevels[pair.Key] = pair.Value is JsonValue v && v.TryGetValue(out string? t) && t != null ? t : string.Empty;
            }
        }

        if (dto.LogLevels.Count == 0)
        {
            dto.LogLevels["Default"] = "Information";
        }

        return dto;
    }
}
