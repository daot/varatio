using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VARatio;

public class WebInjector : IHostedService
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebInjector> _logger;

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
                var version = Guid.NewGuid().ToString("N");
                var scriptTag = $"<script src=\"/VARatio/Player.js?v={version}\"></script>";

                bool changed = false;

                // Remove any existing VARatio script tags to prevent duplicates and update versions
                var regex = new Regex(@"\s*<script src=""/VARatio/Player\.js[^""]*""></script>");
                if (regex.IsMatch(content))
                {
                    // If the exact current scriptTag isn't what's matched, we will replace it.
                    // Actually, easiest is to just remove all and re-inject.
                    content = regex.Replace(content, "");
                    changed = true;
                }

                if (!content.Contains(scriptTag))
                {
                    _logger.LogInformation("Injecting VARatio Player.js (v{Version}) into {IndexPath}", version, indexPath);
                    content = content.Replace("</body>", "    " + scriptTag + "\n</body>");
                    changed = true;
                }

                if (changed)
                {
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
