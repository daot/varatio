using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VARatio;


public class Config : BasePluginConfiguration
{
    public Config()
    {
        BlackFrameThreshold = 16;
        RatioTolerance = 0.05;
        MinSegmentDurationSeconds = 1;
        FfprobePath = string.Empty;
        FfmpegPath = string.Empty;
    }


    public int BlackFrameThreshold { get; set; }

    
    public double RatioTolerance { get; set; }

    
    public int MinSegmentDurationSeconds { get; set; }

    
    public string FfprobePath { get; set; }

    
    public string FfmpegPath { get; set; }
}
