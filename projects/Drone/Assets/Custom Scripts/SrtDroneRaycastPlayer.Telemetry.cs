using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// File parsing and trajectory interpolation; shared by playback and projection.
public sealed partial class SrtDroneRaycastPlayer
{
    private static readonly Regex TimeRegex = new Regex(
        @"(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2}),(?<ms>\d{3})", RegexOptions.Compiled);
    private static readonly Regex FrameCntRegex = new Regex(
        @"FrameCnt\s*:\s*(?<v>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LatRegex = NumberRegex("latitude");
    private static readonly Regex LonRegex = NumberRegex("longitude");
    private static readonly Regex RelAltRegex = NumberRegex("rel_alt");
    private static readonly Regex AbsAltRegex = NumberRegex("abs_alt");
    private static readonly Regex YawRegex = NumberRegex("gb_yaw");
    private static readonly Regex PitchRegex = NumberRegex("gb_pitch");
    private static readonly Regex RollRegex = NumberRegex("gb_roll");
    private static readonly Regex FocalLenRegex = NumberRegex("focal_len");

    private static Regex NumberRegex(string fieldName)
    {
        return new Regex(
            Regex.Escape(fieldName) + @"\s*:\s*(?<v>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private void ParseSrt()
    {
        frames.Clear();
        frameIndexByCount.Clear();
        missingFrameCounts.Clear();
        hasDuplicateFrameCounts = false;
        if (!TryResolveInputPath(
                srtFilePath,
                srtPathJsonKey,
                out resolvedSrtPath,
                out string resolveError))
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Could not resolve SRT path: {resolveError}", this);
            return;
        }

        if (string.IsNullOrWhiteSpace(resolvedSrtPath) || !File.Exists(resolvedSrtPath))
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Missing SRT file: '{resolvedSrtPath}'.", this);
            return;
        }

        string srtText;
        try
        {
            srtText = File.ReadAllText(resolvedSrtPath);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Failed to read SRT '{resolvedSrtPath}': {exception.Message}", this);
            return;
        }

        string[] blocks = Regex.Split(srtText.Trim(), @"\r?\n\s*\r?\n");
        var seenFrameCounts = new HashSet<int>();
        for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            if (!TryParseSrtBlock(blocks[blockIndex], out SrtFrame frame, out string error))
            {
                Debug.LogError(
                    $"[SrtDroneRaycastPlayer] Malformed SRT block {blockIndex + 1} in '{resolvedSrtPath}': {error}",
                    this);
                continue;
            }

            if (!seenFrameCounts.Add(frame.frameCnt))
            {
                hasDuplicateFrameCounts = true;
                Debug.LogError(
                    $"[SrtDroneRaycastPlayer] Duplicate FrameCnt {frame.frameCnt} in '{resolvedSrtPath}'.", this);
                continue;
            }
            frames.Add(frame);
        }

        frames.Sort((left, right) => left.timeSeconds.CompareTo(right.timeSeconds));
        for (int i = 0; i < frames.Count; i++)
            frameIndexByCount[frames[i].frameCnt] = i;

        if (frames.Count == 0)
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] SRT '{resolvedSrtPath}' contains no valid frames.", this);
            return;
        }

        int maximumFrameCount = 0;
        for (int i = 0; i < frames.Count; i++)
            maximumFrameCount = Math.Max(maximumFrameCount, frames[i].frameCnt);
        for (int frameCnt = 1; frameCnt <= maximumFrameCount; frameCnt++)
        {
            if (!frameIndexByCount.ContainsKey(frameCnt))
                missingFrameCounts.Add(frameCnt);
        }
        if (missingFrameCounts.Count > 0)
            Debug.LogError(
                $"[SrtDroneRaycastPlayer] SRT '{resolvedSrtPath}' has {missingFrameCounts.Count} missing or malformed FrameCnt value(s).",
                this);

        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].timeSeconds <= frames[i - 1].timeSeconds)
                Debug.LogWarning(
                    $"[SrtDroneRaycastPlayer] Non-increasing SRT timestamps near FrameCnt {frames[i].frameCnt}.", this);
        }

        timeCursor = frames[0].timeSeconds;
        playbackFrameIndex = 0;
    }

    private static bool TryParseSrtBlock(string block, out SrtFrame frame, out string error)
    {
        frame = default;
        error = string.Empty;
        string[] lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            error = "missing subtitle timestamp or telemetry payload";
            return false;
        }

        Match timeMatch = TimeRegex.Match(lines[1]);
        string payload = Regex.Replace(string.Join(" ", lines), "<[^>]*>", " ");
        if (!timeMatch.Success)
            return Fail("missing or malformed SRT start timestamp", out frame, out error);
        if (!TryParseInt(FrameCntRegex, payload, out int frameCnt) || frameCnt <= 0)
            return Fail("missing or invalid FrameCnt", out frame, out error);
        if (!TryParseDouble(LatRegex, payload, out double latitude))
            return Fail("missing or invalid latitude", out frame, out error);
        if (!TryParseDouble(LonRegex, payload, out double longitude))
            return Fail("missing or invalid longitude", out frame, out error);
        if (!TryParseDouble(RelAltRegex, payload, out double relativeAltitude))
            return Fail("missing or invalid rel_alt", out frame, out error);
        if (!TryParseDouble(AbsAltRegex, payload, out double absoluteAltitude))
            return Fail("missing or invalid abs_alt", out frame, out error);
        if (!TryParseFloat(YawRegex, payload, out float yaw))
            return Fail("missing or invalid gb_yaw", out frame, out error);
        if (!TryParseFloat(PitchRegex, payload, out float pitch))
            return Fail("missing or invalid gb_pitch", out frame, out error);
        if (!TryParseFloat(RollRegex, payload, out float roll))
            return Fail("missing or invalid gb_roll", out frame, out error);
        if (!TryParseFloat(FocalLenRegex, payload, out float focalLength))
            return Fail("missing or invalid focal_len", out frame, out error);

        frame = new SrtFrame
        {
            frameCnt = frameCnt,
            timeSeconds = ParseTimeToSeconds(timeMatch),
            latitude = latitude,
            longitude = longitude,
            relativeAltitude = relativeAltitude,
            absoluteAltitude = absoluteAltitude,
            yaw = yaw,
            pitch = pitch,
            roll = roll,
            focalLengthMm = focalLength
        };
        return true;
    }

    private static bool Fail(string message, out SrtFrame frame, out string error)
    {
        frame = default;
        error = message;
        return false;
    }

    private static double ParseTimeToSeconds(Match match)
    {
        int hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        int milliseconds = int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture);
        return hours * 3600.0 + minutes * 60.0 + seconds + milliseconds / 1000.0;
    }

    private static bool TryParseInt(Regex regex, string input, out int value)
    {
        value = 0;
        Match match = regex.Match(input);
        return match.Success && int.TryParse(
            match.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFloat(Regex regex, string input, out float value)
    {
        value = 0f;
        Match match = regex.Match(input);
        return match.Success && float.TryParse(
            match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               IsFinite(value);
    }

    private static bool TryParseDouble(Regex regex, string input, out double value)
    {
        value = 0.0;
        Match match = regex.Match(input);
        return match.Success && double.TryParse(
            match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               IsFinite(value);
    }

    private SrtFrame GetInterpolatedFrame(double timeSeconds)
    {
        if (timeSeconds <= frames[0].timeSeconds)
            return frames[0];
        if (timeSeconds >= frames[frames.Count - 1].timeSeconds)
            return frames[frames.Count - 1];

        int index = GetFrameIndexForTime(timeSeconds);
        int nextIndex = Mathf.Min(index + 1, frames.Count - 1);
        SrtFrame left = frames[index];
        SrtFrame right = frames[nextIndex];
        double span = right.timeSeconds - left.timeSeconds;
        if (span <= 0.000001)
            return left;

        float t = (float)Math.Max(0.0, Math.Min(1.0, (timeSeconds - left.timeSeconds) / span));
        return new SrtFrame
        {
            frameCnt = left.frameCnt,
            timeSeconds = LerpDouble(left.timeSeconds, right.timeSeconds, t),
            latitude = LerpDouble(left.latitude, right.latitude, t),
            longitude = LerpDouble(left.longitude, right.longitude, t),
            relativeAltitude = LerpDouble(left.relativeAltitude, right.relativeAltitude, t),
            absoluteAltitude = LerpDouble(left.absoluteAltitude, right.absoluteAltitude, t),
            yaw = NormalizeDegrees(Mathf.LerpAngle(left.yaw, right.yaw, t)),
            pitch = NormalizeDegrees(Mathf.LerpAngle(left.pitch, right.pitch, t)),
            roll = NormalizeDegrees(Mathf.LerpAngle(left.roll, right.roll, t)),
            focalLengthMm = Mathf.Lerp(left.focalLengthMm, right.focalLengthMm, t)
        };
    }

    private int GetFrameIndexForTime(double timeSeconds)
    {
        int low = 0;
        int high = frames.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (frames[middle].timeSeconds <= timeSeconds)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return Mathf.Clamp(high, 0, frames.Count - 1);
    }

    private static double LerpDouble(double left, double right, float t)
    {
        return left + (right - left) * t;
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360f;
        if (value > 180f)
            value -= 360f;
        if (value <= -180f)
            value += 360f;
        return value;
    }

    [Serializable]
    private struct SrtFrame
    {
        public int frameCnt;
        public double timeSeconds;
        public double latitude;
        public double longitude;
        public double relativeAltitude;
        public double absoluteAltitude;
        public float yaw;
        public float pitch;
        public float roll;
        public float focalLengthMm;
    }

}
