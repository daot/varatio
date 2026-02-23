using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio;

public class WebInjector : IHostedService
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebInjector> _logger;
    private const string ScriptTag = "<script src=\"/VARatio/Player.js\"></script>";

    public WebInjector(IApplicationPaths appPaths, ILogger<WebInjector> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(_appPaths.WebPath, "index.html");
            if (File.Exists(indexPath))
            {
                var content = File.ReadAllText(indexPath);
                if (!content.Contains("/VARatio/Player.js"))
                {
                    _logger.LogInformation("Injecting VARatio Player.js into {IndexPath}", indexPath);
                    content = content.Replace("</body>", "    " + ScriptTag + "\n</body>");
                    File.WriteAllText(indexPath, content);
                }
            }
            else
            {
                _logger.LogWarning("Could not find index.html at {IndexPath} to inject VARatio script.", indexPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject VARatio Player.js into Web UI");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
