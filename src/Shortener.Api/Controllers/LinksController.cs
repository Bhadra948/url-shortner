using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shortener.Api.Contracts;
using Shortener.Api.Services;

namespace Shortener.Api.Controllers;

/// <summary>
/// Controller for managing short links and their analytics.
/// </summary>      
[ApiController]
[Route("api/links")]
public sealed class LinksController(ShortLinkService links) : ControllerBase
{   /// <summary>
    /// Retrieves a list of recent short links.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A response containing a list of recent short links.</returns>  
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ShortLinkListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recent(CancellationToken cancellationToken) =>
        Ok(await links.GetRecentAsync(cancellationToken));

    [HttpPost]
    [EnableRateLimiting("create-link")]
    [ProducesResponseType<ShortLinkResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateShortLinkRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var link = await links.CreateAsync(request, cancellationToken);
            var response = ToResponse(link.Code, link.TargetUrl, link.CreatedAtUtc, link.ExpiresAtUtc);
            return Created($"/api/links/{link.Code}", response);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid link", Detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails { Title = "Link conflict", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Retrieves a short link by its code.
    /// </summary>
    /// <param name="code">The unique code of the short link.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param    
    [HttpGet("{code}")]
    [ProducesResponseType<ShortLinkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string code, CancellationToken cancellationToken)
    {
        var link = await links.GetByCodeAsync(code, cancellationToken);
        return link is null
            ? NotFound()
            : Ok(ToResponse(link.Code, link.TargetUrl, link.CreatedAtUtc, link.ExpiresAtUtc));
    }

    [HttpGet("{code}/analytics")]
    [ProducesResponseType<LinkAnalyticsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Analytics(string code, CancellationToken cancellationToken)
    {
        var analytics = await links.GetAnalyticsAsync(code, cancellationToken);
        return analytics is null ? NotFound() : Ok(analytics);
    }


    /// <summary>
    /// Converts the short link data into a response object.
    /// </summary>
    /// <param name="code">The unique code of the short link.</param>
    /// <param name="targetUrl">The target URL of the short link.</param>
    /// <param name="createdAtUtc">The creation timestamp of the short link in UTC.</param>
    /// <param name="expiresAtUtc">The expiration timestamp of the short link in UTC, if applicable.</param>
    /// <returns>A ShortLinkResponse object containing the short
    private ShortLinkResponse ToResponse(
        string code,
        string targetUrl,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc) =>
        new(code, targetUrl, $"{Request.Scheme}://{Request.Host}/{code}", createdAtUtc, expiresAtUtc);
}