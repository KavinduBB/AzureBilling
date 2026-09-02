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
    private readonly ILogger<HomeController> _logger;

    public HomeController(IOnboardingRepository repository, ILogger<HomeController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// The landing page. Anonymous visitors get the marketing page; signed-in users go to their
    /// dashboard, or to onboarding when their tenant is not connected yet.
    /// </summary>
    /// <remarks>
    /// A signed-in user whose organisation has not connected sees the demo dashboard rather
    /// than a dead end, because the first person from an organisation to arrive is usually not
    /// an administrator and needs something to show their admin (ADR-008).
    /// </remarks>
    [AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return View("Landing");
        }

        var tenant = await _repository.FindTenantAsync(user.TenantId, cancellationToken);

        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return RedirectToAction("Index", "Onboarding");
        }

        if (tenant.Status == TenantStatus.NeedsReconsent)
        {
            _logger.LogInformation("Tenant {TenantId} needs re-consent; routing to onboarding.", user.TenantId);
            return RedirectToAction("Index", "Onboarding");
        }

        return View("Dashboard", tenant);
    }

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
