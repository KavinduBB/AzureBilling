using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;

namespace Mlcp.Web.Controllers;

/// <summary>
/// The onboarding state machine's web surface (docs/03-architecture.md §3).
/// </summary>
/// <remarks>
/// Thin by policy: every decision lives in <see cref="OnboardingService"/> and
/// <see cref="CapabilityDiscoveryService"/>. What this type owns is the HTTP-specific work —
/// reading claims, validating the consent state, and choosing a view.
/// </remarks>
[Authorize]
[Route("onboarding")]
public sealed class OnboardingController : Controller
{
    private readonly OnboardingService _onboarding;
    private readonly CapabilityDiscoveryService _discovery;
    private readonly ITenantOnboardingStore _store;
    private readonly IOnboardingRepository _repository;
    private readonly AdminConsentUrlBuilder _consentUrlBuilder;
    private readonly DeploymentOptions _deployment;
    private readonly ILogger<OnboardingController> _logger;

    public OnboardingController(
        OnboardingService onboarding,
        CapabilityDiscoveryService discovery,
        ITenantOnboardingStore store,
        IOnboardingRepository repository,
        AdminConsentUrlBuilder consentUrlBuilder,
        DeploymentOptions deployment,
        ILogger<OnboardingController> logger)
    {
        _onboarding = onboarding;
        _discovery = discovery;
        _store = store;
        _repository = repository;
        _consentUrlBuilder = consentUrlBuilder;
        _deployment = deployment;
        _logger = logger;
    }

    /// <summary>The connection status page: either "not connected" or the unlock checklist.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        var state = await _onboarding.RecordSignInAsync(user, _deployment.Region, cancellationToken);

        if (state.Tenant is null || state.Tenant.ConsentGrantedUtc is null)
        {
            return View("NotConnected", new NotConnectedViewModel(
                user.DisplayName,
                state.PendingRequest?.SentToEmail,
                state.PendingRequest?.ExpiresUtc));
        }

        var profile = await _store.FindCapabilityProfileAsync(user.TenantId, cancellationToken);

        if (profile is null)
        {
            // Consent is recorded but discovery has not run yet. Saying so is more honest than
            // rendering a checklist of unknowns, which would read as a list of failures.
            return View("Provisioning");
        }

        return View("Checklist", OnboardingChecklist.Build(state.Tenant, profile));
    }

    /// <summary>Sends an administrator to the Entra tenant-wide consent screen.</summary>
    [HttpPost("connect")]
    [ValidateAntiForgeryToken]
    public IActionResult Connect()
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        var redirectUri = Url.Action(nameof(ConsentCallback), "Onboarding", values: null, protocol: Request.Scheme)
            ?? throw new InvalidOperationException("Could not build the consent callback URL.");

        return Redirect(_consentUrlBuilder.Build(user.TenantId, redirectUri));
    }

    /// <summary>
    /// Where Entra returns after an administrator accepts or declines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Microsoft includes a <c>tenant</c> parameter here. It is deliberately ignored: the
    /// tenant marked as consented comes from the returning administrator's own validated token.
    /// Trusting the query string would let anyone mark an arbitrary tenant connected by
    /// crafting a URL.
    /// </para>
    /// <para>
    /// The protected <c>state</c> is checked as a correlation and replay guard. A mismatch is
    /// logged rather than silently accepted: it means a stale link, or an attempt to splice one
    /// tenant's consent onto another's session.
    /// </para>
    /// </remarks>
    [HttpGet("consent-callback")]
    public async Task<IActionResult> ConsentCallback(
        [FromQuery(Name = "admin_consent")] string? adminConsent,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var admin))
        {
            return Challenge();
        }

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Admin consent was declined or failed: {Error}.", error);
            return View("ConsentDeclined", new ConsentDeclinedViewModel(error, errorDescription));
        }

        if (!string.Equals(adminConsent, "True", StringComparison.OrdinalIgnoreCase))
        {
            return View("ConsentDeclined", new ConsentDeclinedViewModel(
                "no_consent",
                "Consent was not granted, so nothing has changed."));
        }

        if (!_consentUrlBuilder.TryUnprotectState(state, out var initiatingTenantId))
        {
            _logger.LogWarning("Admin consent callback carried an invalid or expired state; refusing.");

            return View("ConsentDeclined", new ConsentDeclinedViewModel(
                "invalid_state",
                "This consent link has expired. Please start again from the connect page."));
        }

        if (initiatingTenantId != admin.TenantId)
        {
            _logger.LogWarning(
                "Consent callback state names tenant {StateTenantId} but the administrator is in {AdminTenantId}. Refusing.",
                initiatingTenantId,
                admin.TenantId);

            return View("ConsentDeclined", new ConsentDeclinedViewModel(
                "tenant_mismatch",
                "This consent link was started from a different organisation."));
        }

        await _onboarding.CompleteAdminConsentAsync(admin, _deployment.Region, cancellationToken);

        // Discovery is one of the few places a request may call Microsoft (CLAUDE.md rule 5),
        // and it is what turns a consent into a usable connection. Running it here means the
        // admin sees a real result rather than a page telling them to come back later.
        await _discovery.DiscoverAsync(admin.TenantId, cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Emails an administrator asking them to connect the tenant.</summary>
    [HttpPost("request-consent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestConsent(
        [FromForm] string adminEmail,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(adminEmail) || !adminEmail.Contains('@', StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(adminEmail), "Enter your administrator's email address.");
            return View("NotConnected", new NotConnectedViewModel(user.DisplayName, null, null));
        }

        var request = await _onboarding.RequestAdminConsentAsync(
            user,
            adminEmail,
            pending => Url.Action(
                nameof(ConsentLanding),
                "Onboarding",
                new { token = pending.Token },
                Request.Scheme)!,
            cancellationToken);

        TempData["ConsentRequestSentTo"] = request.SentToEmail;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Where an emailed administrator lands.
    /// </summary>
    /// <remarks>
    /// The token identifies which request this is; it grants nothing. The administrator still
    /// signs in and completes the Entra consent screen before any permission exists, so a
    /// leaked link discloses only that someone asked for a connection.
    /// </remarks>
    [HttpGet("consent/{token}")]
    public async Task<IActionResult> ConsentLanding(string token, CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out _))
        {
            return Challenge();
        }

        var request = await _repository.FindConsentRequestByTokenAsync(token, cancellationToken);

        if (request is null)
        {
            return View("ConsentLinkExpired");
        }

        return View("ConsentLanding", new ConsentLandingViewModel(request.RequestedByUpn, request.ExpiresUtc));
    }

    /// <summary>Begins the 30-day deletion clock for this tenant.</summary>
    [HttpPost("disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        var appUser = await _repository.FindAppUserAsync(user.TenantId, user.ObjectId, cancellationToken);

        // Disconnecting destroys the whole organisation's data. Only an Owner may start it.
        if (appUser is null || appUser.Role != AppRole.Owner)
        {
            return Forbid();
        }

        var tenant = await _onboarding.DisconnectAsync(user, cancellationToken);

        return View("Disconnected", new DisconnectedViewModel(tenant.DeleteScheduledUtc));
    }
}
