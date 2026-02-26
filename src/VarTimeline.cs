using System.Collections.Concurrent;
using System.Globalization;

namespace Jellyfin.Plugin.VARatio;


/// <summary>
/// Parsed representation of a .var file suitable for building FFmpeg crop filters.
/// </summary>
public sealed class VarTimeline
{
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public IReadOnlyList<VarSegment> Segments { get; init; } = Array.Empty<VarSegment>();
}


public readonly record struct VarSegment(double StartTime, double AspectRatio);


/// <summary>
/// Loads and caches .var timelines from disk.
/// </summary>
public interface IVarTimelineProvider
{
    bool TryGetTimeline(string videoPath, out VarTimeline? timeline);
}


public sealed class VarTimelineProvider : IVarTimelineProvider
{
    private readonly ConcurrentDictionary<string, CachedTimeline> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetTimeline(string videoPath, out VarTimeline? timeline)
    {
        timeline = null;

        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (dir is null || baseName is null)
        {
            return false;
        }

        var varPath = Path.Combine(dir, baseName + ".var");
        if (!File.Exists(varPath))
        {
            return false;
        }

        var lastWrite = File.GetLastWriteTimeUtc(varPath);

        if (_cache.TryGetValue(varPath, out var cached) && cached.LastWriteUtc == lastWrite)
        {
            timeline = cached.Timeline;
            return true;
        }

        if (!TryParse(varPath, out var parsed))
        {
            return false;
        }

        _cache[varPath] = new CachedTimeline(parsed, lastWrite);
        timeline = parsed;
        return true;
    }

    private static bool TryParse(string varPath, out VarTimeline timeline)
    {
        timeline = new VarTimeline();

        var lines = File.ReadAllLines(varPath);
        if (lines.Length == 0)
        {
            return false;
        }

        int frameWidth = 0;
        int frameHeight = 0;
        var segments = new List<VarSegment>();

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw.StartsWith("FrameWidth:", StringComparison.OrdinalIgnoreCase))
            {
                var value = raw.Split(':', 2)[1].Trim();
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frameWidth);
                continue;
            }

            if (raw.StartsWith("FrameHeight:", StringComparison.OrdinalIgnoreCase))
            {
                var value = raw.Split(':', 2)[1].Trim();
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frameHeight);
                continue;
            }

            // Segment block:
            // <index>
            // Time: <start>
            // <AspectRatioLabel>  e.g. "2.39:1"
            if (IsAllDigits(raw) && i + 2 < lines.Length)
            {
                var timeLine = lines[i + 1].Trim();
                var ratioLine = lines[i + 2].Trim();

                if (!timeLine.StartsWith("Time:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var timeValue = timeLine.Split(':', 2)[1].Trim();
                if (!double.TryParse(timeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var start))
                {
                    continue;
                }

                if (!TryParseAspectRatio(ratioLine, out var ratio))
                {
                    continue;
                }

                segments.Add(new VarSegment(start, ratio));
                i += 2;
            }
        }

        if (frameWidth <= 0 || frameHeight <= 0 || segments.Count == 0)
        {
            return false;
        }

        segments.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

        timeline = new VarTimeline
        {
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            Segments = segments
        };

        return true;
    }

    private static bool TryParseAspectRatio(string label, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        label = label.Trim();

        // Accept either raw double (e.g. "2.39") or "<num>:<den>" (e.g. "2.39:1")
        if (double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio))
        {
            return ratio > 0;
        }

        var parts = label.Split(':', 2);
        if (parts.Length == 2 &&
            double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            ratio = num / den;
            return ratio > 0;
        }

        return false;
    }

    private static bool IsAllDigits(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private readonly record struct CachedTimeline(VarTimeline Timeline, DateTime LastWriteUtc);
}


/// <summary>
/// Helper for building FFmpeg crop filters from a VarTimeline.
/// </summary>
public static class FfmpegCropFilterBuilder
{
    public static string BuildCropFilter(VarTimeline timeline)
    {
        if (timeline.Segments.Count == 0)
        {
            return string.Empty;
        }

        int w = timeline.FrameWidth;
        int h = timeline.FrameHeight;

        // Build a filterchain that switches crop geometry based on time.
        // For example:
        // crop=w:h0:0:y0:enable='between(t,t0,t1)';crop=w:h1:0:y1:enable='between(t,t1,t2)';...
        var parts = new List<string>(timeline.Segments.Count);

        for (int i = 0; i < timeline.Segments.Count; i++)
        {
            var seg = timeline.Segments[i];
            var nextStart = (i + 1 < timeline.Segments.Count)
                ? timeline.Segments[i + 1].StartTime
                : double.MaxValue;

            // Center-crop height for desired aspect ratio.
            var targetRatio = seg.AspectRatio;
            if (targetRatio <= 0)
            {
                continue;
            }

            var desiredHeight = w / targetRatio;
            if (desiredHeight > h)
            {
                desiredHeight = h;
            }

            // Align to even integer to keep encoders happy.
            var hInt = (int)(Math.Round(desiredHeight / 2.0, MidpointRounding.AwayFromZero) * 2);
            if (hInt <= 0)
            {
                continue;
            }

            var y = (h - hInt) / 2;
            if (y < 0)
            {
                y = 0;
            }

            var enableExpr = BuildEnableExpression(seg.StartTime, nextStart);
            var filter = FormattableString.Invariant(
                $"crop={w}:{hInt}:0:{y}{enableExpr}");

            parts.Add(filter);
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(',', parts);
    }

    private static string BuildEnableExpression(double start, double nextStart)
    {
        const double epsilon = 0.0005;
        var s = Math.Max(start - epsilon, 0);

        if (double.IsPositiveInfinity(nextStart) || nextStart >= 1e9)
        {
            return FormattableString.Invariant($":enable='gte(t,{s.ToString("F6", CultureInfo.InvariantCulture)})'");
        }

        var e = Math.Max(nextStart - epsilon, s);
        return FormattableString.Invariant(
            $":enable='between(t,{s.ToString("F6", CultureInfo.InvariantCulture)},{e.ToString("F6", CultureInfo.InvariantCulture)})'");
    }
}

