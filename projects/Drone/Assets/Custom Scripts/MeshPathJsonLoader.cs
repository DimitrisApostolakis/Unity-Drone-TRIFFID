using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

public enum PathSource
{
    JsonFile,
    ManualOverride
}

public enum MeshLoadMode
{
    Auto,
    UnityEditorAssetPath,
    ExternalObjFile
}

public class MeshPathJsonLoader : MonoBehaviour
{
    [Header("Path Source")]
    [SerializeField] private PathSource pathSource = PathSource.JsonFile;

    [Header("JSON Config")]
    [SerializeField] private string jsonConfigPath = "project_paths.json";
    [SerializeField] private string meshPathJsonKey = "mesh_path";

    [Header("Manual Override")]
    [SerializeField] private string manualMeshPath = @"\\10.100.55.11\unity\building_mesh.obj";

    [Header("Target Components")]
    [SerializeField] private GameObject targetObject;
    [SerializeField] private bool ensureMeshFilter = true;
    [SerializeField] private bool ensureMeshRenderer = true;
    [SerializeField] private bool ensureMeshCollider = true;

    [Header("Renderer")]
    [SerializeField] private Material rendererMaterial;

    [Header("Collider")]
    [SerializeField] private bool convexCollider = false;
    [SerializeField] private bool clearColliderBeforeAssign = true;

    [Header("Loading")]
    [SerializeField] private MeshLoadMode meshLoadMode = MeshLoadMode.Auto;
    [SerializeField] private bool loadOnStart = false;
    [SerializeField] private bool loadOnEnable = false;
    [SerializeField] private bool requireFileExists = true;
    [SerializeField] private bool preserveBackslashes = true;

    [Header("Debug")]
    [SerializeField] private bool logDetails = true;
    [SerializeField, HideInInspector] private string lastResolvedJsonPath;
    [SerializeField, HideInInspector] private string lastResolvedMeshPath;
    [SerializeField, HideInInspector] private string lastStatus;

    private void OnEnable()
    {
        if (loadOnEnable)
            LoadAndApplyMesh();
    }

    private void Start()
    {
        if (loadOnStart)
            LoadAndApplyMesh();
    }

    [ContextMenu("Validate Paths")]
    public void ValidatePaths()
    {
        string meshPath;
        if (!TryResolveMeshPath(out meshPath))
            return;

        if (!ValidateTarget())
            return;

        if (!ValidateMeshPath(meshPath))
            return;

        lastStatus =
            "Paths validated successfully.\n" +
            "Resolved JSON Path: " + DisplayPath(lastResolvedJsonPath) + "\n" +
            "Resolved Mesh Path: " + DisplayPath(lastResolvedMeshPath);

        if (logDetails)
            Debug.Log(lastStatus, this);
    }

    [ContextMenu("Load And Apply Mesh")]
    public void LoadAndApplyMesh()
    {
        string meshPath;
        if (!TryResolveMeshPath(out meshPath))
            return;

        if (!ValidateTarget())
            return;

        Mesh mesh;
        if (!TryLoadMesh(meshPath, out mesh))
            return;

        if (!ApplyMesh(mesh))
            return;

        GameObject target = targetObject != null ? targetObject : gameObject;
        lastStatus =
            "Mesh loaded and applied successfully.\n" +
            "Target: " + target.name + "\n" +
            "Mesh: " + mesh.name + "\n" +
            "Resolved JSON Path: " + DisplayPath(lastResolvedJsonPath) + "\n" +
            "Resolved Mesh Path: " + DisplayPath(lastResolvedMeshPath) + "\n" +
            "Vertices: " + mesh.vertexCount + "\n" +
            "Triangles: " + (mesh.triangles.Length / 3);

        if (logDetails)
            Debug.Log(lastStatus, this);
    }

    [ContextMenu("Print Last Status")]
    public void PrintLastStatus()
    {
        string status = string.IsNullOrEmpty(lastStatus) ? "No status has been recorded yet." : lastStatus;
        Debug.Log(
            status + "\n" +
            "Resolved JSON Path: " + DisplayPath(lastResolvedJsonPath) + "\n" +
            "Resolved Mesh Path: " + DisplayPath(lastResolvedMeshPath),
            this);
    }

    private bool TryResolveMeshPath(out string meshPath)
    {
        meshPath = null;
        lastResolvedJsonPath = string.Empty;
        lastResolvedMeshPath = string.Empty;

        if (pathSource == PathSource.ManualOverride)
        {
            if (string.IsNullOrWhiteSpace(manualMeshPath))
                return Fail("The manual mesh path is missing or empty.");

            meshPath = NormalizeMeshPath(manualMeshPath.Trim());
            lastResolvedMeshPath = meshPath;
            return true;
        }

        if (string.IsNullOrWhiteSpace(jsonConfigPath))
            return Fail("The JSON config path is missing or empty.");

        try
        {
            lastResolvedJsonPath = Path.Combine(Application.streamingAssetsPath, jsonConfigPath);
        }
        catch (Exception exception)
        {
            return Fail("The JSON config path is invalid: " + exception.Message);
        }

        if (!File.Exists(lastResolvedJsonPath))
            return Fail("JSON file not found: " + lastResolvedJsonPath);

        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(lastResolvedJsonPath));
        }
        catch (JsonException exception)
        {
            return Fail("Invalid JSON in config file '" + lastResolvedJsonPath + "': " + exception.Message);
        }
        catch (Exception exception)
        {
            return Fail("Could not read JSON config file '" + lastResolvedJsonPath + "': " + exception.Message);
        }

        if (string.IsNullOrWhiteSpace(meshPathJsonKey))
            return Fail("The configured mesh path JSON key is missing or empty.");

        JToken meshPathToken;
        try
        {
            meshPathToken = root.SelectToken(meshPathJsonKey, false);
        }
        catch (JsonException exception)
        {
            return Fail("The configured JSON key expression is invalid: " + exception.Message);
        }

        if (meshPathToken == null)
            return Fail("Configured key '" + meshPathJsonKey + "' is missing from JSON config: " + lastResolvedJsonPath);

        if (meshPathToken.Type != JTokenType.String)
            return Fail("Configured key '" + meshPathJsonKey + "' must contain a mesh path string.");

        string configuredMeshPath = meshPathToken.Value<string>();
        if (string.IsNullOrWhiteSpace(configuredMeshPath))
            return Fail("Configured key '" + meshPathJsonKey + "' is empty in JSON config: " + lastResolvedJsonPath);

        meshPath = NormalizeMeshPath(configuredMeshPath.Trim());
        lastResolvedMeshPath = meshPath;
        return true;
    }

    private string NormalizeMeshPath(string path)
    {
        return preserveBackslashes ? path : path.Replace('\\', '/');
    }

    private bool ValidateTarget()
    {
        GameObject target = targetObject != null ? targetObject : gameObject;
        if (target == null)
            return Fail("Target object is invalid.");

        return true;
    }

    private bool ValidateMeshPath(string meshPath)
    {
        bool useAssetDatabase;
        if (!TryDetermineLoadMethod(meshPath, out useAssetDatabase))
            return false;

        if (useAssetDatabase)
        {
#if UNITY_EDITOR
            if (requireFileExists && AssetDatabase.LoadMainAssetAtPath(meshPath) == null)
                return Fail("Mesh asset not found at Unity asset path: " + meshPath);

            return true;
#else
            return Fail("UnityEditorAssetPath loading works only inside the Unity Editor. Use an external .obj path for runtime loading.");
#endif
        }

        string extension;
        try
        {
            extension = Path.GetExtension(meshPath);
        }
        catch (Exception exception)
        {
            return Fail("The mesh path is invalid: " + exception.Message + " Path: " + meshPath);
        }
        if (!string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "External runtime loading currently supports .obj only. " +
                "For .fbx/.glb/.ply, import the asset into Unity first or use a dedicated runtime importer. " +
                "Path: " + meshPath);
        }

        if (requireFileExists && !File.Exists(meshPath))
            return Fail("Mesh file not found: " + meshPath);

        return true;
    }

    private bool TryDetermineLoadMethod(string meshPath, out bool useAssetDatabase)
    {
        useAssetDatabase = false;

        switch (meshLoadMode)
        {
            case MeshLoadMode.Auto:
                useAssetDatabase = meshPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                return true;

            case MeshLoadMode.UnityEditorAssetPath:
                if (!meshPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return Fail("Unity Editor asset paths must start with 'Assets/'. Path: " + meshPath);

                useAssetDatabase = true;
                return true;

            case MeshLoadMode.ExternalObjFile:
                useAssetDatabase = false;
                return true;

            default:
                return Fail("Unsupported mesh load mode: " + meshLoadMode);
        }
    }

    private bool TryLoadMesh(string meshPath, out Mesh mesh)
    {
        mesh = null;

        if (!ValidateMeshPath(meshPath))
            return false;

        bool useAssetDatabase;
        if (!TryDetermineLoadMethod(meshPath, out useAssetDatabase))
            return false;

        if (useAssetDatabase)
            return TryLoadUnityEditorMesh(meshPath, out mesh);

        return TryLoadExternalObj(meshPath, out mesh);
    }

    private bool TryLoadUnityEditorMesh(string assetPath, out Mesh mesh)
    {
        mesh = null;

#if UNITY_EDITOR
        mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (IsUsableAssetMesh(mesh))
            return true;

        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        for (int i = 0; i < assets.Length; i++)
        {
            Mesh candidate = assets[i] as Mesh;
            if (IsUsableAssetMesh(candidate))
            {
                mesh = candidate;
                return true;
            }
        }

        mesh = null;
        return Fail("No valid Mesh asset was found at Unity asset path: " + assetPath);
#else
        return Fail("UnityEditorAssetPath loading works only inside the Unity Editor. Use an external .obj path for runtime loading.");
#endif
    }

#if UNITY_EDITOR
    private static bool IsUsableAssetMesh(Mesh mesh)
    {
        if (mesh == null || !AssetDatabase.Contains(mesh))
            return false;

        if ((mesh.hideFlags & HideFlags.HideAndDontSave) != HideFlags.None)
            return false;

        return mesh.name.IndexOf("preview", StringComparison.OrdinalIgnoreCase) < 0;
    }
#endif

    private bool TryLoadExternalObj(string objPath, out Mesh mesh)
    {
        mesh = null;

        if (!File.Exists(objPath))
            return Fail("Mesh file not found: " + objPath);

        try
        {
            mesh = LoadObj(objPath);
            return true;
        }
        catch (Exception exception)
        {
            return Fail("OBJ parse error for '" + objPath + "': " + exception.Message);
        }
    }

    private static Mesh LoadObj(string objPath)
    {
        List<Vector3> sourceVertices = new List<Vector3>();
        List<Vector3> sourceNormals = new List<Vector3>();
        List<Vector2> sourceUvs = new List<Vector2>();

        List<Vector3> meshVertices = new List<Vector3>();
        List<Vector3> meshNormals = new List<Vector3>();
        List<Vector2> meshUvs = new List<Vector2>();
        List<int> triangles = new List<int>();
        Dictionary<ObjVertexKey, int> vertexMap = new Dictionary<ObjVertexKey, int>();

        bool hasAnyNormals = false;
        bool hasMissingNormals = false;
        bool hasAnyUvs = false;
        int lineNumber = 0;

        using (StreamReader reader = new StreamReader(objPath))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;

                int commentIndex = line.IndexOf('#');
                if (commentIndex >= 0)
                    line = line.Substring(0, commentIndex);

                line = line.Trim();
                if (line.Length == 0)
                    continue;

                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                switch (parts[0])
                {
                    case "v":
                        if (parts.Length < 4)
                            throw ObjError(lineNumber, "Vertex requires three coordinates.");

                        sourceVertices.Add(new Vector3(
                            ParseFloat(parts[1], lineNumber),
                            ParseFloat(parts[2], lineNumber),
                            ParseFloat(parts[3], lineNumber)));
                        break;

                    case "vn":
                        if (parts.Length < 4)
                            throw ObjError(lineNumber, "Normal requires three coordinates.");

                        sourceNormals.Add(new Vector3(
                            ParseFloat(parts[1], lineNumber),
                            ParseFloat(parts[2], lineNumber),
                            ParseFloat(parts[3], lineNumber)));
                        break;

                    case "vt":
                        if (parts.Length < 3)
                            throw ObjError(lineNumber, "Texture coordinate requires two values.");

                        sourceUvs.Add(new Vector2(
                            ParseFloat(parts[1], lineNumber),
                            ParseFloat(parts[2], lineNumber)));
                        break;

                    case "f":
                        if (parts.Length < 4)
                            throw ObjError(lineNumber, "Face requires at least three vertices.");

                        List<int> faceIndices = new List<int>(parts.Length - 1);
                        for (int i = 1; i < parts.Length; i++)
                        {
                            ObjVertexKey key = ParseFaceVertex(
                                parts[i],
                                sourceVertices.Count,
                                sourceUvs.Count,
                                sourceNormals.Count,
                                lineNumber);

                            int meshIndex;
                            if (!vertexMap.TryGetValue(key, out meshIndex))
                            {
                                meshIndex = meshVertices.Count;
                                vertexMap.Add(key, meshIndex);

                                meshVertices.Add(sourceVertices[key.VertexIndex]);

                                if (key.UvIndex >= 0)
                                {
                                    meshUvs.Add(sourceUvs[key.UvIndex]);
                                    hasAnyUvs = true;
                                }
                                else
                                {
                                    meshUvs.Add(Vector2.zero);
                                }

                                if (key.NormalIndex >= 0)
                                {
                                    meshNormals.Add(sourceNormals[key.NormalIndex]);
                                    hasAnyNormals = true;
                                }
                                else
                                {
                                    meshNormals.Add(Vector3.zero);
                                    hasMissingNormals = true;
                                }
                            }

                            faceIndices.Add(meshIndex);
                        }

                        for (int i = 1; i < faceIndices.Count - 1; i++)
                        {
                            triangles.Add(faceIndices[0]);
                            triangles.Add(faceIndices[i]);
                            triangles.Add(faceIndices[i + 1]);
                        }
                        break;
                }
            }
        }

        if (meshVertices.Count == 0)
            throw new FormatException("The OBJ contains no face vertices.");

        if (triangles.Count == 0)
            throw new FormatException("The OBJ contains no valid faces.");

        Mesh mesh = new Mesh();
        mesh.name = Path.GetFileNameWithoutExtension(objPath);

        if (meshVertices.Count > 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.SetVertices(meshVertices);
        if (hasAnyUvs)
            mesh.SetUVs(0, meshUvs);

        mesh.SetTriangles(triangles, 0, true);

        if (hasAnyNormals && !hasMissingNormals)
            mesh.SetNormals(meshNormals);
        else
            mesh.RecalculateNormals();

        mesh.RecalculateBounds();
        return mesh;
    }

    private bool ApplyMesh(Mesh mesh)
    {
        if (mesh == null)
            return Fail("Loaded mesh is invalid.");

        GameObject target = targetObject != null ? targetObject : gameObject;
        if (target == null)
            return Fail("Target object is invalid.");

        if (ensureMeshFilter)
        {
            MeshFilter meshFilter = target.GetComponent<MeshFilter>();
            if (meshFilter == null)
                meshFilter = target.AddComponent<MeshFilter>();

            meshFilter.sharedMesh = mesh;
        }

        if (ensureMeshRenderer)
        {
            MeshRenderer meshRenderer = target.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                meshRenderer = target.AddComponent<MeshRenderer>();

            if (rendererMaterial != null)
                meshRenderer.sharedMaterial = rendererMaterial;
        }

        if (ensureMeshCollider)
        {
            MeshCollider meshCollider = target.GetComponent<MeshCollider>();
            if (meshCollider == null)
                meshCollider = target.AddComponent<MeshCollider>();

            if (clearColliderBeforeAssign)
                meshCollider.sharedMesh = null;

            meshCollider.convex = convexCollider;
            meshCollider.sharedMesh = mesh;
        }

        return true;
    }

    private bool Fail(string message)
    {
        lastStatus = "Error: " + message;
        Debug.LogError(lastStatus, this);
        return false;
    }

    private static string DisplayPath(string path)
    {
        return string.IsNullOrEmpty(path) ? "(not used)" : path;
    }

    private static float ParseFloat(string value, int lineNumber)
    {
        float parsed;
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            throw ObjError(lineNumber, "Invalid floating-point value '" + value + "'.");

        return parsed;
    }

    private static ObjVertexKey ParseFaceVertex(
        string value,
        int vertexCount,
        int uvCount,
        int normalCount,
        int lineNumber)
    {
        string[] indices = value.Split('/');
        if (indices.Length < 1 || indices.Length > 3 || string.IsNullOrEmpty(indices[0]))
            throw ObjError(lineNumber, "Invalid face vertex '" + value + "'.");

        int vertexIndex = ResolveObjIndex(indices[0], vertexCount, "vertex", lineNumber);
        int uvIndex = -1;
        int normalIndex = -1;

        if (indices.Length > 1 && !string.IsNullOrEmpty(indices[1]))
            uvIndex = ResolveObjIndex(indices[1], uvCount, "UV", lineNumber);

        if (indices.Length > 2 && !string.IsNullOrEmpty(indices[2]))
            normalIndex = ResolveObjIndex(indices[2], normalCount, "normal", lineNumber);

        return new ObjVertexKey(vertexIndex, uvIndex, normalIndex);
    }

    private static int ResolveObjIndex(string value, int count, string indexKind, int lineNumber)
    {
        int parsed;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed == 0)
            throw ObjError(lineNumber, "Invalid " + indexKind + " index '" + value + "'.");

        int resolved = parsed > 0 ? parsed - 1 : count + parsed;
        if (resolved < 0 || resolved >= count)
        {
            throw ObjError(
                lineNumber,
                indexKind + " index '" + value + "' is outside the available range of " + count + ".");
        }

        return resolved;
    }

    private static FormatException ObjError(int lineNumber, string message)
    {
        return new FormatException("Line " + lineNumber + ": " + message);
    }

    private struct ObjVertexKey : IEquatable<ObjVertexKey>
    {
        public readonly int VertexIndex;
        public readonly int UvIndex;
        public readonly int NormalIndex;

        public ObjVertexKey(int vertexIndex, int uvIndex, int normalIndex)
        {
            VertexIndex = vertexIndex;
            UvIndex = uvIndex;
            NormalIndex = normalIndex;
        }

        public bool Equals(ObjVertexKey other)
        {
            return VertexIndex == other.VertexIndex &&
                   UvIndex == other.UvIndex &&
                   NormalIndex == other.NormalIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is ObjVertexKey && Equals((ObjVertexKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = VertexIndex;
                hash = (hash * 397) ^ UvIndex;
                hash = (hash * 397) ^ NormalIndex;
                return hash;
            }
        }
    }
}
