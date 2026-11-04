using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Triffid.Uav;
using UnityEditor;
using UnityEngine;

/// <summary>Deterministic checks runnable without adding a Unity test-framework dependency.</summary>
public static class UavProjectionChecks
{
    [MenuItem("TRIFFID/UAV/Run Projection Core Checks")]
    public static void RunCore()
    {
        int checkedMasks = 0;
        // Exhaustive 3x3 topology, including donuts, diagonal contacts and concavities.
        for (int bits = 1; bits < 512; bits++)
        {
            bool[] pixels = new bool[9];
            int count = 0;
            for (int i = 0; i < 9; i++) { pixels[i] = (bits & (1 << i)) != 0; if (pixels[i]) count++; }
            JObject observation = Observation(pixels, 3, 3);
            UavBinaryMask mask = UavBinaryMask.Decode(observation, 12, 12);
            List<UavMaskContours.Polygon> polygons = UavMaskContours.Trace(mask, 1000);
            double area = 0;
            foreach (UavMaskContours.Polygon polygon in polygons)
            {
                area += UavMaskContours.SignedArea(polygon.Exterior);
                foreach (List<Vector2> hole in polygon.Holes) area += UavMaskContours.SignedArea(hole);
            }
            Check(area == count, $"Contour area mismatch: mask {bits}, {area} != {count}");
            checkedMasks++;
        }
        var donut = Observation(new[] { true, true, true, true, false, true, true, true, true }, 3, 3);
        List<UavMaskContours.Polygon> rings = UavMaskContours.Trace(UavBinaryMask.Decode(donut, 12, 12), 1000);
        Check(rings.Count == 1 && rings[0].Holes.Count == 1, "Mask hole was lost.");
        var diagonal = Observation(new[] { true, false, false, true }, 2, 2);
        Check(UavMaskContours.Trace(UavBinaryMask.Decode(diagonal, 12, 12), 1000).Count == 2, "Diagonal islands were joined.");
        var point = Observation(new[] { false, true, true, true }, 2, 2);
        point["raycast_point_xy"] = new JArray(4, 4);
        Vector2 sample = UavBinaryMask.Decode(point, 12, 12).PointSample(point);
        Check(sample.x == 4.5f && sample.y == 4.5f, "Point rays do not use pixel centres.");
        point["raycast_point_xy"] = new JArray(3, 4);
        Throws(() => UavBinaryMask.Decode(point, 12, 12).PointSample(point));
        var broken = (JObject)donut.DeepClone(); broken["mask"]["counts"] = new JArray(0, 20);
        Throws(() => UavBinaryMask.Decode(broken, 12, 12));
        broken = (JObject)donut.DeepClone(); broken["mask"]["offset_xy"] = new JArray(-1, 4);
        Throws(() => UavBinaryMask.Decode(broken, 12, 12));
        broken = (JObject)donut.DeepClone(); broken["mask"]["counts"] = new JArray(0, 1);
        Throws(() => UavBinaryMask.Decode(broken, 12, 12));
        var exterior = new JArray(new JArray(0, 0, 1), new JArray(0, 1, 1), new JArray(1, 1, 1), new JArray(1, 0, 1));
        Check(UavGeoJsonRing.TryClose(exterior, true) && exterior.Count == 5, "Exterior closure/winding failed.");
        Check(JToken.DeepEquals(exterior[0], exterior[4]), "GeoJSON ring is open.");
        var bowtie = new JArray(new JArray(0, 0), new JArray(1, 1), new JArray(0, 1), new JArray(1, 0));
        Check(!UavGeoJsonRing.TryClose(bowtie, true), "Self-intersecting ring was accepted.");
        string file = Path.GetTempFileName();
        try
        {
            JObject document = new JObject
            {
                ["schema_version"] = "triffid.uav.observations.v1", ["task"] = "point_detections",
                ["coordinate_space"] = "original_image_pixels", ["pixel_origin"] = "top_left",
                ["axes"] = new JObject { ["x"] = "right", ["y"] = "down" }, ["bbox_format"] = "xyxy_max_exclusive",
                ["sources"] = new JArray(new JObject
                { ["source_id"] = "view_00", ["width"] = 12, ["height"] = 12, ["observations"] = new JArray(donut) })
            };
            File.WriteAllText(file, document.ToString());
            Check(UavObservationDocument.Read(file, "point_detections", "Point").Sources.Count == 1, "Perception JSON import failed.");
            Throws(() => UavObservationDocument.Read(file, "polygon_segmentation", "Polygon"));
            document["sources"][0]["observations"] = new JArray(donut.DeepClone(), donut.DeepClone());
            File.WriteAllText(file, document.ToString());
            Throws(() => UavObservationDocument.Read(file, "point_detections", "Point"));
        }
        finally { File.Delete(file); }
        Debug.Log($"[UAV Checks] PASS: {checkedMasks} exhaustive masks; holes, islands, pixel centres, invalid RLE, ring validity and perception JSON validation.");
    }
    private static JObject Observation(bool[] pixels, int width, int height)
    {
        var counts = new JArray();
        bool foreground = false;
        int length = 0;
        foreach (bool pixel in pixels)
        {
            if (pixel == foreground) length++;
            else { counts.Add(length); foreground = pixel; length = 1; }
        }
        counts.Add(length);
        return new JObject
        {
            ["observation_id"] = "example", ["class_id"] = 1, ["class_name"] = "person", ["target_geometry"] = "Point",
            ["bbox_xyxy"] = new JArray(3, 4, 3 + width, 4 + height),
            ["mask"] = new JObject
            {
                ["encoding"] = "binary_rle_row_major", ["size_hw"] = new JArray(height, width),
                ["offset_xy"] = new JArray(3, 4), ["counts"] = counts
            }
        };
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid input to be rejected.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
