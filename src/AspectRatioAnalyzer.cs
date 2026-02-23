using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio;


public partial class AspectRatioAnalyzer
{
    private readonly ILogger<AspectRatioAnalyzer> _logger;

    private static readonly (double Ratio, string Label)[] KnownRatios =
    [
        (2.76, "2.76:1"), (2.55, "2.55:1"), (2.39, "2.39:1"), (2.35, "2.35:1"),
        (2.20, "2.20:1"), (2.00, "2.00:1"), (1.90, "1.90:1"), (1.85, "1.85:1"),
        (1.78, "1.78:1"), (1.66, "1.66:1"), (1.50, "1.50:1"), (1.43, "1.43:1"),
        (1.37, "1.37:1"), (1.33, "1.33:1"),
    ];

    public AspectRatioAnalyzer(ILogger<AspectRatioAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<AnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration ?? new Config();
        var ffprobePath = ResolveTool(config.FfprobePath, "ffprobe");
        var ffmpegPath = ResolveTool(config.FfmpegPath, "ffmpeg");
        var tolerance = config.RatioTolerance;

        // ── Step 1: Probe video metadata ────────────────────────────────
        var info = await ProbeVideoAsync(ffprobePath, filePath, ct);
        if (info is null)
        {
            _logger.LogError("Failed to probe {File}", filePath);
            return AnalysisResult.Empty;
        }

        _logger.LogInformation(
            "Probed {File}: {W}x{H}, {Dur:F1}s ({DurMin:F0} min)",
            Path.GetFileName(filePath), info.Width, info.Height,
            info.Duration, info.Duration / 60.0);

        var sw = Stopwatch.StartNew();

        // ── Step 2: Single-pass Frame Analysis ───────────────────────
        _logger.LogInformation("Starting single-pass frame analysis for {File}...", Path.GetFileName(filePath));
        var samples = await ProbeAllFramesAsync(
            ffmpegPath, filePath, info, config.BlackFrameThreshold, ct);

        samples.Sort((a, b) => a.Time.CompareTo(b.Time));

        _logger.LogInformation(
            "Collected {N} keyframe samples in {E:F1}s",
            samples.Count, sw.Elapsed.TotalSeconds);

        if (samples.Count == 0)
        {
            _logger.LogWarning("No valid samples for {File}", filePath);
            return AnalysisResult.Empty;
        }

        // Find dominant ratio
        var validSamples = samples.Where(s => s.Ratio > 0).ToList();
        if (validSamples.Count == 0)
        {
            _logger.LogWarning("No non-windowboxed samples found for {File}", filePath);
            return AnalysisResult.Empty;
        }

        var dominantLabel = validSamples
            .GroupBy(s => LabelFor(s.Ratio))
            .OrderByDescending(g => g.Count())
            .First().Key;
            
        double dominantRatio = validSamples
            .Where(s => LabelFor(s.Ratio) == dominantLabel)
            .Average(s => s.Ratio);

        // Replace unknown/text frames (-1) with dominant ratio
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Ratio < 0)
            {
                samples[i] = samples[i] with { Ratio = dominantRatio };
            }
        }

        // ── Step 3: Build and Merge Segments ─────────────────────────────
        var segments = BuildSegments(samples, info.Duration, tolerance);
        segments = MergeShortSegments(segments, config.MinSegmentDurationSeconds, tolerance);

        sw.Stop();
        _logger.LogInformation("Total analysis: {E:F1}s", sw.Elapsed.TotalSeconds);

        if (segments.Count <= 1)
        {
            _logger.LogInformation("Uniform aspect ratio in {File}", Path.GetFileName(filePath));
            return AnalysisResult.Empty;
        }

        _logger.LogInformation("{File}: {N} aspect ratio segments detected",
            Path.GetFileName(filePath), segments.Count);

        return new AnalysisResult
        {
            Segments = segments,
            FrameWidth = info.Width,
            FrameHeight = info.Height
        };
    }

    
    private async Task<VideoInfo?> ProbeVideoAsync(string ffprobe, string file, CancellationToken ct)
    {
        var args =
            $"-v error -select_streams v:0 " +
            $"-show_entries stream=width,height,duration " +
            $"-show_entries format=duration " +
            $"-of csv=p=0:s=, \"{file}\"";

        var output = await RunAsync(ffprobe, args, ct);
        if (string.IsNullOrWhiteSpace(output)) return null;

        int w = 0, h = 0;
        double dur = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out var pw) && int.TryParse(parts[1], out var ph))
            {
                w = pw; h = ph;
                if (parts.Length >= 3)
                    double.TryParse(parts[2], CultureInfo.InvariantCulture, out dur);
            }
            else if (double.TryParse(line.Trim(','), CultureInfo.InvariantCulture, out var fd) && dur <= 0)
            {
                dur = fd;
            }
        }

        return (w > 0 && h > 0 && dur > 0) ? new VideoInfo(w, h, dur) : null;
    }

    
    private async Task<List<Sample>> ProbeAllFramesAsync(
        string ffmpeg, string file, VideoInfo info, int blackThreshold, CancellationToken ct)
    {
        var limit = (blackThreshold / 255.0).ToString("F3", CultureInfo.InvariantCulture);

        var args =
            $"-nostdin -hwaccel videotoolbox -skip_frame noref -i \"{file}\" -an -sn -dn " +
            $"-vf \"format=yuv420p,cropdetect=limit={limit}:round=2:reset=1\" " +
            $"-f null -";

        var stderr = await RunAsync(ffmpeg, args, ct, readStdErr: true);
        if (string.IsNullOrWhiteSpace(stderr)) return [];

        var samples = new List<Sample>();
        double lastProgressReportTime = 0;

        foreach (Match m in CropRegex().Matches(stderr))
        {
            var t = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);

            if (t - lastProgressReportTime > 30.0)
            {
                _logger.LogInformation("  analyzed up to {T:F1}s / {Dur:F1}s ({Pct:F1}%)",
                    t, info.Duration, (t / info.Duration) * 100);
                lastProgressReportTime = t;
            }

            var cropW = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
            var cropH = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);

            // Pure black frame filter: if practically blank, skip
            if (cropW < 32 || cropH < 32)
                continue;

            // Small text filter: if the crop is very short, likely a title card
            if (cropH < info.Height * 0.15)
            {
                samples.Add(new Sample(t, -1));
                continue;
            }

            // Windowbox filter: Ignore frames that don't touch at least one pair of edges
            if (cropW < info.Width * 0.95 && cropH < info.Height * 0.95)
            {
                samples.Add(new Sample(t, -1));
                continue;
            }

            // Area filter: Small area is likely text
            double cropArea = cropW * cropH;
            double frameArea = info.Width * info.Height;
            if (cropArea < frameArea * 0.40)
            {
                samples.Add(new Sample(t, -1));
                continue;
            }

            samples.Add(new Sample(t, Math.Round((double)cropW / cropH, 2)));
        }

        return samples;
    }

    
    private List<AspectRatioSegment> BuildSegments(
        List<Sample> samples, double duration, double tolerance)
    {
        if (samples.Count == 0) return [];

        var segs = new List<AspectRatioSegment>();
        var curRatio = samples[0].Ratio;
        var segStart = 0.0;

        for (int i = 1; i < samples.Count; i++)
        {
            if (Math.Abs(samples[i].Ratio - curRatio) > tolerance)
            {
                segs.Add(MakeSeg(segStart, samples[i].Time, curRatio));
                curRatio = samples[i].Ratio;
                segStart = samples[i].Time;
            }
        }

        segs.Add(MakeSeg(segStart, duration, curRatio));
        return segs;
    }

    private List<AspectRatioSegment> MergeShortSegments(
        List<AspectRatioSegment> segs, int minDur, double tolerance)
    {
        if (segs.Count <= 1) return segs;

        bool changed;
        do
        {
            changed = false;
            for (int i = segs.Count - 1; i >= 0; i--)
            {
                if (segs[i].Duration < minDur)
                {
                    if (i > 0)
                        segs[i - 1] = segs[i - 1] with { EndTime = segs[i].EndTime };
                    else if (i < segs.Count - 1)
                        segs[i + 1] = segs[i + 1] with { StartTime = segs[i].StartTime };
                    segs.RemoveAt(i);
                    changed = true;
                }
            }
        } while (changed);

        var result = new List<AspectRatioSegment> { segs[0] };
        for (int i = 1; i < segs.Count; i++)
        {
            if (Math.Abs(segs[i].AspectRatio - result[^1].AspectRatio) <= tolerance)
                result[^1] = result[^1] with { EndTime = segs[i].EndTime };
            else
                result.Add(segs[i]);
        }
        return result;
    }

    
    private static AspectRatioSegment MakeSeg(double start, double end, double ratio) =>
        new()
        {
            StartTime = start,
            EndTime = end,
            AspectRatio = ratio,
            AspectRatioLabel = LabelFor(ratio)
        };

    private static string LabelFor(double ratio)
    {
        foreach (var (r, label) in KnownRatios)
            if (Math.Abs(ratio - r) < 0.08) return label;
        return $"{ratio:F2}:1";
    }

    private static string ResolveTool(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    
    private async Task<string> RunAsync(
        string exe, string args, CancellationToken ct, bool readStdErr = false)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        proc.Start();

        var wanted = readStdErr
            ? proc.StandardError.ReadToEndAsync(ct)
            : proc.StandardOutput.ReadToEndAsync(ct);
        var drain = readStdErr
            ? proc.StandardOutput.ReadToEndAsync(ct)
            : proc.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(wanted, drain);
        await proc.WaitForExitAsync(ct);
        return await wanted;
    }

    [GeneratedRegex(@"t:(?<t>[\d.]+)\s.*?crop=(?<w>\d+):(?<h>\d+):\d+:\d+", RegexOptions.Compiled)]
    private static partial Regex CropRegex();

    private record VideoInfo(int Width, int Height, double Duration);
    private record Sample(double Time, double Ratio);
}


public record AnalysisResult
{
    public List<AspectRatioSegment> Segments { get; init; } = [];
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public bool HasVariableRatios => Segments.Count > 1;
    public static AnalysisResult Empty => new();
}
