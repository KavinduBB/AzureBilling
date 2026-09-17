using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mlcp.Web.Infrastructure;

namespace Mlcp.Web.Controllers;

/// <summary>
/// The landing page's region picker (ADR-021). Remembers the choice in a cookie on this host
/// and sends the visitor to the chosen regional stack.
/// </summary>
[AllowAnonymous]
public sealed class RegionController : Controller
{
    private readonly RegionPicker _regions;
    private readonly MlcpWebOptions _options;

    public RegionController(RegionPicker regions, MlcpWebOptions options)
    {
        _regions = regions;
        _options = options;
    }

    [HttpGet("region/{code}")]
    public IActionResult Choose(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32 || !_regions.IsKnown(code))
        {
            return NotFound();
        }

        Response.Cookies.Append(RegionPicker.PreferenceCookie, code.ToLowerInvariant(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(365),
        });

        if (string.Equals(code, _regions.CurrentRegion, StringComparison.OrdinalIgnoreCase))
        {
            return LocalRedirect("~/");
        }

        // Only configured hosts are reachable, so this is not an open redirect.
        return Redirect(_options.RegionUrl(code, "/")!.AbsoluteUri);
    }
}
