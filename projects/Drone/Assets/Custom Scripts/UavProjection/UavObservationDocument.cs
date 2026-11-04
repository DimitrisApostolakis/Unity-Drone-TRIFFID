using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Triffid.Uav
{
    /// <summary>The versioned perception contract, independent of SANSA or other backends.</summary>
    public sealed class UavObservationDocument
    {
        public readonly List<UavObservationSource> Sources = new List<UavObservationSource>();
        public readonly string FilePath;
        private UavObservationDocument(string path) { FilePath = path; }

        public static UavObservationDocument Read(string path, string task, string geometry)
        {
            JObject root = JObject.Parse(File.ReadAllText(path));
            Require((string)root["schema_version"] == "triffid.uav.observations.v1", "Unsupported observation schema.");
            Require((string)root["task"] == task, $"Expected {task} results.");
            Require((string)root["coordinate_space"] == "original_image_pixels" &&
                (string)root["pixel_origin"] == "top_left" &&
                (string)root["axes"]?["x"] == "right" && (string)root["axes"]?["y"] == "down" &&
                (string)root["bbox_format"] == "xyxy_max_exclusive", "Unsupported pixel coordinates.");
            var doc = new UavObservationDocument(Path.GetFullPath(path));
            var sources = root["sources"] as JArray;
            Require(sources != null && sources.Count > 0, "No sources in observation document.");
            var ids = new HashSet<string>();
            var observations = new HashSet<string>();
            foreach (JObject source in sources)
            {
                string id = Text(source, "source_id");
                Require(ids.Add(id), $"Repeated source ID: {id}");
                var item = new UavObservationSource(source, id, Integer(source["width"]), Integer(source["height"]));
                Require(item.Width > 0 && item.Height > 0 &&
                    (long)item.Width * item.Height <= UavBinaryMask.MaximumPixels, "Invalid or excessive source dimensions.");
                var entries = source["observations"] as JArray;
                Require(entries != null, $"Missing observations in {id}.");
                foreach (JObject entry in entries)
                {
                    Require((string)entry["target_geometry"] == geometry, $"Wrong geometry in {id}.");
                    Require(observations.Add(Text(entry, "observation_id")), "Repeated observation ID.");
                    Text(entry, "class_name");
                    Integer(entry["class_id"]);
                    item.Observations.Add(entry);
                }
                doc.Sources.Add(item);
            }
            return doc;
        }

        public static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        public static string Text(JObject value, string name)
        {
            string text = value[name]?.Type == JTokenType.String ? (string)value[name] : null;
            Require(!string.IsNullOrWhiteSpace(text), $"Missing {name}.");
            return text;
        }
        public static int Integer(JToken value)
        {
            Require(value != null && value.Type == JTokenType.Integer, "Expected an integer.");
            return checked((int)value);
        }
        public static double Number(JToken value)
        {
            Require(value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer), "Expected a number.");
            double n = (double)value;
            Require(!double.IsNaN(n) && !double.IsInfinity(n), "Non-finite coordinate.");
            return n;
        }
    }

    public sealed class UavObservationSource
    {
        public readonly JObject Metadata;
        public readonly string Id;
        public readonly int Width, Height;
        public readonly List<JObject> Observations = new List<JObject>();
        public UavObservationSource(JObject metadata, string id, int width, int height)
        { Metadata = metadata; Id = id; Width = width; Height = height; }
    }

    public sealed class UavBinaryMask
    {
        public const int MaximumPixels = 100000000;
        public readonly int Width, Height, X, Y;
        private readonly bool[] foreground;
        private UavBinaryMask(int width, int height, int x, int y, bool[] pixels)
        { Width = width; Height = height; X = x; Y = y; foreground = pixels; }
        public bool Contains(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height && foreground[y * Width + x];

        public static UavBinaryMask Decode(JObject observation, int sourceWidth, int sourceHeight)
        {
            JObject mask = observation["mask"] as JObject;
            UavObservationDocument.Require((string)mask?["encoding"] == "binary_rle_row_major", "Unsupported mask encoding.");
            var size = mask["size_hw"] as JArray;
            var offset = mask["offset_xy"] as JArray;
            var runs = mask["counts"] as JArray;
            UavObservationDocument.Require(size?.Count == 2 && offset?.Count == 2 && runs?.Count > 0, "Invalid cropped RLE.");
            int h = UavObservationDocument.Integer(size[0]), w = UavObservationDocument.Integer(size[1]);
            int x = UavObservationDocument.Integer(offset[0]), y = UavObservationDocument.Integer(offset[1]);
            long area = (long)w * h;
            UavObservationDocument.Require(w > 0 && h > 0 && area <= MaximumPixels && x >= 0 && y >= 0 &&
                (long)x + w <= sourceWidth && (long)y + h <= sourceHeight, "Mask exceeds image bounds.");
            var pixels = new bool[(int)area];
            int cursor = 0, countForeground = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                int count = UavObservationDocument.Integer(runs[i]);
                UavObservationDocument.Require(count >= 0 && (long)cursor + count <= area, "RLE run exceeds mask size.");
                if ((i & 1) == 1)
                {
                    countForeground += count;
                    for (int j = cursor; j < cursor + count; j++) pixels[j] = true;
                }
                cursor += count;
            }
            UavObservationDocument.Require(cursor == area && countForeground > 0, "Empty or truncated mask.");
            var box = observation["bbox_xyxy"] as JArray;
            UavObservationDocument.Require(box?.Count == 4 &&
                UavObservationDocument.Integer(box[0]) == x && UavObservationDocument.Integer(box[1]) == y &&
                UavObservationDocument.Integer(box[2]) == x + w && UavObservationDocument.Integer(box[3]) == y + h,
                "Bounding box does not match the cropped mask.");
            return new UavBinaryMask(w, h, x, y, pixels);
        }

        // Evenly sample foreground ranks across the whole crop, never bbox background.
        public IEnumerable<Vector2> ForegroundSamples(int requested)
        {
            int count=0;
            foreach(bool pixel in foreground) if(pixel) count++;
            int total=Math.Min(requested,count), rank=0, target=0;
            for(int i=0;i<foreground.Length && target<total;i++)
            {
                if(!foreground[i]) continue;
                if(rank==(int)Math.Floor((target+0.5)*count/total))
                { yield return new Vector2(X+i%Width+0.5f,Y+i/Width+0.5f); target++; }
                rank++;
            }
        }

        public Vector2 PointSample(JObject observation)
        {
            var point = observation["raycast_point_xy"] as JArray;
            UavObservationDocument.Require(point?.Count == 2, "Missing foreground raycast point.");
            int x = UavObservationDocument.Integer(point[0]), y = UavObservationDocument.Integer(point[1]);
            UavObservationDocument.Require(Contains(x - X, y - Y), "Raycast point is not in its mask.");
            // Perception indexes pixel centres by integer; viewport coordinates use pixel edges.
            return new Vector2(x + 0.5f, y + 0.5f);
        }
    }
}
