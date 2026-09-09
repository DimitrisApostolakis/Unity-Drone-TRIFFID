using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class StreamingAssetsPathResolver
{
    public static bool TryResolvePathFromStreamingAssetsJson(
        string configJsonFileName,
        string jsonKey,
        out string resolvedPath,
        out string error)
    {
        resolvedPath = null;
        error = null;

        if (string.IsNullOrWhiteSpace(configJsonFileName))
        {
            error = "Config JSON file name is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(jsonKey))
        {
            error = "JSON key is empty.";
            return false;
        }

        string configPath;
        try
        {
            configPath = Path.Combine(Application.streamingAssetsPath, configJsonFileName);
        }
        catch (Exception exception)
        {
            error = "Failed to resolve config JSON path: " + exception.Message;
            return false;
        }

        if (!FileExists(configPath))
        {
            error = "Config JSON file does not exist: " + configPath;
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception exception)
        {
            error = "Failed to read config JSON file '" + configPath + "': " + exception.Message;
            return false;
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (JsonException exception)
        {
            error = "Invalid JSON in config file '" + configPath + "': " + exception.Message;
            return false;
        }

        JToken pathToken;
        try
        {
            pathToken = root.SelectToken(jsonKey, false);
        }
        catch (JsonException exception)
        {
            error = "Invalid JSON key expression '" + jsonKey + "': " + exception.Message;
            return false;
        }

        if (pathToken == null)
            pathToken = root[jsonKey];

        if (pathToken == null)
        {
            error = "JSON key '" + jsonKey + "' is missing from config file: " + configPath;
            return false;
        }

        if (pathToken.Type != JTokenType.String)
        {
            error = "JSON key '" + jsonKey + "' must contain a string path.";
            return false;
        }

        string path = pathToken.Value<string>();
        if (path != null)
            path = TrimWhitespaceAndWrappingQuotes(path);

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "JSON key '" + jsonKey + "' contains an empty path.";
            return false;
        }

        resolvedPath = path;
        return true;
    }

    public static bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string TrimWhitespaceAndWrappingQuotes(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 2)
            return trimmed;

        char first = trimmed[0];
        char last = trimmed[trimmed.Length - 1];
        if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            return trimmed.Substring(1, trimmed.Length - 2).Trim();

        return trimmed;
    }
}
