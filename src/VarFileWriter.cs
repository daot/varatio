using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio;


public static class VarFileWriter
{
    
    public static async Task WriteAsync(
        string videoFilePath,
        AnalysisResult result,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasVariableRatios)
        {
            return;
        }

        var dir = Path.GetDirectoryName(videoFilePath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoFilePath);
        var varFilePath = Path.Combine(dir, baseName + ".var");
        var sourceFileName = Path.GetFileName(videoFilePath);

        var sb = new StringBuilder();

        // Header
        sb.AppendLine("[VARatio v2]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"FrameWidth: {result.FrameWidth}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"FrameHeight: {result.FrameHeight}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"SourceFile: {sourceFileName}");
        sb.AppendLine();

        // Segments
        for (int i = 0; i < result.Segments.Count; i++)
        {
            var seg = result.Segments[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Time: {seg.StartTime:F6}");
            sb.AppendLine(seg.AspectRatioLabel);

            if (i < result.Segments.Count - 1)
            {
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(varFilePath, sb.ToString(), Encoding.UTF8, cancellationToken);
        logger.LogInformation("Wrote VARatio v2 file: {Path}", varFilePath);
    }
}
