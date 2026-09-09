// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Shared path-loading rules for the Gaussian asset and the Drone project components.
    /// Absolute paths are accepted. Relative paths are resolved against the selected root.
    /// A shared JSON file is always read from StreamingAssets, matching the renderer Inspector.
    /// </summary>
    public static class ProjectPathResolver
    {
        public enum PathRoot
        {
            ProjectRoot,
            StreamingAssets,
            PersistentDataPath,
            DataPath
        }

        public static bool TryResolveConfiguredPath(
            string customPath,
            PathRoot inputPathRoot,
            bool useSharedPathJson,
            string sharedPathsJsonFile,
            string pathJsonKey,
            out string resolvedPath,
            out string error)
        {
            string rawPath = customPath;
            if (useSharedPathJson &&
                !TryReadPathFromStreamingAssetsJson(
                    sharedPathsJsonFile, pathJsonKey, out rawPath, out error))
            {
                resolvedPath = string.Empty;
                return false;
            }

            return TryResolvePath(rawPath, inputPathRoot, out resolvedPath, out error);
        }

        public static bool TryResolvePath(
            string rawPath,
            PathRoot root,
            out string resolvedPath,
            out string error)
        {
            resolvedPath = string.Empty;
            error = string.Empty;
            rawPath = TrimWhitespaceAndWrappingQuotes(rawPath);
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                error = "Path is empty.";
                return false;
            }

            try
            {
                resolvedPath = Path.IsPathRooted(rawPath)
                    ? Path.GetFullPath(rawPath)
                    : Path.GetFullPath(Path.Combine(
                        GetBaseDirectory(root),
                        rawPath.Replace('/', Path.DirectorySeparatorChar)));
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not resolve path '{rawPath}': {exception.Message}";
                return false;
            }
        }

        public static string GetBaseDirectory(PathRoot root)
        {
            switch (root)
            {
                case PathRoot.StreamingAssets:
                    return Application.streamingAssetsPath;
                case PathRoot.PersistentDataPath:
                    return Application.persistentDataPath;
                case PathRoot.DataPath:
                    return Application.dataPath;
                case PathRoot.ProjectRoot:
                default:
                    DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                    return parent != null ? parent.FullName : Application.dataPath;
            }
        }

        public static bool TryGetUnityProjectRelativePath(
            string resolvedPath, out string projectRelativePath)
        {
            projectRelativePath = string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return false;

            try
            {
                string projectRoot = GetBaseDirectory(PathRoot.ProjectRoot);
                string relative = Path.GetRelativePath(
                    Path.GetFullPath(projectRoot), Path.GetFullPath(resolvedPath)).Replace('\\', '/');
                if (relative == "Assets" || relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    relative == "Packages" || relative.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    projectRelativePath = relative;
                    return true;
                }
            }
            catch
            {
                // Callers use false to choose non-AssetDatabase loading.
            }
            return false;
        }

        public static bool TryReadPathFromStreamingAssetsJson(
            string configJsonFileName,
            string jsonKey,
            out string path,
            out string error)
        {
            path = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(configJsonFileName))
            {
                error = "Shared config file is empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(jsonKey))
            {
                error = "Path JSON key is empty.";
                return false;
            }

            if (!TryResolvePath(
                    configJsonFileName,
                    PathRoot.StreamingAssets,
                    out string configPath,
                    out error))
                return false;

            if (!File.Exists(configPath))
            {
                error = "Shared config file does not exist: " + configPath;
                return false;
            }

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(configPath));
                JToken token = root.SelectToken(jsonKey, false) ?? root[jsonKey];
                if (token == null)
                {
                    error = $"JSON key '{jsonKey}' is missing from: {configPath}";
                    return false;
                }
                if (token.Type != JTokenType.String)
                {
                    error = $"JSON key '{jsonKey}' must contain a string path.";
                    return false;
                }

                path = TrimWhitespaceAndWrappingQuotes(token.Value<string>());
                if (!string.IsNullOrWhiteSpace(path))
                    return true;

                error = $"JSON key '{jsonKey}' contains an empty path.";
                return false;
            }
            catch (Exception exception)
            {
                error = "Could not read shared path config: " + exception.Message;
                return false;
            }
        }

        private static string TrimWhitespaceAndWrappingQuotes(string value)
        {
            if (value == null)
                return string.Empty;
            string trimmed = value.Trim();
            if (trimmed.Length < 2)
                return trimmed;
            char first = trimmed[0];
            char last = trimmed[trimmed.Length - 1];
            return (first == '"' && last == '"') || (first == '\'' && last == '\'')
                ? trimmed.Substring(1, trimmed.Length - 2).Trim()
                : trimmed;
        }
    }
}
