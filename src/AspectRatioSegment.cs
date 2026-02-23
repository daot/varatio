namespace Jellyfin.Plugin.VARatio;


public record AspectRatioSegment
{
    
    public double StartTime { get; init; }

    
    public double EndTime { get; init; }

    
    public double AspectRatio { get; init; }

    
    public string AspectRatioLabel { get; init; } = string.Empty;

    
    public double Duration => EndTime - StartTime;
}
