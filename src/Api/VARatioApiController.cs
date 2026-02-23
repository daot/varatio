using System.Net.Mime;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VARatio.Api;

[ApiController]
[Route("VARatio")]
public class VARatioApiController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VARatioApiController> _logger;

    public VARatioApiController(ILibraryManager libraryManager, ILogger<VARatioApiController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    [HttpGet("Data")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetData([FromQuery] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Item not found");
        }

        var varPath = Path.Combine(
            Path.GetDirectoryName(item.Path)!,
            Path.GetFileNameWithoutExtension(item.Path) + ".var");

        if (!System.IO.File.Exists(varPath))
        {
            return NotFound("VARatio data not found for this item");
        }

        var content = await System.IO.File.ReadAllTextAsync(varPath);
        return Content(content, MediaTypeNames.Text.Plain);
    }

    [HttpGet("Player.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPlayerScript()
    {
        var assembly = typeof(VARatioApiController).Assembly;
        var resourceName = "Jellyfin.Plugin.VARatio.Web.Player.js";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound("Player script not found in assembly resources");
        }

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        return Content(content, "application/javascript");
    }
}
