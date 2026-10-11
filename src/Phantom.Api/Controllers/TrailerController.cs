using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Phantom.Api.Controllers;

/// <summary>
/// A page holding YouTube's embedded player, which the apps open in a WebView to play trailers in place. YouTube's
/// player won't play inside a page with no web origin (an app's own HTML), so it is served from here.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("trailer")]
public partial class TrailerController : ControllerBase
{
    [HttpGet("{videoId}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Page(string videoId)
    {
        if (!VideoIdRegex().IsMatch(videoId)) return NotFound();

        var html = $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="referrer" content="strict-origin-when-cross-origin">
              <title>Trailer</title>
              <style>html, body { margin: 0; height: 100%; background: #000; overflow: hidden; } iframe { border: 0; width: 100%; height: 100%; }</style>
            </head>
            <body>
              <iframe src="https://www.youtube-nocookie.com/embed/{{videoId}}?autoplay=1&playsinline=1&rel=0&modestbranding=1"
                      allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen
                      referrerpolicy="strict-origin-when-cross-origin"></iframe>
            </body>
            </html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }

    // YouTube video ids: 11 characters of A-Z, a-z, 0-9, - and _.
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")] private static partial Regex VideoIdRegex();
}
