using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio;


public class PostScanAnalyzeTask : ILibraryPostScanTask
{
    private readonly ILogger<PostScanAnalyzeTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly AspectRatioAnalyzer _analyzer;

    public PostScanAnalyzeTask(
        ILogger<PostScanAnalyzeTask> logger,
        ILibraryManager libraryManager,
        AspectRatioAnalyzer analyzer)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _analyzer = analyzer;
    }

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("VARatio post-scan: checking for new movies to analyze");

        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie],
            IsVirtualItem = false,
            Recursive = true,
        };

        var movies = _libraryManager.GetItemList(query);
        var analyzed = 0;
        var varFilesWritten = 0;

        foreach (var item in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                continue;
            }

            // Only analyze movies that don't already have a .var file
            var varPath = Path.Combine(
                Path.GetDirectoryName(item.Path)!,
                Path.GetFileNameWithoutExtension(item.Path) + ".var");

            if (File.Exists(varPath))
            {
                continue;
            }

            try
            {
                var result = await _analyzer.AnalyzeAsync(item.Path, cancellationToken);
                analyzed++;

                if (result.HasVariableRatios)
                {
                    await VarFileWriter.WriteAsync(item.Path, result, _logger, cancellationToken);
                    varFilesWritten++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Post-scan analysis error for {Name}", item.Name);
            }

            progress.Report((double)analyzed / movies.Count * 100);
        }

        _logger.LogInformation(
            "VARatio post-scan complete. Analyzed {Count} new movies, wrote {Written} .var files",
            analyzed, varFilesWritten);
    }
}
