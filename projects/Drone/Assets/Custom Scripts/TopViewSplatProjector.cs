using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GaussianSplatting.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Top-view pipeline:
/// first-frame centre ray -> geodetic top camera -> Gaussian-splat PNG -> top-view masks ->
/// one WGS84 polygon per connected mask component.
/// </summary>
public sealed class TopViewSplatProjector : MonoBehaviour
{
    private const string Wgs84CoordinateSpace = "WGS84 [longitude, latitude]";

    [Header("1. References")]
    [SerializeField] private SrtDroneRaycastPlayer player;
    [Tooltip("Aligns the top of the captured image with the splat's local Forward axis.")]
    [SerializeField] private GaussianSplatRenderer gaussianSplatRenderer;

    [Header("2. Top-view camera")]
    [Min(1)]
    [Tooltip("SRT FrameCnt used to cast the centre ray. FrameCnt starts at 1.")]
    [SerializeField] private int referenceFrameCount = 1;
    [Tooltip("Metres added to or subtracted from the reference drone-camera altitude. Zero preserves the SRT-derived altitude.")]
    [SerializeField] private float heightOffsetMeters;
    [Range(-180f, 180f)]
    [Tooltip("Rotation around geodetic Up after aligning the image with the splat's local Forward axis (or Unity world Forward when no splat renderer is assigned).")]
    [SerializeField] private float imageRotationDegrees;
    [Tooltip("Ordinary scene geometry is hidden by default; Gaussian splats are rendered by the URP feature independently of this mask.")]
    [SerializeField] private LayerMask captureSceneLayers = 0;
    [SerializeField] private Color captureBackground = Color.black;

    [Header("3. Top-view capture")]
    [Min(64)] [SerializeField] private int captureWidth = 2048;
    [SerializeField] private string capturePngPath = "Exports/top_view_splat.png";
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot capturePathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.ProjectRoot;

    [Header("4. Few-shot masks")]
    [Tooltip("A mask PNG/JPG or a directory containing masks produced from the captured top view. Absolute Windows paths are accepted.")]
    [SerializeField] private string maskPath = "Exports/top_view_masks";
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot maskPathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.ProjectRoot;
    [SerializeField] private string polygonClassName = "building";
    [Range(0f, 1f)] [SerializeField] private float foregroundThreshold = 0.5f;
    [SerializeField] private bool useAlphaAsForeground;
    [SerializeField] private bool invertForeground;
    [Min(1)] [SerializeField] private int minimumComponentPixels = 64;

    [Header("5. Polygon generation")]
    [Min(0f)]
    [Tooltip("Ramer-Douglas-Peucker tolerance in source-mask pixels, applied before raycasting.")]
    [SerializeField] private float contourSimplificationPixels = 2f;
    [Min(3)] [SerializeField] private int maximumContourVertices = 1000;
    [Range(0f, 2f)]
    [Tooltip("Moves contour samples slightly inside their connected component to avoid rays exactly on an image edge.")]
    [SerializeField] private float contourInsetPixels = 0.35f;
    [Range(0.05f, 1f)]
    [SerializeField] private float minimumSuccessfulRayFraction = 0.5f;
    [SerializeField] private string polygonGeoJsonPath = "Exports/top_view_polygons.geojson";
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot polygonOutputPathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.ProjectRoot;

    [NonSerialized] private Camera gameViewPreviewCamera;

    [ContextMenu("0. Show Or Refresh Game View Preview")]
    public void ShowOrRefreshGameViewPreview()
    {
        if (player == null)
        {
            Debug.LogError("[TopViewSplatProjector] SrtDroneRaycastPlayer reference is missing.", this);
            return;
        }

        SrtDroneRaycastPlayer.ProjectionState savedState = player.CaptureProjectionState();
        Camera candidateCamera = null;
#if UNITY_EDITOR
        SceneDirtinessState dirtiness = SceneDirtinessState.Capture(player, player.DroneViewCamera);
#endif
        try
        {
            if (!player.TryPrepareForGeoreferencing(out string preparationError))
            {
                Debug.LogError(
                    $"[TopViewSplatProjector] Preview preparation failed: {preparationError}", this);
                return;
            }
            if (!TryCreateTopViewCamera(
                    out candidateCamera, out TopViewContext context, out string cameraError))
            {
                Debug.LogError(
                    $"[TopViewSplatProjector] Preview setup failed: {cameraError}", this);
                return;
            }

            StopGameViewPreview();
            candidateCamera.gameObject.name = "TopViewGamePreviewCamera";
            candidateCamera.gameObject.hideFlags = HideFlags.DontSave;
            candidateCamera.targetTexture = null;
            candidateCamera.targetDisplay = 0;
            candidateCamera.rect = new Rect(0f, 0f, 1f, 1f);
            candidateCamera.depth = Mathf.Max(1000f, player.DroneViewCamera.depth + 100f);
            candidateCamera.enabled = true;
            gameViewPreviewCamera = candidateCamera;
            candidateCamera = null;

            Debug.Log(
                $"[TopViewSplatProjector] Game View preview is active at " +
                $"{context.ImageWidth}x{context.ImageHeight} ({context.ProjectionName}, " +
                $"image rotation {FormatSigned(context.ImageRotationDegrees)} degrees). " +
                "Choose the same aspect ratio in the Game View for an unstretched preview.",
                this);
#if UNITY_EDITOR
            FocusGameView();
#endif
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[TopViewSplatProjector] Preview aborted by an unexpected error: " +
                $"{exception.Message}",
                this);
        }
        finally
        {
            DestroyTemporaryCamera(candidateCamera);
            player.RestoreProjectionState(savedState);
            Physics.SyncTransforms();
#if UNITY_EDITOR
            dirtiness.Restore();
#endif
        }
    }

    [ContextMenu("0b. Stop Game View Preview")]
    public void StopGameViewPreview()
    {
        DestroyTemporaryCamera(gameViewPreviewCamera);
        gameViewPreviewCamera = null;
    }

    [ContextMenu("1. Capture Top View Splat PNG")]
    public void CaptureTopViewSplatPng()
    {
        RunTopViewOperation(true);
    }

    [ContextMenu("2. Project Top View Masks To Polygons")]
    public void ProjectTopViewMasksToPolygons()
    {
        RunTopViewOperation(false);
    }

    private void RunTopViewOperation(bool captureImage)
    {
        if (player == null)
        {
            Debug.LogError("[TopViewSplatProjector] SrtDroneRaycastPlayer reference is missing.", this);
            return;
        }

        SrtDroneRaycastPlayer.ProjectionState savedState = player.CaptureProjectionState();
        Camera temporaryCamera = null;
#if UNITY_EDITOR
        SceneDirtinessState dirtiness = SceneDirtinessState.Capture(player, player.DroneViewCamera);
#endif
        try
        {
            if (!player.TryPrepareForGeoreferencing(out string preparationError))
            {
                Debug.LogError($"[TopViewSplatProjector] Preparation failed: {preparationError}", this);
                return;
            }
            if (!TryCreateTopViewCamera(out temporaryCamera, out TopViewContext context, out string cameraError))
            {
                Debug.LogError($"[TopViewSplatProjector] Top-view setup failed: {cameraError}", this);
                return;
            }

            if (captureImage)
            {
                if (!TryCapturePng(temporaryCamera, context, out string path, out string captureError))
                {
                    Debug.LogError($"[TopViewSplatProjector] Capture failed: {captureError}", this);
                    return;
                }
                Debug.Log(
                    $"[TopViewSplatProjector] Captured {context.ImageWidth}x{context.ImageHeight} " +
                    $"{context.ProjectionName} top view to '{path}'. Centre is the FrameCnt " +
                    $"{referenceFrameCount} central-ray hit; estimated centre-plane coverage is " +
                    $"{Format(context.CoverageWidthMeters)} x {Format(context.CoverageHeightMeters)} m. " +
                    $"Height uses the SRT reference plus offset; final height is " +
                    $"{Format(context.FinalHeightWorldUnits)} Unity units " +
                    $"({Format(context.FinalHeightMeters)} geodetic m) above the centre hit.",
                    this);
            }
            else
            {
                if (!TryProjectMasks(temporaryCamera, context, out string path, out PolygonSummary summary, out string polygonError))
                {
                    Debug.LogError($"[TopViewSplatProjector] Polygon export failed: {polygonError}", this);
                    return;
                }
                Debug.Log(
                    $"[TopViewSplatProjector] Exported {summary.ExportedPolygons} polygon(s) from " +
                    $"{summary.AcceptedComponents}/{summary.DiscoveredComponents} connected component(s) in " +
                    $"{summary.MaskFiles} top-view mask(s) to '{path}'. " +
                    $"Raycasts succeeded for {summary.SuccessfulRays}/{summary.AttemptedRays} contour samples.",
                    this);
            }
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[TopViewSplatProjector] Operation aborted by an unexpected error: {exception.Message}",
                this);
        }
        finally
        {
            DestroyTemporaryCamera(temporaryCamera);
            player.RestoreProjectionState(savedState);
            Physics.SyncTransforms();
#if UNITY_EDITOR
            dirtiness.Restore();
#endif
        }
    }

    private bool TryCreateTopViewCamera(
        out Camera topCamera, out TopViewContext context, out string error)
    {
        topCamera = null;
        context = default;
        error = string.Empty;
        if (referenceFrameCount < 1)
            return Fail("Reference Frame Count must be at least 1.", out error);
        if (!IsFinite(heightOffsetMeters))
            return Fail("Height Offset Metres must be finite.", out error);
        if (!IsFinite(imageRotationDegrees))
            return Fail("Image Rotation Degrees must be finite.", out error);
        if (captureWidth < 64)
            return Fail("Capture Width must be at least 64 pixels.", out error);
        if (!player.TryConfigureForFrameCnt(referenceFrameCount, out string configureError))
            return Fail($"Cannot configure FrameCnt {referenceFrameCount}: {configureError}", out error);

        Camera sourceCamera = player.DroneViewCamera;
        if (sourceCamera == null)
            return Fail("The SRT player has no Drone View Camera.", out error);
        Ray centreRay = sourceCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (!player.TryRaycastMap(centreRay, out RaycastHit centreHit))
            return Fail(
                $"The central pixel of FrameCnt {referenceFrameCount} did not hit the configured map collider.",
                out error);
        if (!player.TryGetWorldEnuAxes(out Vector3 worldEast, out Vector3 worldNorth, out Vector3 worldUp) ||
            !player.TryConvertEnuOffsetToWorldVector(Vector3.right, out Vector3 eastMetre) ||
            !player.TryConvertEnuOffsetToWorldVector(Vector3.up, out Vector3 northMetre) ||
            !player.TryConvertEnuOffsetToWorldVector(Vector3.forward, out Vector3 upMetre))
            return Fail("The SRT player could not derive world-space ENU axes.", out error);

        float upWorldUnitsPerMetre = upMetre.magnitude;
        if (!IsFinite(upWorldUnitsPerMetre) || upWorldUnitsPerMetre <= 0.000001f)
            return Fail("The transform JSON produces an invalid vertical metres-to-world scale.", out error);
        float referenceHeightWorld = Vector3.Dot(
            sourceCamera.transform.position - centreHit.point, worldUp);
        float referenceHeightMeters = referenceHeightWorld / upWorldUnitsPerMetre;
        if (!IsFinite(referenceHeightMeters))
            return Fail("The SRT reference height above the centre hit is invalid.", out error);

        float finalHeightMeters = referenceHeightMeters + heightOffsetMeters;
        float finalHeightWorldUnits = finalHeightMeters * upWorldUnitsPerMetre;
        if (!IsFinite(finalHeightMeters) || !IsFinite(finalHeightWorldUnits) ||
            finalHeightMeters <= 0.1f || finalHeightWorldUnits <= 0.0001f)
            return Fail("The SRT reference height plus offset does not place the top camera above the centre hit.", out error);

        int imageWidth = Mathf.Max(64, captureWidth);
        float srtAspect = sourceCamera.aspect;
        if (!IsFinite(srtAspect) || srtAspect <= 0f)
            return Fail("The SRT-configured Drone View Camera has an invalid aspect ratio.", out error);
        int imageHeight = Mathf.Max(64, Mathf.RoundToInt(imageWidth / srtAspect));
        float outputAspect = (float)imageWidth / imageHeight;
        Vector3 baseImageUp = Vector3.ProjectOnPlane(
            gaussianSplatRenderer != null
                ? gaussianSplatRenderer.transform.forward
                : Vector3.forward,
            worldUp);
        string imageAlignment = gaussianSplatRenderer != null
            ? "gaussian_splat_local_forward"
            : "unity_world_forward";
        if (baseImageUp.sqrMagnitude <= 0.000001f)
        {
            baseImageUp = Vector3.ProjectOnPlane(Vector3.right, worldUp);
            imageAlignment += "_right_fallback";
        }
        if (baseImageUp.sqrMagnitude <= 0.000001f)
            return Fail("Could not derive a horizontal image-alignment axis.", out error);
        baseImageUp.Normalize();
        Vector3 imageUp = Quaternion.AngleAxis(imageRotationDegrees, worldUp) * baseImageUp;
        Quaternion rotation = Quaternion.LookRotation(-worldUp, imageUp);
        Vector3 cameraPosition = centreHit.point + worldUp * finalHeightWorldUnits;
        GameObject cameraObject = new GameObject("TopViewSplatCamera")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        topCamera = cameraObject.AddComponent<Camera>();
        topCamera.CopyFrom(sourceCamera);
        topCamera.enabled = false;
        topCamera.transform.SetPositionAndRotation(cameraPosition, rotation);
        topCamera.aspect = outputAspect;
        topCamera.clearFlags = CameraClearFlags.SolidColor;
        topCamera.backgroundColor = captureBackground;
        topCamera.cullingMask = captureSceneLayers;
        topCamera.nearClipPlane = 0.01f;
        float centreDistanceWorld = Vector3.Distance(cameraPosition, centreHit.point);
        float effectiveWidthWorld;
        float effectiveHeightWorld;
        topCamera.orthographic = false;
        float halfHeightWorld = centreDistanceWorld *
                                Mathf.Tan(topCamera.fieldOfView * Mathf.Deg2Rad * 0.5f);
        effectiveHeightWorld = halfHeightWorld * 2f;
        effectiveWidthWorld = effectiveHeightWorld * outputAspect;

        topCamera.farClipPlane = Mathf.Max(
            sourceCamera.farClipPlane,
            centreDistanceWorld * 4f);
        context = new TopViewContext(
            centreHit.point,
            cameraPosition,
            rotation,
            "srt_perspective",
            imageAlignment,
            imageUp,
            imageWidth,
            imageHeight,
            effectiveWidthWorld / Mathf.Max(eastMetre.magnitude, 0.000001f),
            effectiveHeightWorld / Mathf.Max(northMetre.magnitude, 0.000001f),
            referenceHeightMeters,
            finalHeightMeters,
            heightOffsetMeters,
            finalHeightWorldUnits,
            imageRotationDegrees,
            topCamera.fieldOfView,
            topCamera.focalLength,
            topCamera.sensorSize,
            topCamera.usePhysicalProperties,
            worldEast,
            worldNorth,
            worldUp);
        return true;
    }

    private bool TryCapturePng(
        Camera camera, TopViewContext context, out string outputPath, out string error)
    {
        outputPath = string.Empty;
        error = string.Empty;
        try
        {
            outputPath = ResolvePath(capturePngPath, capturePathRoot);
        }
        catch (Exception exception)
        {
            return Fail($"Invalid capture path: {exception.Message}", out error);
        }
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Capture PNG path is empty.", out error);
        if (Directory.Exists(outputPath))
        {
            return Fail(
                $"Capture PNG Path points to a directory. Include a filename, for example " +
                $"'{Path.Combine(outputPath, "top_view_splat.png")}'.",
                out error);
        }
        if (!string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
            return Fail("Capture PNG Path must end in .png.", out error);

        RenderTexture renderTexture = null;
        Texture2D image = null;
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        try
        {
            renderTexture = new RenderTexture(
                context.ImageWidth, context.ImageHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "TopViewSplatCapture",
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 1
            };
            renderTexture.Create();
            camera.targetTexture = renderTexture;
            camera.Render();
            RenderTexture.active = renderTexture;
            image = new Texture2D(
                context.ImageWidth, context.ImageHeight, TextureFormat.RGB24, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            image.ReadPixels(
                new Rect(0, 0, context.ImageWidth, context.ImageHeight), 0, 0, false);
            image.Apply(false, false);
            byte[] png = image.EncodeToPNG();
            if (png == null || png.Length == 0)
                return Fail("Unity returned an empty PNG.", out error);
            if (!TryWriteBytesAtomically(outputPath, png, out error))
                return false;

            string manifestPath = outputPath + ".json";
            JObject manifest = BuildCaptureManifest(context, outputPath);
            if (!TryWriteTextAtomically(
                    manifestPath,
                    JsonConvert.SerializeObject(manifest, Formatting.Indented),
                    out error))
                return false;
            return true;
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            DestroyObject(image);
            if (renderTexture != null)
            {
                renderTexture.Release();
                DestroyObject(renderTexture);
            }
        }
    }

    private JObject BuildCaptureManifest(TopViewContext context, string imagePath)
    {
        player.TryConvertWorldToWgs84(
            context.CentreWorld, out double centreLongitude, out double centreLatitude, out double centreAltitude);
        return new JObject
        {
            ["type"] = "triffid_top_view_capture",
            ["image_path"] = imagePath,
            ["reference_frame_cnt"] = referenceFrameCount,
            ["width_px"] = context.ImageWidth,
            ["height_px"] = context.ImageHeight,
            ["projection"] = context.ProjectionName,
            ["height_mode"] = "srt_reference_plus_offset",
            ["image_alignment"] = context.ImageAlignment,
            ["estimated_centre_plane_coverage_width_m"] = context.CoverageWidthMeters,
            ["estimated_centre_plane_coverage_height_m"] = context.CoverageHeightMeters,
            ["reference_drone_height_above_centre_m"] = context.ReferenceHeightMeters,
            ["height_offset_m"] = context.HeightOffsetMeters,
            ["final_camera_height_above_centre_m"] = context.FinalHeightMeters,
            ["final_camera_height_above_centre_unity_units"] = context.FinalHeightWorldUnits,
            ["image_rotation_degrees_from_alignment"] = context.ImageRotationDegrees,
            ["vertical_fov_degrees"] = context.VerticalFieldOfViewDegrees,
            ["focal_length_mm"] = context.FocalLengthMillimetres,
            ["sensor_size_mm"] = new JArray(context.SensorSizeMillimetres.x, context.SensorSizeMillimetres.y),
            ["uses_physical_camera"] = context.UsesPhysicalCamera,
            ["centre_wgs84"] = new JArray(centreLongitude, centreLatitude, centreAltitude),
            ["centre_world"] = VectorToJson(context.CentreWorld),
            ["camera_world"] = VectorToJson(context.CameraWorld),
            ["camera_rotation_xyzw"] = QuaternionToJson(context.CameraRotation),
            ["image_up_world"] = VectorToJson(context.ImageUpWorld),
            ["world_east"] = VectorToJson(context.WorldEast),
            ["world_north"] = VectorToJson(context.WorldNorth),
            ["world_up"] = VectorToJson(context.WorldUp)
        };
    }

    private bool TryProjectMasks(
        Camera camera,
        TopViewContext context,
        out string outputPath,
        out PolygonSummary summary,
        out string error)
    {
        outputPath = string.Empty;
        summary = default;
        error = string.Empty;
        string resolvedMaskPath;
        try
        {
            resolvedMaskPath = ResolvePath(maskPath, maskPathRoot);
            outputPath = ResolvePath(polygonGeoJsonPath, polygonOutputPathRoot);
        }
        catch (Exception exception)
        {
            return Fail($"Invalid mask or polygon output path: {exception.Message}", out error);
        }
        if (!TryDiscoverMaskFiles(resolvedMaskPath, out List<string> files, out error))
            return false;
        if (string.IsNullOrWhiteSpace(polygonClassName))
            return Fail("Polygon Class Name is empty.", out error);
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Polygon GeoJSON output path is empty.", out error);

        var features = new JArray();
        int discoveredComponents = 0;
        int acceptedComponents = 0;
        int attemptedRays = 0;
        int successfulRays = 0;
        int featureIndex = 0;
        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            if (!TryLoadForegroundMask(files[fileIndex], out ForegroundMask mask, out string maskError))
            {
                Debug.LogWarning(
                    $"[TopViewSplatProjector] Omitted mask '{files[fileIndex]}': {maskError}", this);
                continue;
            }
            if (mask.Width != context.ImageWidth || mask.Height != context.ImageHeight)
            {
                Debug.LogWarning(
                    $"[TopViewSplatProjector] Omitted mask '{files[fileIndex]}': dimensions " +
                    $"{mask.Width}x{mask.Height} do not match the top-view capture " +
                    $"{context.ImageWidth}x{context.ImageHeight}.", this);
                continue;
            }

            List<PixelComponent> components = FindConnectedComponents(mask);
            discoveredComponents += components.Count;
            for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                PixelComponent component = components[componentIndex];
                if (component.Pixels.Count < minimumComponentPixels)
                    continue;
                acceptedComponents++;
                if (!TryBuildPolygonFeature(
                        camera,
                        context,
                        mask,
                        component,
                        files[fileIndex],
                        componentIndex,
                        featureIndex,
                        out JObject feature,
                        out int componentAttemptedRays,
                        out int componentSuccessfulRays,
                        out string componentError))
                {
                    attemptedRays += componentAttemptedRays;
                    successfulRays += componentSuccessfulRays;
                    Debug.LogWarning(
                        $"[TopViewSplatProjector] Omitted component {componentIndex} from " +
                        $"'{Path.GetFileName(files[fileIndex])}': {componentError}", this);
                    continue;
                }
                attemptedRays += componentAttemptedRays;
                successfulRays += componentSuccessfulRays;
                features.Add(feature);
                featureIndex++;
            }
        }
        if (features.Count == 0)
            return Fail("No valid polygon could be projected from the discovered top-view masks.", out error);

        player.TryConvertWorldToWgs84(
            context.CentreWorld, out double centreLongitude, out double centreLatitude, out double centreAltitude);
        JObject root = new JObject
        {
            ["type"] = "FeatureCollection",
            ["generated_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["metadata"] = new JObject
            {
                ["coordinate_space"] = Wgs84CoordinateSpace,
                ["source"] = context.ProjectionName + "_top_view_mask_raycast",
                ["projection"] = context.ProjectionName,
                ["height_mode"] = "srt_reference_plus_offset",
                ["image_alignment"] = context.ImageAlignment,
                ["class"] = polygonClassName,
                ["reference_frame_cnt"] = referenceFrameCount,
                ["capture_width_px"] = context.ImageWidth,
                ["capture_height_px"] = context.ImageHeight,
                ["estimated_centre_plane_coverage_width_m"] = context.CoverageWidthMeters,
                ["estimated_centre_plane_coverage_height_m"] = context.CoverageHeightMeters,
                ["reference_drone_height_above_centre_m"] = context.ReferenceHeightMeters,
                ["height_offset_m"] = context.HeightOffsetMeters,
                ["final_camera_height_above_centre_m"] = context.FinalHeightMeters,
                ["final_camera_height_above_centre_unity_units"] = context.FinalHeightWorldUnits,
                ["image_rotation_degrees_from_alignment"] = context.ImageRotationDegrees,
                ["vertical_fov_degrees"] = context.VerticalFieldOfViewDegrees,
                ["focal_length_mm"] = context.FocalLengthMillimetres,
                ["centre_wgs84"] = new JArray(centreLongitude, centreLatitude, centreAltitude),
                ["mask_file_count"] = files.Count,
                ["connected_component_count"] = discoveredComponents,
                ["minimum_component_pixels"] = minimumComponentPixels,
                ["contour_simplification_pixels"] = contourSimplificationPixels,
                ["minimum_successful_ray_fraction"] = minimumSuccessfulRayFraction
            },
            ["features"] = features
        };
        string serialized = JsonConvert.SerializeObject(
            root,
            Formatting.Indented,
            new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture });
        if (!TryWriteTextAtomically(outputPath, serialized, out error))
            return false;
        summary = new PolygonSummary(
            files.Count,
            discoveredComponents,
            acceptedComponents,
            features.Count,
            attemptedRays,
            successfulRays);
        return true;
    }

    private bool TryBuildPolygonFeature(
        Camera camera,
        TopViewContext context,
        ForegroundMask mask,
        PixelComponent component,
        string maskFile,
        int componentIndex,
        int featureIndex,
        out JObject feature,
        out int attemptedRays,
        out int successfulRays,
        out string error)
    {
        feature = null;
        attemptedRays = 0;
        successfulRays = 0;
        error = string.Empty;
        List<PixelPoint> contour = TraceLargestOuterContour(component, mask.Width);
        if (contour.Count < 3)
            return Fail("outer contour contains fewer than three vertices.", out error);
        contour = RemoveCollinearVertices(contour);
        contour = SimplifyClosedRing(contour, Mathf.Max(0f, contourSimplificationPixels));
        contour = LimitRingVertices(contour, Mathf.Max(3, maximumContourVertices));
        if (contour.Count < 3)
            return Fail("simplification left fewer than three contour vertices.", out error);

        var projected = new List<GeoCoordinate>();
        double altitudeSum = 0.0;
        for (int i = 0; i < contour.Count; i++)
        {
            attemptedRays++;
            PixelPoint sample = InsetContourPoint(contour[i], component, contourInsetPixels);
            float viewportX = Mathf.Clamp01((float)(sample.X / mask.Width));
            float viewportY = Mathf.Clamp01((float)(sample.Y / mask.Height));
            Ray ray = camera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
            if (!player.TryRaycastMap(ray, out RaycastHit hit) ||
                !player.TryConvertWorldToWgs84(
                    hit.point, out double longitude, out double latitude, out double altitude))
                continue;
            var coordinate = new GeoCoordinate(longitude, latitude, altitude);
            if (projected.Count > 0 && projected[projected.Count - 1].SameHorizontalPosition(coordinate))
                continue;
            projected.Add(coordinate);
            altitudeSum += altitude;
            successfulRays++;
        }
        if (projected.Count > 1 && projected[0].SameHorizontalPosition(projected[projected.Count - 1]))
            projected.RemoveAt(projected.Count - 1);
        if (projected.Count < 3)
            return Fail("fewer than three distinct contour raycasts succeeded.", out error);
        float successFraction = attemptedRays > 0 ? (float)successfulRays / attemptedRays : 0f;
        if (successFraction < minimumSuccessfulRayFraction)
            return Fail(
                $"only {successfulRays}/{attemptedRays} contour raycasts succeeded " +
                $"({Format(successFraction)} < {Format(minimumSuccessfulRayFraction)}).",
                out error);
        if (SignedGeoArea(projected) < 0.0)
            projected.Reverse();

        var ring = new JArray();
        for (int i = 0; i < projected.Count; i++)
            ring.Add(new JArray(projected[i].Longitude, projected[i].Latitude));
        ring.Add(new JArray(projected[0].Longitude, projected[0].Latitude));
        var polygonCoordinates = new JArray();
        polygonCoordinates.Add(ring);
        feature = new JObject
        {
            ["type"] = "Feature",
            ["id"] = $"{SanitizeId(polygonClassName)}_top_{featureIndex:D3}",
            ["properties"] = new JObject
            {
                ["class"] = polygonClassName,
                ["source"] = context.ProjectionName + "_top_view_mask_raycast",
                ["projection"] = context.ProjectionName,
                ["mask_file"] = Path.GetFileName(maskFile),
                ["component_index"] = componentIndex,
                ["source_pixel_count"] = component.Pixels.Count,
                ["contour_vertex_count_before_projection"] = contour.Count,
                ["projected_vertex_count"] = projected.Count,
                ["successful_ray_fraction"] = successFraction,
                ["mean_surface_altitude_m"] = altitudeSum / successfulRays,
                ["reference_frame_cnt"] = referenceFrameCount
            },
            ["geometry"] = new JObject
            {
                ["type"] = "Polygon",
                ["coordinates"] = polygonCoordinates
            }
        };
        return true;
    }

    private bool TryLoadForegroundMask(string path, out ForegroundMask mask, out string error)
    {
        mask = default;
        error = string.Empty;
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!texture.LoadImage(File.ReadAllBytes(path), false))
                return Fail("Unity could not decode the image.", out error);
            Color32[] pixels = texture.GetPixels32();
            var foreground = new bool[pixels.Length];
            byte threshold = (byte)Mathf.RoundToInt(Mathf.Clamp01(foregroundThreshold) * 255f);
            for (int i = 0; i < pixels.Length; i++)
            {
                byte value = useAlphaAsForeground
                    ? pixels[i].a
                    : (byte)Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, pixels[i].b));
                foreground[i] = invertForeground ? value < threshold : value >= threshold;
            }
            mask = new ForegroundMask(texture.width, texture.height, foreground);
            return true;
        }
        catch (Exception exception)
        {
            return Fail(exception.Message, out error);
        }
        finally
        {
            DestroyObject(texture);
        }
    }

    private List<PixelComponent> FindConnectedComponents(ForegroundMask mask)
    {
        var components = new List<PixelComponent>();
        var visited = new bool[mask.Foreground.Length];
        var queue = new Queue<int>();
        for (int index = 0; index < mask.Foreground.Length; index++)
        {
            if (visited[index] || !mask.Foreground[index])
                continue;
            var pixels = new List<int>();
            double sumX = 0.0;
            double sumY = 0.0;
            visited[index] = true;
            queue.Enqueue(index);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                pixels.Add(current);
                int x = current % mask.Width;
                int y = current / mask.Width;
                sumX += x + 0.5;
                sumY += y + 0.5;
                EnqueueForeground(x - 1, y, mask, visited, queue);
                EnqueueForeground(x + 1, y, mask, visited, queue);
                EnqueueForeground(x, y - 1, mask, visited, queue);
                EnqueueForeground(x, y + 1, mask, visited, queue);
            }
            components.Add(new PixelComponent(
                pixels,
                sumX / pixels.Count,
                sumY / pixels.Count));
        }
        return components;
    }

    private static void EnqueueForeground(
        int x,
        int y,
        ForegroundMask mask,
        bool[] visited,
        Queue<int> queue)
    {
        if (x < 0 || x >= mask.Width || y < 0 || y >= mask.Height)
            return;
        int index = y * mask.Width + x;
        if (visited[index] || !mask.Foreground[index])
            return;
        visited[index] = true;
        queue.Enqueue(index);
    }

    private static List<PixelPoint> TraceLargestOuterContour(
        PixelComponent component, int maskWidth)
    {
        var occupied = new HashSet<int>(component.Pixels);
        var edges = new List<DirectedPixelEdge>();
        for (int i = 0; i < component.Pixels.Count; i++)
        {
            int index = component.Pixels[i];
            int x = index % maskWidth;
            int y = index / maskWidth;
            if (!occupied.Contains((y - 1) * maskWidth + x) || y == 0)
                edges.Add(new DirectedPixelEdge(x, y, x + 1, y));
            if (!occupied.Contains(y * maskWidth + x + 1) || x == maskWidth - 1)
                edges.Add(new DirectedPixelEdge(x + 1, y, x + 1, y + 1));
            if (!occupied.Contains((y + 1) * maskWidth + x))
                edges.Add(new DirectedPixelEdge(x + 1, y + 1, x, y + 1));
            if (!occupied.Contains(y * maskWidth + x - 1) || x == 0)
                edges.Add(new DirectedPixelEdge(x, y + 1, x, y));
        }

        var outgoing = new Dictionary<PixelPoint, List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!outgoing.TryGetValue(edges[i].Start, out List<int> edgeIndices))
            {
                edgeIndices = new List<int>();
                outgoing.Add(edges[i].Start, edgeIndices);
            }
            edgeIndices.Add(i);
        }

        var used = new bool[edges.Count];
        List<PixelPoint> largest = null;
        double largestArea = double.NegativeInfinity;
        for (int i = 0; i < edges.Count; i++)
        {
            if (used[i])
                continue;
            List<PixelPoint> ring = TraceRing(i, edges, outgoing, used);
            if (ring.Count < 3)
                continue;
            double area = SignedPixelArea(ring);
            if (area > largestArea)
            {
                largestArea = area;
                largest = ring;
            }
        }
        return largest ?? new List<PixelPoint>();
    }

    private static List<PixelPoint> TraceRing(
        int startEdgeIndex,
        List<DirectedPixelEdge> edges,
        Dictionary<PixelPoint, List<int>> outgoing,
        bool[] used)
    {
        var ring = new List<PixelPoint>();
        PixelPoint start = edges[startEdgeIndex].Start;
        int currentEdgeIndex = startEdgeIndex;
        ring.Add(start);
        for (int guard = 0; guard <= edges.Count; guard++)
        {
            if (used[currentEdgeIndex])
                return new List<PixelPoint>();
            DirectedPixelEdge current = edges[currentEdgeIndex];
            used[currentEdgeIndex] = true;
            if (current.End.Equals(start))
                return ring;
            ring.Add(current.End);
            if (!outgoing.TryGetValue(current.End, out List<int> candidates))
                return new List<PixelPoint>();
            int next = SelectNextEdge(current.Direction, candidates, edges, used);
            if (next < 0)
                return new List<PixelPoint>();
            currentEdgeIndex = next;
        }
        return new List<PixelPoint>();
    }

    private static int SelectNextEdge(
        int incomingDirection,
        List<int> candidates,
        List<DirectedPixelEdge> edges,
        bool[] used)
    {
        int bestIndex = -1;
        int bestRank = int.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            int edgeIndex = candidates[i];
            if (used[edgeIndex])
                continue;
            int turn = (edges[edgeIndex].Direction - incomingDirection + 4) % 4;
            int rank = turn == 3 ? 0 : turn == 0 ? 1 : turn == 1 ? 2 : 3;
            if (rank < bestRank)
            {
                bestRank = rank;
                bestIndex = edgeIndex;
            }
        }
        return bestIndex;
    }

    private static List<PixelPoint> RemoveCollinearVertices(List<PixelPoint> ring)
    {
        if (ring.Count < 4)
            return ring;
        var result = new List<PixelPoint>();
        for (int i = 0; i < ring.Count; i++)
        {
            PixelPoint previous = ring[(i - 1 + ring.Count) % ring.Count];
            PixelPoint current = ring[i];
            PixelPoint next = ring[(i + 1) % ring.Count];
            double cross = (current.X - previous.X) * (next.Y - current.Y) -
                           (current.Y - previous.Y) * (next.X - current.X);
            if (Math.Abs(cross) > 0.0000001)
                result.Add(current);
        }
        return result.Count >= 3 ? result : ring;
    }

    private static List<PixelPoint> SimplifyClosedRing(List<PixelPoint> ring, double tolerance)
    {
        if (ring.Count <= 3 || tolerance <= 0.0)
            return ring;
        int first = FindFarthestPoint(ring, 0);
        int second = FindFarthestPoint(ring, first);
        first = FindFarthestPoint(ring, second);
        if (first == second)
            return ring;
        List<PixelPoint> firstArc = SimplifyOpenLine(BuildRingArc(ring, first, second), tolerance);
        List<PixelPoint> secondArc = SimplifyOpenLine(BuildRingArc(ring, second, first), tolerance);
        var simplified = new List<PixelPoint>(firstArc);
        for (int i = 1; i < secondArc.Count - 1; i++)
            simplified.Add(secondArc[i]);
        return simplified.Count >= 3 ? simplified : ring;
    }

    private static int FindFarthestPoint(List<PixelPoint> points, int originIndex)
    {
        int farthestIndex = originIndex;
        double farthestDistance = -1.0;
        for (int i = 0; i < points.Count; i++)
        {
            double distance = SquaredDistance(points[originIndex], points[i]);
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthestIndex = i;
            }
        }
        return farthestIndex;
    }

    private static List<PixelPoint> BuildRingArc(
        List<PixelPoint> ring, int startIndex, int endIndex)
    {
        var arc = new List<PixelPoint>();
        int index = startIndex;
        arc.Add(ring[index]);
        while (index != endIndex)
        {
            index = (index + 1) % ring.Count;
            arc.Add(ring[index]);
        }
        return arc;
    }

    private static List<PixelPoint> SimplifyOpenLine(
        List<PixelPoint> points, double tolerance)
    {
        if (points.Count <= 2)
            return points;
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        MarkRamerDouglasPeucker(points, 0, points.Count - 1, tolerance * tolerance, keep);
        var result = new List<PixelPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }
        return result;
    }

    private static void MarkRamerDouglasPeucker(
        List<PixelPoint> points,
        int startIndex,
        int endIndex,
        double squaredTolerance,
        bool[] keep)
    {
        if (endIndex <= startIndex + 1)
            return;
        double maximumDistance = -1.0;
        int farthestIndex = -1;
        for (int i = startIndex + 1; i < endIndex; i++)
        {
            double distance = SquaredDistanceToSegment(points[i], points[startIndex], points[endIndex]);
            if (distance > maximumDistance)
            {
                maximumDistance = distance;
                farthestIndex = i;
            }
        }
        if (maximumDistance <= squaredTolerance || farthestIndex < 0)
            return;
        keep[farthestIndex] = true;
        MarkRamerDouglasPeucker(points, startIndex, farthestIndex, squaredTolerance, keep);
        MarkRamerDouglasPeucker(points, farthestIndex, endIndex, squaredTolerance, keep);
    }

    private static double SquaredDistanceToSegment(
        PixelPoint point, PixelPoint start, PixelPoint end)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double squaredLength = deltaX * deltaX + deltaY * deltaY;
        if (squaredLength <= double.Epsilon)
            return SquaredDistance(point, start);
        double projection = ((point.X - start.X) * deltaX +
                             (point.Y - start.Y) * deltaY) / squaredLength;
        projection = Math.Max(0.0, Math.Min(1.0, projection));
        return SquaredDistance(
            point,
            new PixelPoint(start.X + projection * deltaX, start.Y + projection * deltaY));
    }

    private static List<PixelPoint> LimitRingVertices(List<PixelPoint> ring, int maximum)
    {
        if (ring.Count <= maximum)
            return ring;
        var limited = new List<PixelPoint>(maximum);
        for (int i = 0; i < maximum; i++)
        {
            int index = (int)((long)i * ring.Count / maximum);
            limited.Add(ring[index]);
        }
        return limited;
    }

    private static PixelPoint InsetContourPoint(
        PixelPoint point, PixelComponent component, double insetPixels)
    {
        if (insetPixels <= 0.0)
            return point;
        double deltaX = component.CentroidX - point.X;
        double deltaY = component.CentroidY - point.Y;
        double length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (length <= 0.000001)
            return point;
        return new PixelPoint(
            point.X + deltaX / length * insetPixels,
            point.Y + deltaY / length * insetPixels);
    }

    private bool TryDiscoverMaskFiles(
        string resolvedPath, out List<string> files, out string error)
    {
        files = new List<string>();
        error = string.Empty;
        if (File.Exists(resolvedPath))
        {
            if (!IsSupportedImagePath(resolvedPath))
                return Fail("Mask file must be PNG, JPG, or JPEG.", out error);
            files.Add(resolvedPath);
            return true;
        }
        if (!Directory.Exists(resolvedPath))
            return Fail($"Mask path does not exist: '{resolvedPath}'.", out error);
        string[] discovered = Directory.GetFiles(resolvedPath, "*", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < discovered.Length; i++)
        {
            if (!IsSupportedImagePath(discovered[i]))
                continue;
            files.Add(discovered[i]);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files.Count > 0 || Fail($"No matching PNG/JPG masks were found in '{resolvedPath}'.", out error);
    }

    private static bool IsSupportedImagePath(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static double SignedPixelArea(List<PixelPoint> ring)
    {
        double twiceArea = 0.0;
        for (int i = 0; i < ring.Count; i++)
        {
            PixelPoint current = ring[i];
            PixelPoint next = ring[(i + 1) % ring.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea * 0.5;
    }

    private static double SignedGeoArea(List<GeoCoordinate> ring)
    {
        double twiceArea = 0.0;
        for (int i = 0; i < ring.Count; i++)
        {
            GeoCoordinate current = ring[i];
            GeoCoordinate next = ring[(i + 1) % ring.Count];
            twiceArea += current.Longitude * next.Latitude - next.Longitude * current.Latitude;
        }
        return twiceArea * 0.5;
    }

    private static double SquaredDistance(PixelPoint left, PixelPoint right)
    {
        double deltaX = left.X - right.X;
        double deltaY = left.Y - right.Y;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private static JArray VectorToJson(Vector3 vector)
    {
        return new JArray(vector.x, vector.y, vector.z);
    }

    private static JArray QuaternionToJson(Quaternion rotation)
    {
        return new JArray(rotation.x, rotation.y, rotation.z, rotation.w);
    }

    private static string SanitizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "object";
        char[] characters = raw.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < characters.Length; i++)
        {
            if (!char.IsLetterOrDigit(characters[i]) && characters[i] != '_' && characters[i] != '-')
                characters[i] = '_';
        }
        return new string(characters);
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

    private static bool TryWriteBytesAtomically(string path, byte[] bytes, out string error)
    {
        string temporaryPath = string.Empty;
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return Fail("Output path has no parent directory.", out error);
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporaryPath, bytes);
            ReplaceOrMove(temporaryPath, path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            DeleteTemporaryFile(temporaryPath);
            return Fail($"Could not write '{path}': {exception.Message}", out error);
        }
    }

    private static bool TryWriteTextAtomically(string path, string text, out string error)
    {
        string temporaryPath = string.Empty;
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return Fail("Output path has no parent directory.", out error);
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporaryPath, text);
            ReplaceOrMove(temporaryPath, path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            DeleteTemporaryFile(temporaryPath);
            return Fail($"Could not write '{path}': {exception.Message}", out error);
        }
    }

    private static void ReplaceOrMove(string temporaryPath, string finalPath)
    {
        if (File.Exists(finalPath))
            File.Replace(temporaryPath, finalPath, null);
        else
            File.Move(temporaryPath, finalPath);
    }

    private static void DeleteTemporaryFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original write error.
        }
    }

    private static void DestroyTemporaryCamera(Camera camera)
    {
        if (camera != null)
            DestroyObject(camera.gameObject);
    }

    private static void DestroyObject(UnityEngine.Object target)
    {
        if (target == null)
            return;
        if (Application.isPlaying)
            Destroy(target);
        else
            DestroyImmediate(target);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatSigned(double value)
    {
        return value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
    }

    private void OnDisable()
    {
        StopGameViewPreview();
    }

#if UNITY_EDITOR
    private static void FocusGameView()
    {
        Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
        if (gameViewType == null)
            return;
        UnityEditor.EditorWindow gameView = UnityEditor.EditorWindow.GetWindow(gameViewType);
        if (gameView != null)
            gameView.Focus();
    }
#endif

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private readonly struct TopViewContext
    {
        public readonly Vector3 CentreWorld;
        public readonly Vector3 CameraWorld;
        public readonly Quaternion CameraRotation;
        public readonly string ProjectionName;
        public readonly string ImageAlignment;
        public readonly Vector3 ImageUpWorld;
        public readonly int ImageWidth;
        public readonly int ImageHeight;
        public readonly float CoverageWidthMeters;
        public readonly float CoverageHeightMeters;
        public readonly float ReferenceHeightMeters;
        public readonly float FinalHeightMeters;
        public readonly float HeightOffsetMeters;
        public readonly float FinalHeightWorldUnits;
        public readonly float ImageRotationDegrees;
        public readonly float VerticalFieldOfViewDegrees;
        public readonly float FocalLengthMillimetres;
        public readonly Vector2 SensorSizeMillimetres;
        public readonly bool UsesPhysicalCamera;
        public readonly Vector3 WorldEast;
        public readonly Vector3 WorldNorth;
        public readonly Vector3 WorldUp;

        public TopViewContext(
            Vector3 centreWorld,
            Vector3 cameraWorld,
            Quaternion cameraRotation,
            string projectionName,
            string imageAlignment,
            Vector3 imageUpWorld,
            int imageWidth,
            int imageHeight,
            float coverageWidthMeters,
            float coverageHeightMeters,
            float referenceHeightMeters,
            float finalHeightMeters,
            float heightOffsetMeters,
            float finalHeightWorldUnits,
            float imageRotationDegrees,
            float verticalFieldOfViewDegrees,
            float focalLengthMillimetres,
            Vector2 sensorSizeMillimetres,
            bool usesPhysicalCamera,
            Vector3 worldEast,
            Vector3 worldNorth,
            Vector3 worldUp)
        {
            CentreWorld = centreWorld;
            CameraWorld = cameraWorld;
            CameraRotation = cameraRotation;
            ProjectionName = projectionName;
            ImageAlignment = imageAlignment;
            ImageUpWorld = imageUpWorld;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            CoverageWidthMeters = coverageWidthMeters;
            CoverageHeightMeters = coverageHeightMeters;
            ReferenceHeightMeters = referenceHeightMeters;
            FinalHeightMeters = finalHeightMeters;
            HeightOffsetMeters = heightOffsetMeters;
            FinalHeightWorldUnits = finalHeightWorldUnits;
            ImageRotationDegrees = imageRotationDegrees;
            VerticalFieldOfViewDegrees = verticalFieldOfViewDegrees;
            FocalLengthMillimetres = focalLengthMillimetres;
            SensorSizeMillimetres = sensorSizeMillimetres;
            UsesPhysicalCamera = usesPhysicalCamera;
            WorldEast = worldEast;
            WorldNorth = worldNorth;
            WorldUp = worldUp;
        }
    }

    private readonly struct ForegroundMask
    {
        public readonly int Width;
        public readonly int Height;
        public readonly bool[] Foreground;

        public ForegroundMask(int width, int height, bool[] foreground)
        {
            Width = width;
            Height = height;
            Foreground = foreground;
        }
    }

    private sealed class PixelComponent
    {
        public readonly List<int> Pixels;
        public readonly double CentroidX;
        public readonly double CentroidY;

        public PixelComponent(List<int> pixels, double centroidX, double centroidY)
        {
            Pixels = pixels;
            CentroidX = centroidX;
            CentroidY = centroidY;
        }
    }

    private readonly struct PixelPoint : IEquatable<PixelPoint>
    {
        public readonly double X;
        public readonly double Y;

        public PixelPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(PixelPoint other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y);
        }

        public override bool Equals(object obj)
        {
            return obj is PixelPoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }
    }

    private readonly struct DirectedPixelEdge
    {
        public readonly PixelPoint Start;
        public readonly PixelPoint End;
        public readonly int Direction;

        public DirectedPixelEdge(int startX, int startY, int endX, int endY)
        {
            Start = new PixelPoint(startX, startY);
            End = new PixelPoint(endX, endY);
            int deltaX = endX - startX;
            int deltaY = endY - startY;
            Direction = deltaX > 0 ? 0 : deltaY > 0 ? 1 : deltaX < 0 ? 2 : 3;
        }
    }

    private readonly struct GeoCoordinate
    {
        public readonly double Longitude;
        public readonly double Latitude;
        public readonly double Altitude;

        public GeoCoordinate(double longitude, double latitude, double altitude)
        {
            Longitude = longitude;
            Latitude = latitude;
            Altitude = altitude;
        }

        public bool SameHorizontalPosition(GeoCoordinate other)
        {
            return Longitude.Equals(other.Longitude) && Latitude.Equals(other.Latitude);
        }
    }

    private readonly struct PolygonSummary
    {
        public readonly int MaskFiles;
        public readonly int DiscoveredComponents;
        public readonly int AcceptedComponents;
        public readonly int ExportedPolygons;
        public readonly int AttemptedRays;
        public readonly int SuccessfulRays;

        public PolygonSummary(
            int maskFiles,
            int discoveredComponents,
            int acceptedComponents,
            int exportedPolygons,
            int attemptedRays,
            int successfulRays)
        {
            MaskFiles = maskFiles;
            DiscoveredComponents = discoveredComponents;
            AcceptedComponents = acceptedComponents;
            ExportedPolygons = exportedPolygons;
            AttemptedRays = attemptedRays;
            SuccessfulRays = successfulRays;
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
            UnityEngine.SceneManagement.Scene capturedPlayerScene = player.gameObject.scene;
            bool playerSceneIsValid = capturedPlayerScene.IsValid();
            UnityEngine.SceneManagement.Scene capturedCameraScene = camera != null
                ? camera.gameObject.scene
                : default;
            bool cameraSceneIsSeparate = capturedCameraScene.IsValid() &&
                                         (!playerSceneIsValid || capturedCameraScene.handle != capturedPlayerScene.handle);
            return new SceneDirtinessState(
                capturedPlayerScene,
                capturedCameraScene,
                playerSceneIsValid && capturedPlayerScene.isDirty,
                cameraSceneIsSeparate && capturedCameraScene.isDirty,
                playerSceneIsValid,
                cameraSceneIsSeparate);
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
                    $"[TopViewSplatProjector] Could not restore scene dirtiness state: {exception.Message}");
            }
        }
    }
#endif
}
