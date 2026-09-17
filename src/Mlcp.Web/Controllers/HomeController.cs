using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;

namespace Mlcp.Web.Controllers;

public sealed class HomeController : Controller
{
    private readonly IOnboardingRepository _repository;
    private readonly RegionPicker _regions;

    public HomeController(IOnboardingRepository repository, RegionPicker regions)
    {
        _repository = repository;
        _regions = regions;
    }

    /// <summary>
    /// The landing page. Anonymous visitors get the marketing page; signed-in users go to their
    /// dashboard when the tenant is active, and to the connection page otherwise — which covers
    /// not connected, finishing, needs re-consent and disconnected alike.
    /// </summary>
    [AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            Request.Cookies.TryGetValue(RegionPicker.PreferenceCookie, out var preferred);
            return View("Landing", _regions.Landing(preferred));
        }

        var tenant = await _repository.FindTenantAsync(user.TenantId, cancellationToken);

        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return RedirectToAction("Index", "Onboarding");
        }

        return View("Dashboard", tenant);
    }

    /// <summary>
    /// Manual price entry (ADR-006) arrives in Phase 1. The checklist links here, so the link says
    /// so instead of failing.
    /// </summary>
    [HttpGet("prices")]
    public IActionResult Prices() => View("PricesComingSoon");

    [AllowAnonymous]
    public IActionResult Privacy() => View();

    [AllowAnonymous]
    [Route("dpa")]
    public IActionResult DataProcessing() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
