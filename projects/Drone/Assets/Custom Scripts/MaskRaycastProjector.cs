using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Describes one local semantic component whose binary mask belongs to one original video frame.
/// FrameIndex is zero-based, matching the frame_index convention used by the Python preprocessor.
/// </summary>
[Serializable]
public sealed class MaskProjectionRequest
{
    [Tooltip("PNG mask path. Relative paths use the projector's Mask Path Root.")]
    public string maskPath = string.Empty;

    [Min(0)] public int viewIndex;
    [Min(0)] public int frameIndex;
    public string localDetectionId = string.Empty;
    public string className = "building";
    [Range(0f, 1f)] public float confidence = 1f;
}

/// <summary>
/// Loads binary semantic masks, samples their eroded boundary and interior, configures the
/// authoritative SRT camera for each original frame, and retains the complete mesh raycast hits.
/// </summary>
public sealed class MaskRaycastProjector : MonoBehaviour
{
    [Header("Projection Engine")]
    [SerializeField] private SrtDroneRaycastPlayer player;

    [Header("Mask Inputs")]
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot maskPathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.ProjectRoot;
    [SerializeField] private List<MaskProjectionRequest> masks = new List<MaskProjectionRequest>();

    [Header("Source Video Dimensions")]
    [Tooltip("Use zero to infer the width from each mask. Set this when masks were resized.")]
    [Min(0)] [SerializeField] private int sourceFrameWidth;
    [Tooltip("Use zero to infer the height from each mask. Set this when masks were resized.")]
    [Min(0)] [SerializeField] private int sourceFrameHeight;

    [Header("Sparse Sampling")]
    [Range(0f, 1f)] [SerializeField] private float foregroundThreshold = 0.5f;
    [Range(0, 16)] [SerializeField] private int erosionRadiusPixels = 3;
    [Min(1)] [SerializeField] private int boundaryStridePixels = 8;
    [Min(1)] [SerializeField] private int interiorStridePixels = 16;

    [Header("Debug Visualization")]
    [SerializeField] private bool drawHitGizmos = true;
    [Min(1)] [SerializeField] private int maxGizmoHits = 2000;
    [Min(0.001f)] [SerializeField] private float hitGizmoRadius = 0.08f;
    [SerializeField] private Color hitGizmoColor = new Color(0f, 1f, 0.75f, 0.85f);

    [NonSerialized] private List<SurfaceHit> surfaceHits = new List<SurfaceHit>();
    private bool projectionInProgress;

    public IReadOnlyList<SurfaceHit> SurfaceHits => surfaceHits;

    [ContextMenu("Project Configured Masks To Surface")]
    public void ProjectConfiguredMasks()
    {
        if (!TryProjectConfiguredMasks(out string error))
            Debug.LogError($"[MaskRaycastProjector] Projection aborted: {error}", this);
    }

    public bool TryProjectConfiguredMasks(out string error)
    {
        error = string.Empty;
        if (projectionInProgress)
            return Fail("A projection is already running.", out error);
        if (!TryValidateConfiguration(out List<ResolvedMaskRequest> resolvedRequests, out error))
            return false;

        projectionInProgress = true;
        bool hasSavedProjectionState = false;
        SrtDroneRaycastPlayer.ProjectionState savedProjectionState = default;
#if UNITY_EDITOR
        SceneDirtinessState sceneDirtinessState = default;
#endif
        try
        {
            savedProjectionState = player.CaptureProjectionState();
            hasSavedProjectionState = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                sceneDirtinessState = SceneDirtinessState.Capture(player, savedProjectionState.Camera);
#endif

            if (!player.TryPrepareForGeoreferencing(out string preparationError))
                return Fail($"Projection preparation failed: {preparationError}", out error);

            var projectedHits = new List<SurfaceHit>();
            var colliderPaths = new Dictionary<int, string>();
            for (int i = 0; i < resolvedRequests.Count; i++)
            {
                if (!TryProjectMask(
                        resolvedRequests[i], projectedHits, colliderPaths, out error))
                    return false;
            }

            surfaceHits = projectedHits;
            Debug.Log(
                $"[MaskRaycastProjector] Retained {surfaceHits.Count} surface hit(s) " +
                $"from {resolvedRequests.Count} mask(s).",
                this);
            return true;
        }
        catch (Exception exception)
        {
            return Fail($"Unexpected projection error: {exception.Message}", out error);
        }
        finally
        {
            if (hasSavedProjectionState && player != null)
            {
                player.RestoreProjectionState(savedProjectionState);
                Physics.SyncTransforms();
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
                sceneDirtinessState.Restore();
#endif
            projectionInProgress = false;
        }
    }

    public List<SurfaceHit> GetHitsForDetection(string localDetectionId)
    {
        var matches = new List<SurfaceHit>();
        if (string.IsNullOrWhiteSpace(localDetectionId))
            return matches;

        for (int i = 0; i < surfaceHits.Count; i++)
        {
            if (string.Equals(
                    surfaceHits[i].LocalDetectionId, localDetectionId, StringComparison.Ordinal))
                matches.Add(surfaceHits[i]);
        }
        return matches;
    }

    public void ClearSurfaceHits()
    {
        surfaceHits.Clear();
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawHitGizmos || surfaceHits == null || surfaceHits.Count == 0)
            return;

        Gizmos.color = hitGizmoColor;
        int count = Math.Min(surfaceHits.Count, Math.Max(1, maxGizmoHits));
        int step = Math.Max(1, surfaceHits.Count / count);
        int drawn = 0;
        for (int i = 0; i < surfaceHits.Count && drawn < count; i += step)
        {
            Gizmos.DrawSphere(surfaceHits[i].WorldPoint, Math.Max(0.001f, hitGizmoRadius));
            drawn++;
        }
    }

    private bool TryValidateConfiguration(
        out List<ResolvedMaskRequest> resolvedRequests, out string error)
    {
        resolvedRequests = new List<ResolvedMaskRequest>();
        error = string.Empty;

        if (player == null)
            return Fail("SrtDroneRaycastPlayer reference is missing.", out error);
        if (masks == null || masks.Count == 0)
            return Fail("At least one mask request is required.", out error);
        if (sourceFrameWidth < 0 || sourceFrameHeight < 0 ||
            (sourceFrameWidth == 1 || sourceFrameHeight == 1))
            return Fail("Source frame dimensions must be zero (infer) or greater than one.", out error);
        if ((sourceFrameWidth == 0) != (sourceFrameHeight == 0))
            return Fail("Set both source frame dimensions, or leave both at zero.", out error);
        if (boundaryStridePixels < 1 || interiorStridePixels < 1)
            return Fail("Boundary and interior strides must be at least one pixel.", out error);
        if (erosionRadiusPixels < 0)
            return Fail("Erosion radius cannot be negative.", out error);
        if (float.IsNaN(foregroundThreshold) || float.IsInfinity(foregroundThreshold) ||
            foregroundThreshold <= 0f || foregroundThreshold > 1f)
            return Fail("Foreground threshold must be greater than zero and at most one.", out error);

        var detectionIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < masks.Count; i++)
        {
            MaskProjectionRequest request = masks[i];
            if (request == null)
                return Fail($"Mask request {i} is null.", out error);
            if (request.viewIndex < 0 || request.frameIndex < 0)
                return Fail($"Mask request {i} has a negative view or frame index.", out error);
            if (string.IsNullOrWhiteSpace(request.localDetectionId))
                return Fail($"Mask request {i} has no local detection ID.", out error);
            if (!detectionIds.Add(request.localDetectionId))
                return Fail($"Local detection ID '{request.localDetectionId}' is duplicated.", out error);
            if (string.IsNullOrWhiteSpace(request.className))
                return Fail($"Mask request {i} has no class name.", out error);
            if (float.IsNaN(request.confidence) || float.IsInfinity(request.confidence))
                return Fail($"Mask request {i} has non-finite confidence.", out error);

            string resolvedPath;
            try
            {
                resolvedPath = ResolvePath(request.maskPath, maskPathRoot);
            }
            catch (Exception exception)
            {
                return Fail($"Mask request {i} has an invalid path: {exception.Message}", out error);
            }
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                return Fail($"Mask request {i} file does not exist: '{resolvedPath}'.", out error);

            resolvedRequests.Add(new ResolvedMaskRequest(request, resolvedPath));
        }
        return true;
    }

    private bool TryProjectMask(
        ResolvedMaskRequest resolvedRequest,
        List<SurfaceHit> output,
        Dictionary<int, string> colliderPaths,
        out string error)
    {
        error = string.Empty;
        MaskProjectionRequest request = resolvedRequest.request;
        Texture2D maskTexture = null;
        try
        {
            maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            byte[] bytes = File.ReadAllBytes(resolvedRequest.path);
            if (!ImageConversion.LoadImage(maskTexture, bytes, false))
                return Fail($"Could not decode PNG mask '{resolvedRequest.path}'.", out error);

            int projectionWidth = sourceFrameWidth > 1 ? sourceFrameWidth : maskTexture.width;
            int projectionHeight = sourceFrameHeight > 1 ? sourceFrameHeight : maskTexture.height;
            if (!HaveMatchingAspectRatios(
                    maskTexture.width, maskTexture.height, projectionWidth, projectionHeight))
            {
                return Fail(
                    $"Mask '{resolvedRequest.path}' is {maskTexture.width}x{maskTexture.height}, " +
                    $"but the source frame is {projectionWidth}x{projectionHeight}; aspect ratios differ.",
                    out error);
            }

            if (!player.TryConfigureSourceFrameDimensions(
                    projectionWidth, projectionHeight, out string dimensionError))
                return Fail($"Cannot configure source frame dimensions: {dimensionError}", out error);
            if (!player.TryConfigureForFrameCnt(request.frameIndex + 1, out string frameError))
                return Fail(
                    $"Cannot configure original frame_index {request.frameIndex}: {frameError}", out error);

            Camera camera = player.DroneViewCamera;
            if (camera == null)
                return Fail("Drone View Camera is missing after frame configuration.", out error);

            Color32[] pixels = maskTexture.GetPixels32();
            bool[] foreground = BuildForegroundMask(pixels, foregroundThreshold);
            int foregroundCount = CountTrue(foreground);
            if (foregroundCount == 0)
                return Fail(
                    $"Mask '{resolvedRequest.path}' contains no foreground pixels at threshold " +
                    $"{foregroundThreshold:0.###}.",
                    out error);
            bool[] eroded = ErodeMask(
                foreground, maskTexture.width, maskTexture.height, erosionRadiusPixels);
            if (erosionRadiusPixels > 0 && CountTrue(eroded) == 0)
            {
                Debug.LogWarning(
                    $"[MaskRaycastProjector] {request.localDetectionId}: erosion removed the entire " +
                    "component; sampling the original mask instead.",
                    this);
                eroded = foreground;
            }
            List<int> sampleIndices = CollectSampleIndices(
                eroded,
                maskTexture.width,
                maskTexture.height,
                boundaryStridePixels,
                interiorStridePixels);

            int initialHitCount = output.Count;
            for (int i = 0; i < sampleIndices.Count; i++)
            {
                int index = sampleIndices[i];
                int textureX = index % maskTexture.width;
                int textureY = index / maskTexture.width;
                // GetPixels32 and viewport coordinates both start at the bottom-left, so the
                // sampled texture Y maps directly to viewport Y without an image-space flip.
                float viewportX = (textureX + 0.5f) / maskTexture.width;
                float viewportY = (textureY + 0.5f) / maskTexture.height;
                Ray ray = camera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
                if (!player.TryRaycastMap(ray, out RaycastHit raycastHit))
                    continue;

                int colliderInstanceId = raycastHit.collider != null
                    ? raycastHit.collider.GetInstanceID()
                    : 0;
                if (!colliderPaths.TryGetValue(colliderInstanceId, out string colliderPath))
                {
                    colliderPath = SurfaceHit.BuildColliderPath(raycastHit.collider);
                    colliderPaths.Add(colliderInstanceId, colliderPath);
                }

                output.Add(new SurfaceHit(
                    raycastHit,
                    ray,
                    request.viewIndex,
                    request.frameIndex,
                    request.localDetectionId,
                    request.className,
                    Mathf.Clamp01(request.confidence),
                    colliderPath));
            }

            int hitCount = output.Count - initialHitCount;
            Debug.Log(
                $"[MaskRaycastProjector] {request.localDetectionId}: " +
                $"{sampleIndices.Count} sample(s), {hitCount} mesh hit(s), " +
                $"{sampleIndices.Count - hitCount} miss(es).",
                this);
            return true;
        }
        catch (Exception exception)
        {
            return Fail($"Failed to project mask '{resolvedRequest.path}': {exception.Message}", out error);
        }
        finally
        {
            if (maskTexture != null)
            {
                if (Application.isPlaying)
                    Destroy(maskTexture);
                else
                    DestroyImmediate(maskTexture);
            }
        }
    }

    private static bool[] BuildForegroundMask(Color32[] pixels, float normalizedThreshold)
    {
        if (pixels == null)
            throw new ArgumentNullException(nameof(pixels));
        if (float.IsNaN(normalizedThreshold) || float.IsInfinity(normalizedThreshold) ||
            normalizedThreshold < 0f || normalizedThreshold > 1f)
            throw new ArgumentOutOfRangeException(
                nameof(normalizedThreshold), "Threshold must be between zero and one.");

        int threshold = Mathf.RoundToInt(Mathf.Clamp01(normalizedThreshold) * 255f);
        var foreground = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 pixel = pixels[i];
            int strongestChannel = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
            foreground[i] = pixel.a >= threshold && strongestChannel >= threshold;
        }
        return foreground;
    }

    private static int CountTrue(bool[] values)
    {
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                count++;
        }
        return count;
    }

    private static bool[] ErodeMask(bool[] foreground, int width, int height, int radius)
    {
        ValidateMaskDimensions(foreground, width, height);
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius cannot be negative.");
        if (radius <= 0)
            return (bool[])foreground.Clone();

        var eroded = new bool[foreground.Length];
        if (width <= radius * 2 || height <= radius * 2)
            return eroded;

        int integralWidth = width + 1;
        var integral = new int[integralWidth * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            int rowSum = 0;
            for (int x = 0; x < width; x++)
            {
                if (foreground[y * width + x])
                    rowSum++;
                integral[(y + 1) * integralWidth + x + 1] =
                    integral[y * integralWidth + x + 1] + rowSum;
            }
        }

        int windowSize = radius * 2 + 1;
        int requiredCount = windowSize * windowSize;
        for (int y = radius; y < height - radius; y++)
        {
            int bottom = y - radius;
            int topExclusive = y + radius + 1;
            for (int x = radius; x < width - radius; x++)
            {
                int left = x - radius;
                int rightExclusive = x + radius + 1;
                int count =
                    integral[topExclusive * integralWidth + rightExclusive] -
                    integral[bottom * integralWidth + rightExclusive] -
                    integral[topExclusive * integralWidth + left] +
                    integral[bottom * integralWidth + left];
                eroded[y * width + x] = count == requiredCount;
            }
        }
        return eroded;
    }

    private static List<int> CollectSampleIndices(
        bool[] mask, int width, int height, int boundaryStride, int interiorStride)
    {
        ValidateMaskDimensions(mask, width, height);
        if (boundaryStride < 1)
            throw new ArgumentOutOfRangeException(
                nameof(boundaryStride), "Boundary stride must be at least one.");
        if (interiorStride < 1)
            throw new ArgumentOutOfRangeException(
                nameof(interiorStride), "Interior stride must be at least one.");

        var boundary = new bool[mask.Length];
        int cellColumns = (width + boundaryStride - 1) / boundaryStride;
        int cellRows = (height + boundaryStride - 1) / boundaryStride;
        var boundaryCellSamples = new int[cellColumns * cellRows];
        for (int i = 0; i < boundaryCellSamples.Length; i++)
            boundaryCellSamples[i] = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (!mask[index] || !IsBoundaryPixel(mask, width, height, x, y))
                    continue;

                boundary[index] = true;
                int cell = (y / boundaryStride) * cellColumns + x / boundaryStride;
                if (boundaryCellSamples[cell] < 0)
                    boundaryCellSamples[cell] = index;
            }
        }

        var samples = new List<int>();
        for (int i = 0; i < boundaryCellSamples.Length; i++)
        {
            if (boundaryCellSamples[i] >= 0)
                samples.Add(boundaryCellSamples[i]);
        }

        int interiorOffset = interiorStride / 2;
        for (int y = interiorOffset; y < height; y += interiorStride)
        {
            for (int x = interiorOffset; x < width; x += interiorStride)
            {
                int index = y * width + x;
                if (mask[index] && !boundary[index])
                    samples.Add(index);
            }
        }
        return samples;
    }

    private static bool IsBoundaryPixel(bool[] mask, int width, int height, int x, int y)
    {
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0)
                    continue;
                int neighborX = x + offsetX;
                int neighborY = y + offsetY;
                if (neighborX < 0 || neighborX >= width ||
                    neighborY < 0 || neighborY >= height ||
                    !mask[neighborY * width + neighborX])
                    return true;
            }
        }
        return false;
    }

    private static void ValidateMaskDimensions(bool[] mask, int width, int height)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        if (width < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height < 1)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        if ((long)width * height != mask.Length)
            throw new ArgumentException(
                "Mask length must equal width multiplied by height.", nameof(mask));
    }

    private static bool HaveMatchingAspectRatios(
        int leftWidth, int leftHeight, int rightWidth, int rightHeight)
    {
        double left = (double)leftWidth / leftHeight;
        double right = (double)rightWidth / rightHeight;
        return Math.Abs(left - right) <= 0.001;
    }

    private static string ResolvePath(
        string rawPath, SrtDroneRaycastPlayer.FilePathRoot rootMode)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;
        if (Path.IsPathRooted(rawPath))
            return Path.GetFullPath(rawPath);
        return Path.GetFullPath(Path.Combine(
            SrtDroneRaycastPlayer.GetPathRoot(rootMode),
            rawPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private readonly struct ResolvedMaskRequest
    {
        public readonly MaskProjectionRequest request;
        public readonly string path;

        public ResolvedMaskRequest(MaskProjectionRequest request, string path)
        {
            this.request = request;
            this.path = path;
        }
    }

#if UNITY_EDITOR
    private readonly struct SceneDirtinessState
    {
        private readonly UnityEngine.SceneManagement.Scene playerScene;
        private readonly UnityEngine.SceneManagement.Scene cameraScene;
        private readonly bool playerSceneWasDirty;
        private readonly bool cameraSceneWasDirty;
        private readonly bool hasPlayerScene;
        private readonly bool hasSeparateCameraScene;

        private SceneDirtinessState(
            UnityEngine.SceneManagement.Scene playerScene,
            UnityEngine.SceneManagement.Scene cameraScene,
            bool playerSceneWasDirty,
            bool cameraSceneWasDirty,
            bool hasPlayerScene,
            bool hasSeparateCameraScene)
        {
            this.playerScene = playerScene;
            this.cameraScene = cameraScene;
            this.playerSceneWasDirty = playerSceneWasDirty;
            this.cameraSceneWasDirty = cameraSceneWasDirty;
            this.hasPlayerScene = hasPlayerScene;
            this.hasSeparateCameraScene = hasSeparateCameraScene;
        }

        public static SceneDirtinessState Capture(SrtDroneRaycastPlayer player, Camera camera)
        {
            UnityEngine.SceneManagement.Scene playerScene = player.gameObject.scene;
            bool hasPlayerScene = playerScene.IsValid();
            UnityEngine.SceneManagement.Scene cameraScene = camera != null
                ? camera.gameObject.scene
                : default;
            bool hasSeparateCameraScene = cameraScene.IsValid() &&
                                          (!hasPlayerScene || cameraScene.handle != playerScene.handle);
            return new SceneDirtinessState(
                playerScene,
                cameraScene,
                hasPlayerScene && playerScene.isDirty,
                hasSeparateCameraScene && cameraScene.isDirty,
                hasPlayerScene,
                hasSeparateCameraScene);
        }

        public void Restore()
        {
            if (hasPlayerScene && !playerSceneWasDirty && playerScene.isDirty)
                ClearSceneDirtiness(playerScene);
            if (hasSeparateCameraScene && !cameraSceneWasDirty && cameraScene.isDirty)
                ClearSceneDirtiness(cameraScene);
        }

        private static void ClearSceneDirtiness(UnityEngine.SceneManagement.Scene scene)
        {
            try
            {
                System.Reflection.MethodInfo method =
                    typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(
                        "ClearSceneDirtiness",
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                    method.Invoke(null, new object[] { scene });
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[MaskRaycastProjector] Could not restore scene dirtiness state: {exception.Message}");
            }
        }
    }
#endif
}
