using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public enum DetectionProjectionMode
{
    FullDetectionPolygon,
    RandomPointPerDetection
}

/// <summary>
/// Converts detection pixels in a GeoJSON FeatureCollection to WGS84 map hits by using the
/// pose, camera and filtered raycast implementation owned by SrtDroneRaycastPlayer.
/// </summary>
public sealed class GeoJsonDetectionGeoreferencer : MonoBehaviour
{
    private const string Wgs84CoordinateSpace = "WGS84 [longitude, latitude, altitude]";
    private const double PixelBoundaryTolerance = 0.001;

    [Header("Projection Engine")]
    [SerializeField] private SrtDroneRaycastPlayer player;

    [Header("Projection Mode")]
    [SerializeField]
    private DetectionProjectionMode detectionProjectionMode =
        DetectionProjectionMode.FullDetectionPolygon;

    [SerializeField]
    private int randomSeed = 12345;

    [Header("Paths")]
    [SerializeField] private string inputGeoJsonPath = "campus_video_detections.geojson";
    [SerializeField] private string outputGeoJsonPath = "Exports/campus_video_detections_wgs84.geojson";
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot inputPathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.StreamingAssets;
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot outputPathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.PersistentDataPath;

    [Header("Execution")]
    [SerializeField] private bool processOnStart;

    private bool conversionInProgress;

    private void Start()
    {
        if (processOnStart && Application.isPlaying)
            ConvertGeoJson();
    }

    [ContextMenu("Convert GeoJSON Pixel Coordinates To WGS84")]
    public void ConvertGeoJson()
    {
        if (conversionInProgress)
        {
            Debug.LogError("[GeoJsonDetectionGeoreferencer] A conversion is already running.", this);
            return;
        }
        conversionInProgress = true;
        bool hasSavedProjectionState = false;
        SrtDroneRaycastPlayer.ProjectionState savedProjectionState = default;
#if UNITY_EDITOR
        SceneDirtinessState sceneDirtinessState = default;
#endif
        try
        {
            if (player == null)
            {
                Debug.LogError("[GeoJsonDetectionGeoreferencer] SrtDroneRaycastPlayer reference is missing.", this);
                return;
            }

            savedProjectionState = player.CaptureProjectionState();
            hasSavedProjectionState = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                sceneDirtinessState = SceneDirtinessState.Capture(player, savedProjectionState.Camera);
#endif

            if (!player.TryPrepareForGeoreferencing(out string preparationError))
            {
                Debug.LogError(
                    $"[GeoJsonDetectionGeoreferencer] Conversion preparation failed: {preparationError}", this);
                return;
            }

            DetectionProjectionMode selectedMode = detectionProjectionMode;
            if (!TryConvert(selectedMode, out string outputPath, out int outputFeatureCount, out string error))
            {
                Debug.LogError($"[GeoJsonDetectionGeoreferencer] Conversion aborted: {error}", this);
                return;
            }

            Debug.Log(
                $"[GeoJsonDetectionGeoreferencer] Exported {outputFeatureCount} WGS84 feature(s) " +
                $"using {selectedMode} mode to '{outputPath}'.",
                this);
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[GeoJsonDetectionGeoreferencer] Conversion aborted by an unexpected runtime error: {exception.Message}",
                this);
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
            conversionInProgress = false;
        }
    }

    private bool TryConvert(
        DetectionProjectionMode selectedMode,
        out string resolvedOutputPath,
        out int outputFeatureCount,
        out string error)
    {
        resolvedOutputPath = string.Empty;
        outputFeatureCount = 0;
        error = string.Empty;

        if (player == null)
            return Fail("SrtDroneRaycastPlayer reference is missing.", out error);
        if (!player.TryGetGeoreferencingReadiness(out string readinessReason))
            return Fail($"SrtDroneRaycastPlayer is not ready: {readinessReason}", out error);

        string resolvedInputPath;
        try
        {
            resolvedInputPath = ResolvePath(inputGeoJsonPath, inputPathRoot);
            resolvedOutputPath = ResolvePath(outputGeoJsonPath, outputPathRoot);
        }
        catch (Exception exception)
        {
            return Fail($"Invalid input or output path: {exception.Message}", out error);
        }

        if (string.IsNullOrWhiteSpace(resolvedInputPath) || !File.Exists(resolvedInputPath))
            return Fail($"Missing input GeoJSON file: '{resolvedInputPath}'.", out error);
        if (string.IsNullOrWhiteSpace(resolvedOutputPath))
            return Fail("Output GeoJSON path is empty.", out error);
        if (string.Equals(
                Path.GetFullPath(resolvedInputPath), Path.GetFullPath(resolvedOutputPath),
                StringComparison.OrdinalIgnoreCase))
            return Fail("The output path resolves to the input GeoJSON. The input will never be overwritten.", out error);

        JObject inputRoot;
        try
        {
            inputRoot = JObject.Parse(File.ReadAllText(resolvedInputPath));
        }
        catch (Exception exception)
        {
            return Fail($"Failed to read or parse GeoJSON '{resolvedInputPath}': {exception.Message}", out error);
        }

        if (!TryValidateAndReadInput(
                inputRoot, resolvedInputPath, out InputContext context, out List<InputFeature> inputFeatures, out error))
            return false;
        if (!player.TryConfigureSourceFrameDimensions(
                context.frameWidth, context.frameHeight, out string dimensionError))
            return Fail($"Cannot configure source-video projection: {dimensionError}", out error);

        var outputEntries = new List<OutputEntry>();
        for (int i = 0; i < inputFeatures.Count; i++)
        {
            InputFeature inputFeature = inputFeatures[i];
            if (!TryConfigureFeatureFrame(inputFeature, out string configureError))
            {
                // Exact-frame availability was validated before any raycast, so reaching this path normally
                // means the runtime camera/physics state became invalid during processing.
                return Fail(
                    $"Feature {inputFeature.inputOrder} camera configuration failed: {configureError}", out error);
            }

            if (selectedMode == DetectionProjectionMode.RandomPointPerDetection)
            {
                ProcessRandomPoint(inputFeature, context, outputEntries);
                continue;
            }

            switch (inputFeature.intent)
            {
                case GeometryIntent.Point:
                    ProcessPoint(inputFeature, context, outputEntries);
                    break;
                case GeometryIntent.LineString:
                    ProcessLineString(inputFeature, context, outputEntries);
                    break;
                case GeometryIntent.Polygon:
                    ProcessPolygon(inputFeature, context, outputEntries);
                    break;
            }
        }

        outputEntries.Sort((left, right) => left.inputOrder.CompareTo(right.inputOrder));
        var outputFeatures = new JArray();
        for (int i = 0; i < outputEntries.Count; i++)
            outputFeatures.Add(outputEntries[i].feature);

        JObject outputRoot = (JObject)inputRoot.DeepClone();
        outputRoot["type"] = "FeatureCollection";
        JObject outputMetadata = outputRoot["metadata"] as JObject;
        if (outputMetadata == null)
        {
            // Input validation requires metadata; this protects against accidental mutation between stages.
            return Fail("GeoJSON metadata disappeared during conversion.", out error);
        }
        outputMetadata["coordinate_space"] = Wgs84CoordinateSpace;
        if (outputRoot["coordinate_space"] != null)
            outputRoot["coordinate_space"] = Wgs84CoordinateSpace;
        outputRoot["features"] = outputFeatures;

        string serialized;
        try
        {
            if (ContainsNonFiniteNumber(outputRoot))
                return Fail("The final FeatureCollection contains NaN or Infinity and was not written.", out error);
            serialized = JsonConvert.SerializeObject(
                outputRoot, Formatting.Indented,
                new JsonSerializerSettings
                {
                    Culture = CultureInfo.InvariantCulture
                });
            // Parse once before touching the output path. This also guards against an invalid custom token.
            JObject.Parse(serialized);
        }
        catch (Exception exception)
        {
            return Fail($"Failed to serialize the final FeatureCollection: {exception.Message}", out error);
        }

        if (!TryWriteAtomically(resolvedOutputPath, serialized, out error))
            return false;

        outputFeatureCount = outputFeatures.Count;
        return true;
    }

    private bool TryValidateAndReadInput(
        JObject root,
        string inputPath,
        out InputContext context,
        out List<InputFeature> inputFeatures,
        out string error)
    {
        context = default;
        inputFeatures = new List<InputFeature>();
        error = string.Empty;

        if (!string.Equals(ReadString(root["type"]), "FeatureCollection", StringComparison.Ordinal))
            return Fail("Input JSON type must be 'FeatureCollection'.", out error);
        if (!(root["metadata"] is JObject metadata))
            return Fail("Input GeoJSON metadata object is missing.", out error);
        if (!TryReadPositiveInt(metadata["frame_width"], out int frameWidth))
            return Fail("GeoJSON metadata.frame_width is missing or invalid.", out error);
        if (!TryReadPositiveInt(metadata["frame_height"], out int frameHeight))
            return Fail("GeoJSON metadata.frame_height is missing or invalid.", out error);
        if (frameWidth < 2 || frameHeight < 2)
            return Fail("GeoJSON frame_width and frame_height must both be at least 2.", out error);

        double framesPerSecond = 0.0;
        if (metadata.TryGetValue("fps", out JToken fpsToken) &&
            !TryReadDouble(fpsToken, out framesPerSecond))
            return Fail("GeoJSON metadata.fps is invalid.", out error);
        double timestampTolerance = framesPerSecond > 0.0
            ? 1.0 / framesPerSecond
            : 1.0 / 30.0;
        if (metadata.TryGetValue("total_frames", out JToken totalFramesToken))
        {
            if (!TryReadPositiveInt(totalFramesToken, out int totalFrames))
                return Fail("GeoJSON metadata.total_frames is invalid.", out error);
            if (totalFrames != player.TotalSrtFrameCount)
                return Fail(
                    $"GeoJSON metadata.total_frames is {totalFrames}, but parsed SRT frame count is {player.TotalSrtFrameCount}. " +
                    $"Input GeoJSON: '{inputPath}'. Input SRT: '{player.ResolvedSrtPath}'.", out error);
        }
        if (metadata.TryGetValue("srt_frames", out JToken srtFramesToken))
        {
            if (!TryReadPositiveInt(srtFramesToken, out int srtFrames))
                return Fail("GeoJSON metadata.srt_frames is invalid.", out error);
            if (srtFrames != player.TotalSrtFrameCount)
                return Fail(
                    $"GeoJSON metadata.srt_frames is {srtFrames}, but parsed SRT frame count is {player.TotalSrtFrameCount}. " +
                    $"Input GeoJSON: '{inputPath}'. Input SRT: '{player.ResolvedSrtPath}'.", out error);
        }
        if (!(root["features"] is JArray features))
            return Fail("Input GeoJSON features array is missing.", out error);

        context = new InputContext(frameWidth, frameHeight, timestampTolerance);
        var firstFeatureForFrame = new Dictionary<int, InputFeature>();
        for (int i = 0; i < features.Count; i++)
        {
            if (!(features[i] is JObject feature) || !(feature["properties"] is JObject properties) ||
                !(feature["geometry"] is JObject geometry))
            {
                Debug.LogError(
                    $"[GeoJsonDetectionGeoreferencer] Omitted malformed Feature at index {i}: type, properties, or geometry is missing.",
                    this);
                continue;
            }

            if (!TryReadGeometryIntent(properties, geometry, out GeometryIntent intent, out string geometryError))
            {
                Debug.LogError(
                    $"[GeoJsonDetectionGeoreferencer] Omitted Feature at index {i}: {geometryError}", this);
                continue;
            }

            bool hasFrameIndex = properties.TryGetValue("frame_index", out JToken frameIndexToken);
            int frameIndex = -1;
            if (hasFrameIndex && (!TryReadNonNegativeInt(frameIndexToken, out frameIndex)))
                return Fail($"Feature {i} has an invalid frame_index in '{inputPath}'.", out error);

            bool hasTimestamp = properties.TryGetValue("timestamp_s", out JToken timestampToken);
            double timestamp = 0.0;
            if (hasTimestamp && !TryReadDouble(timestampToken, out timestamp))
                return Fail($"Feature {i} has an invalid timestamp_s in '{inputPath}'.", out error);
            if (!hasFrameIndex && !hasTimestamp)
            {
                Debug.LogError(
                    $"[GeoJsonDetectionGeoreferencer] Omitted Feature at index {i}: both frame_index and timestamp_s are missing.",
                    this);
                continue;
            }

            var inputFeature = new InputFeature(
                i, feature, properties, geometry, intent, hasFrameIndex, frameIndex, hasTimestamp, timestamp);
            if (!TryReadPixels(inputFeature, out List<Vector2d> pixels, out string pixelReadError))
            {
                Debug.LogError(
                    $"[GeoJsonDetectionGeoreferencer] Omitted Feature at index {i}: {pixelReadError}", this);
                continue;
            }
            inputFeature.pixels = pixels;
            inputFeature.pixelIsValid = new bool[pixels.Count];
            for (int pixelIndex = 0; pixelIndex < pixels.Count; pixelIndex++)
            {
                bool pixelIsValid = IsPixelWithinFrame(pixels[pixelIndex], context);
                inputFeature.pixelIsValid[pixelIndex] = pixelIsValid;
                if (!pixelIsValid)
                {
                    Debug.LogError(
                        $"[GeoJsonDetectionGeoreferencer] Invalid pixel will be omitted: Feature {i}, " +
                        $"coordinate {pixelIndex}, pixel [{Format(pixels[pixelIndex].x)}, {Format(pixels[pixelIndex].y)}], " +
                        $"image {context.frameWidth}x{context.frameHeight}.",
                        this);
                }
            }
            inputFeatures.Add(inputFeature);
            if (hasFrameIndex && !firstFeatureForFrame.ContainsKey(frameIndex))
                firstFeatureForFrame.Add(frameIndex, inputFeature);
        }

        // This complete consistency pass happens before the first camera configuration or raycast.
        foreach (KeyValuePair<int, InputFeature> pair in firstFeatureForFrame)
        {
            int frameIndex = pair.Key;
            InputFeature feature = pair.Value;
            if (!player.TryMapGeoJsonFrameIndex(frameIndex, out int frameCnt) ||
                !player.TryGetTelemetryForFrameCnt(frameCnt, out SrtDroneRaycastPlayer.SrtTelemetry telemetry))
            {
                return Fail(BuildMissingFrameError(frameIndex, frameIndex + 1, inputPath), out error);
            }
            if (!TryValidateTelemetry(feature, telemetry, context.timestampTolerance, inputPath, out error))
                return false;
        }

        // A timestamp-only feature is an explicitly permitted fallback and is validated against
        // the nearest authoritative SRT frame before it can be processed.
        for (int i = 0; i < inputFeatures.Count; i++)
        {
            InputFeature feature = inputFeatures[i];
            if (feature.hasFrameIndex)
                continue;
            if (!player.TryGetNearestTelemetryForTimestamp(
                    feature.timestampSeconds, out SrtDroneRaycastPlayer.SrtTelemetry telemetry))
                return Fail(
                    $"GeoJSON frame_index <missing>, expected SRT FrameCnt <timestamp fallback>, field timestamp_s, " +
                    $"GeoJSON value {Format(feature.timestampSeconds)}, SRT value <no frame available>, " +
                    $"Input GeoJSON: '{inputPath}', Input SRT: '{player.ResolvedSrtPath}'.", out error);
            if (!TryValidateTelemetry(feature, telemetry, context.timestampTolerance, inputPath, out error, false))
                return false;
        }

        return true;
    }

    private bool TryValidateTelemetry(
        InputFeature feature,
        SrtDroneRaycastPlayer.SrtTelemetry telemetry,
        double timestampTolerance,
        string inputPath,
        out string error,
        bool requireExactTimestamp = true)
    {
        error = string.Empty;
        int expectedFrameCnt = feature.hasFrameIndex ? feature.frameIndex + 1 : telemetry.FrameCnt;
        if (!TryCompareRounded(feature, "drone_latitude", telemetry.Latitude, 6, expectedFrameCnt, inputPath, out error))
            return false;
        if (!TryCompareRounded(feature, "drone_longitude", telemetry.Longitude, 6, expectedFrameCnt, inputPath, out error))
            return false;
        if (!TryCompareRounded(feature, "drone_altitude_m", telemetry.AbsoluteAltitude, 3, expectedFrameCnt, inputPath, out error))
            return false;
        if (!TryCompareRounded(feature, "drone_rel_altitude_m", telemetry.RelativeAltitude, 3, expectedFrameCnt, inputPath, out error))
            return false;
        if (!TryCompareRounded(feature, "gimbal_yaw", telemetry.Yaw, 1, expectedFrameCnt, inputPath, out error))
            return false;
        if (!TryCompareRounded(feature, "gimbal_pitch", telemetry.Pitch, 1, expectedFrameCnt, inputPath, out error))
            return false;
        if (!TryCompareRounded(feature, "gimbal_roll", telemetry.Roll, 1, expectedFrameCnt, inputPath, out error))
            return false;

        if (!feature.hasTimestamp)
            return FailMismatch(feature, expectedFrameCnt, "timestamp_s", "<missing>", telemetry.TimeSeconds, inputPath, out error);
        if (requireExactTimestamp &&
            Math.Abs(feature.timestampSeconds - telemetry.TimeSeconds) > timestampTolerance + 1e-6)
            return FailMismatch(
                feature, expectedFrameCnt, "timestamp_s", Format(feature.timestampSeconds),
                telemetry.TimeSeconds, inputPath, out error);
        return true;
    }

    private bool TryCompareRounded(
        InputFeature feature,
        string fieldName,
        double srtValue,
        int digits,
        int expectedFrameCnt,
        string inputPath,
        out string error)
    {
        if (!feature.properties.TryGetValue(fieldName, out JToken token) || !TryReadDouble(token, out double geoJsonValue))
            return FailMismatch(feature, expectedFrameCnt, fieldName, "<missing or invalid>", srtValue, inputPath, out error);

        double roundedGeoJson = Math.Round(geoJsonValue, digits, MidpointRounding.AwayFromZero);
        double roundedSrt = Math.Round(srtValue, digits, MidpointRounding.AwayFromZero);
        if (roundedGeoJson != roundedSrt)
            return FailMismatch(feature, expectedFrameCnt, fieldName, Format(geoJsonValue), srtValue, inputPath, out error);

        error = string.Empty;
        return true;
    }

    private bool FailMismatch(
        InputFeature feature,
        int expectedFrameCnt,
        string fieldName,
        string geoJsonValue,
        double srtValue,
        string inputPath,
        out string error)
    {
        string frameIndexText = feature.hasFrameIndex
            ? feature.frameIndex.ToString(CultureInfo.InvariantCulture)
            : "<missing>";
        error =
            $"SRT-GeoJSON telemetry mismatch. GeoJSON frame_index {frameIndexText}, expected SRT FrameCnt {expectedFrameCnt}, " +
            $"field {fieldName}, GeoJSON value {geoJsonValue}, SRT value {Format(srtValue)}, " +
            $"Input GeoJSON: '{inputPath}', Input SRT: '{player.ResolvedSrtPath}'.";
        return false;
    }

    private string BuildMissingFrameError(int frameIndex, int expectedFrameCnt, string inputPath)
    {
        return
            $"SRT-GeoJSON telemetry mismatch. GeoJSON frame_index {frameIndex}, expected SRT FrameCnt {expectedFrameCnt}, " +
            $"field FrameCnt, GeoJSON value {frameIndex}, SRT value <missing or malformed>, " +
            $"Input GeoJSON: '{inputPath}', Input SRT: '{player.ResolvedSrtPath}'.";
    }

    private static bool TryReadGeometryIntent(
        JObject properties, JObject geometry, out GeometryIntent intent, out string error)
    {
        intent = default;
        error = string.Empty;
        string declared = ReadString(properties["classes_txt_geometry"]);
        string geometryType = ReadString(geometry["type"]);
        if (string.Equals(declared, "Point", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(geometryType, "Point", StringComparison.Ordinal))
            {
                error = $"classes_txt_geometry is Point but geometry.type is '{geometryType}'.";
                return false;
            }
            intent = GeometryIntent.Point;
            return true;
        }
        if (string.Equals(declared, "Line", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(declared, "LineString", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(geometryType, "MultiPoint", StringComparison.Ordinal) &&
                !string.Equals(geometryType, "LineString", StringComparison.Ordinal))
            {
                error = $"Line detection has unsupported geometry.type '{geometryType}'.";
                return false;
            }
            intent = GeometryIntent.LineString;
            return true;
        }
        if (string.Equals(declared, "Polygon", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(geometryType, "MultiPoint", StringComparison.Ordinal))
            {
                error = $"Polygon detection has unsupported geometry.type '{geometryType}'.";
                return false;
            }
            intent = GeometryIntent.Polygon;
            return true;
        }

        error = $"unsupported or missing properties.classes_txt_geometry '{declared}'.";
        return false;
    }

    private static bool TryReadPixels(InputFeature feature, out List<Vector2d> pixels, out string error)
    {
        pixels = new List<Vector2d>();
        error = string.Empty;
        JToken coordinatesToken = feature.geometry["coordinates"];

        if (feature.intent == GeometryIntent.Point)
        {
            if (!TryReadPixel(coordinatesToken, out Vector2d point))
            {
                error = "Point coordinates must contain two finite numbers.";
                return false;
            }
            pixels.Add(point);
            return true;
        }

        JArray coordinateArray = coordinatesToken as JArray;

        if (coordinateArray == null)
        {
            error = "geometry.coordinates must be an array of pixel coordinates.";
            return false;
        }
        for (int i = 0; i < coordinateArray.Count; i++)
        {
            if (!TryReadPixel(coordinateArray[i], out Vector2d pixel))
            {
                error = $"pixel coordinate {i} must contain two finite numbers.";
                return false;
            }
            pixels.Add(pixel);
        }

        if (feature.intent == GeometryIntent.LineString && pixels.Count < 2)
        {
            error = "Line detection requires at least two input pixels.";
            return false;
        }
        if (feature.intent == GeometryIntent.Polygon && pixels.Count < 3)
        {
            error = $"Polygon detection requires at least three pixel coordinates; found {pixels.Count}.";
            return false;
        }
        return true;
    }

    private bool TryConfigureFeatureFrame(InputFeature feature, out string error)
    {
        return feature.hasFrameIndex
            ? player.TryConfigureForFrameCnt(feature.frameIndex + 1, out error)
            : player.TryConfigureForTimestamp(feature.timestampSeconds, out error);
    }

    private void ProcessPoint(
        InputFeature input, InputContext context, List<OutputEntry> outputEntries)
    {
        if (!TryProjectPixel(input.pixels[0], context, input, 0, out JArray coordinate))
            return;

        JObject outputFeature = CloneFeatureWithGeometry(input, "Point", coordinate);
        outputEntries.Add(new OutputEntry(input.inputOrder, outputFeature));
    }

    private void ProcessRandomPoint(
        InputFeature input, InputContext context, List<OutputEntry> outputEntries)
    {
        int candidateCount = input.pixels.Count;
        var randomizedIndices = new int[candidateCount];
        for (int i = 0; i < candidateCount; i++)
            randomizedIndices[i] = i;

        var random = new System.Random(CreateFeatureRandomSeed(randomSeed, input.inputOrder));
        for (int i = candidateCount - 1; i > 0; i--)
        {
            int swapIndex = random.Next(i + 1);
            int temporary = randomizedIndices[i];
            randomizedIndices[i] = randomizedIndices[swapIndex];
            randomizedIndices[swapIndex] = temporary;
        }

        for (int i = 0; i < randomizedIndices.Length; i++)
        {
            int coordinateIndex = randomizedIndices[i];
            if (!TryProjectPixel(
                    input.pixels[coordinateIndex], context, input, coordinateIndex, out JArray coordinate))
                continue;

            JObject outputFeature = CloneFeatureWithGeometry(input, "Point", coordinate);
            JObject outputProperties = (JObject)outputFeature["properties"];
            outputProperties["classes_txt_geometry"] = "Point";
            outputEntries.Add(new OutputEntry(input.inputOrder, outputFeature));
            return;
        }
    }

    private static int CreateFeatureRandomSeed(int seed, int featureInputIndex)
    {
        unchecked
        {
            uint mixed = (uint)seed;
            mixed ^= (uint)featureInputIndex + 0x9E3779B9u + (mixed << 6) + (mixed >> 2);
            mixed ^= mixed >> 16;
            mixed *= 0x85EBCA6Bu;
            mixed ^= mixed >> 13;
            return (int)mixed;
        }
    }

    private void ProcessLineString(
        InputFeature input, InputContext context, List<OutputEntry> outputEntries)
    {
        var coordinates = new JArray();
        for (int i = 0; i < input.pixels.Count; i++)
        {
            if (TryProjectPixel(input.pixels[i], context, input, i, out JArray coordinate))
                coordinates.Add(coordinate);
        }
        if (coordinates.Count < 2)
        {
            Debug.LogWarning(
                $"[GeoJsonDetectionGeoreferencer] Omitted LineString feature {input.inputOrder}: fewer than two raycasts succeeded.",
                this);
            return;
        }

        JObject outputFeature = CloneFeatureWithGeometry(input, "LineString", coordinates);
        outputEntries.Add(new OutputEntry(input.inputOrder, outputFeature));
    }

    private void ProcessPolygon(
        InputFeature input, InputContext context, List<OutputEntry> outputEntries)
    {
        var successfulCoordinates = new JArray();
        var distinctCoordinates = new List<JArray>();
        for (int i = 0; i < input.pixels.Count; i++)
        {
            if (!TryProjectPixel(input.pixels[i], context, input, i, out JArray coordinate))
                continue;

            successfulCoordinates.Add(coordinate);
            bool isDistinct = true;
            for (int j = 0; j < distinctCoordinates.Count; j++)
            {
                if (CoordinatesEqual(coordinate, distinctCoordinates[j]))
                {
                    isDistinct = false;
                    break;
                }
            }
            if (isDistinct)
                distinctCoordinates.Add(coordinate);
        }

        if (distinctCoordinates.Count < 3)
        {
            Debug.LogWarning(
                $"[GeoJsonDetectionGeoreferencer] Omitted Polygon feature {input.inputOrder}: " +
                "fewer than three distinct raycasts succeeded.",
                this);
            return;
        }

        successfulCoordinates.Add(successfulCoordinates[0].DeepClone());
        var polygonCoordinates = new JArray();
        polygonCoordinates.Add(successfulCoordinates);
        JObject outputFeature = CloneFeatureWithGeometry(input, "Polygon", polygonCoordinates);
        outputEntries.Add(new OutputEntry(input.inputOrder, outputFeature));
    }

    private bool TryProjectPixel(
        Vector2d pixel,
        InputContext context,
        InputFeature feature,
        int coordinateIndex,
        out JArray coordinate)
    {
        coordinate = null;
        if (feature.pixelIsValid == null || coordinateIndex < 0 ||
            coordinateIndex >= feature.pixelIsValid.Length || !feature.pixelIsValid[coordinateIndex])
            return false;
        if (!TryNormalizePixel(pixel, context, out double normalizedX, out double normalizedYFromBottom))
            return false;

        Camera camera = player.DroneViewCamera;
        if (camera == null)
            return false;
        Ray ray = camera.ViewportPointToRay(new Vector3((float)normalizedX, (float)normalizedYFromBottom, 0f));
        if (!player.TryRaycastMap(ray, out RaycastHit hit))
            return false;
        if (!player.TryConvertWorldToWgs84(
                hit.point, out double longitude, out double latitude, out double altitude))
            return false;

        coordinate = new JArray(longitude, latitude, altitude);
        return true;
    }

    private static bool TryNormalizePixel(
        Vector2d pixel, InputContext context, out double normalizedX, out double normalizedYFromBottom)
    {
        normalizedX = normalizedYFromBottom = 0.0;
        if (!IsPixelWithinFrame(pixel, context))
            return false;

        double x = pixel.x < 0.0
            ? 0.0
            : pixel.x >= context.frameWidth ? context.frameWidth - 1e-9 : pixel.x;
        double y = pixel.y < 0.0
            ? 0.0
            : pixel.y >= context.frameHeight ? context.frameHeight - 1e-9 : pixel.y;
        normalizedX = (x + 0.5) / context.frameWidth;
        normalizedYFromBottom = 1.0 - ((y + 0.5) / context.frameHeight);
        return IsFinite(normalizedX) && IsFinite(normalizedYFromBottom);
    }

    private static bool IsPixelWithinFrame(Vector2d pixel, InputContext context)
    {
        return pixel.x >= -PixelBoundaryTolerance &&
               pixel.x < context.frameWidth + PixelBoundaryTolerance &&
               pixel.y >= -PixelBoundaryTolerance &&
               pixel.y < context.frameHeight + PixelBoundaryTolerance;
    }

    private static JObject CloneFeatureWithGeometry(InputFeature input, string geometryType, JToken coordinates)
    {
        JObject output = (JObject)input.feature.DeepClone();
        JObject properties = (JObject)output["properties"];
        properties["coordinate_space"] = Wgs84CoordinateSpace;
        output["geometry"] = new JObject
        {
            ["type"] = geometryType,
            ["coordinates"] = coordinates
        };
        return output;
    }

    private static bool CoordinatesEqual(JArray left, JArray right)
    {
        if (left.Count != 3 || right.Count != 3)
            return false;
        return (double)left[0] == (double)right[0] &&
               (double)left[1] == (double)right[1] &&
               (double)left[2] == (double)right[2];
    }

    private static bool TryWriteAtomically(string outputPath, string contents, out string error)
    {
        error = string.Empty;
        string temporaryPath = string.Empty;
        try
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory))
                return Fail("Output path has no valid parent directory.", out error);
            Directory.CreateDirectory(directory);

            temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporaryPath, contents);
            if (File.Exists(outputPath))
                File.Replace(temporaryPath, outputPath, null);
            else
                File.Move(temporaryPath, outputPath);
            return true;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Preserve the original failure. The temp path is included for manual cleanup.
                }
            }
            return Fail(
                $"Failed to write output GeoJSON '{outputPath}' atomically: {exception.Message}" +
                (string.IsNullOrWhiteSpace(temporaryPath) ? string.Empty : $" Temporary path: '{temporaryPath}'."),
                out error);
        }
    }

    private static string ResolvePath(
        string rawPath, SrtDroneRaycastPlayer.FilePathRoot pathRoot)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;
        if (Path.IsPathRooted(rawPath))
            return Path.GetFullPath(rawPath);
        return Path.GetFullPath(Path.Combine(
            SrtDroneRaycastPlayer.GetPathRoot(pathRoot),
            rawPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool TryReadPixel(JToken token, out Vector2d pixel)
    {
        pixel = default;
        if (!(token is JArray coordinate) || coordinate.Count != 2 ||
            !TryReadDouble(coordinate[0], out double x) || !TryReadDouble(coordinate[1], out double y))
            return false;
        pixel = new Vector2d(x, y);
        return true;
    }

    private static bool TryReadDouble(JToken token, out double value)
    {
        value = 0.0;
        if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
            return false;
        try
        {
            value = token.Value<double>();
            return IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPositiveInt(JToken token, out int value)
    {
        return TryReadNonNegativeInt(token, out value) && value > 0;
    }

    private static bool TryReadNonNegativeInt(JToken token, out int value)
    {
        value = 0;
        if (token == null || token.Type != JTokenType.Integer)
            return false;
        try
        {
            long parsed = token.Value<long>();
            if (parsed < 0 || parsed > int.MaxValue)
                return false;
            value = (int)parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadString(JToken token)
    {
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : string.Empty;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool ContainsNonFiniteNumber(JToken token)
    {
        if (token == null)
            return false;
        if (token.Type == JTokenType.Float)
        {
            try
            {
                return !IsFinite(token.Value<double>());
            }
            catch
            {
                return true;
            }
        }
        if (!(token is JContainer container))
            return false;
        foreach (JToken child in container.Children())
        {
            if (ContainsNonFiniteNumber(child))
                return true;
        }
        return false;
    }

    private static string Format(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private enum GeometryIntent
    {
        Point,
        LineString,
        Polygon
    }

    private readonly struct InputContext
    {
        public readonly int frameWidth;
        public readonly int frameHeight;
        public readonly double timestampTolerance;

        public InputContext(int frameWidth, int frameHeight, double timestampTolerance)
        {
            this.frameWidth = frameWidth;
            this.frameHeight = frameHeight;
            this.timestampTolerance = timestampTolerance;
        }
    }

    private sealed class InputFeature
    {
        public readonly int inputOrder;
        public readonly JObject feature;
        public readonly JObject properties;
        public readonly JObject geometry;
        public readonly GeometryIntent intent;
        public readonly bool hasFrameIndex;
        public readonly int frameIndex;
        public readonly bool hasTimestamp;
        public readonly double timestampSeconds;
        public List<Vector2d> pixels;
        public bool[] pixelIsValid;

        public InputFeature(
            int inputOrder,
            JObject feature,
            JObject properties,
            JObject geometry,
            GeometryIntent intent,
            bool hasFrameIndex,
            int frameIndex,
            bool hasTimestamp,
            double timestampSeconds)
        {
            this.inputOrder = inputOrder;
            this.feature = feature;
            this.properties = properties;
            this.geometry = geometry;
            this.intent = intent;
            this.hasFrameIndex = hasFrameIndex;
            this.frameIndex = frameIndex;
            this.hasTimestamp = hasTimestamp;
            this.timestampSeconds = timestampSeconds;
        }
    }

    private readonly struct Vector2d
    {
        public readonly double x;
        public readonly double y;

        public Vector2d(double x, double y)
        {
            this.x = x;
            this.y = y;
        }
    }

    private readonly struct OutputEntry
    {
        public readonly int inputOrder;
        public readonly JObject feature;

        public OutputEntry(int inputOrder, JObject feature)
        {
            this.inputOrder = inputOrder;
            this.feature = feature;
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
                    $"[GeoJsonDetectionGeoreferencer] Could not restore scene dirtiness state: {exception.Message}");
            }
        }
    }
#endif
}
