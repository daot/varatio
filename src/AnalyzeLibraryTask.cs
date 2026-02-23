using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio;


public class AnalyzeLibraryTask : IScheduledTask
{
    private readonly ILogger<AnalyzeLibraryTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly AspectRatioAnalyzer _analyzer;

    public AnalyzeLibraryTask(
        ILogger<AnalyzeLibraryTask> logger,
        ILibraryManager libraryManager,
        AspectRatioAnalyzer analyzer)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _analyzer = analyzer;
    }

    public string Name => "Analyze Variable Aspect Ratios";
    public string Key => "task-varatio-analyze-all";
    public string Description => "Scans all movies for variable aspect ratios and generates .var files";
    public string Category => "VARatio";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No automatic trigger by default — user runs manually or post-scan runs automatically
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting VARatio analysis of all movies");

        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie],
            IsVirtualItem = false,
            Recursive = true,
        };

        var movies = _libraryManager.GetItemList(query);
        var totalMovies = movies.Count;
        var processed = 0;
        var varFilesWritten = 0;

        _logger.LogInformation("Found {Count} movies to analyze", totalMovies);

        foreach (var item in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                processed++;
                progress.Report((double)processed / totalMovies * 100);
                continue;
            }

            // Check if .var file already exists
            var varPath = Path.Combine(
                Path.GetDirectoryName(item.Path)!,
                Path.GetFileNameWithoutExtension(item.Path) + ".var");

            if (File.Exists(varPath))
            {
                _logger.LogDebug("Skipping {Name} — .var file already exists", item.Name);
                processed++;
                progress.Report((double)processed / totalMovies * 100);
                continue;
            }

            try
            {
                _logger.LogInformation("Analyzing: {Name}", item.Name);
                var result = await _analyzer.AnalyzeAsync(item.Path, cancellationToken);

                if (result.HasVariableRatios)
                {
                    await VarFileWriter.WriteAsync(item.Path, result, _logger, cancellationToken);
                    varFilesWritten++;
                    _logger.LogInformation(
                        "Found {Count} aspect ratio segments in {Name}",
                        result.Segments.Count, item.Name);
                }
                else
                {
                    _logger.LogInformation("No variable aspect ratios in {Name}", item.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing {Name}", item.Name);
            }

            processed++;
            progress.Report((double)processed / totalMovies * 100);
        }

        _logger.LogInformation(
            "VARatio analysis complete. Processed {Processed} movies, wrote {Written} .var files",
            processed, varFilesWritten);
    }
}
