using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;

namespace Mlcp.Web.Controllers;

/// <summary>
/// The onboarding state machine's web surface (docs/03-architecture.md §3, ADR-018).
/// </summary>
/// <remarks>
/// Thin by policy: decisions live in <see cref="OnboardingService"/>. What this type owns is the
/// HTTP-specific work — reading the live claims, issuing and checking the consent state,
/// step-up authentication, and choosing a view. No Microsoft call is made here except through
/// <see cref="OnboardingService.CompleteAdminConsentAsync"/>'s single verification.
/// </remarks>
[Authorize]
[Route("onboarding")]
public sealed class OnboardingController : Controller
{
    private const string MessageKey = "OnboardingMessage";

    private readonly OnboardingService _onboarding;
    private readonly ITenantOnboardingStore _store;
    private readonly IOnboardingRepository _repository;
    private readonly AdminConsentUrlBuilder _consentUrls;
    private readonly ConsentStateProtector _consentState;
    private readonly RegionPicker _regions;
    private readonly MlcpWebOptions _options;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OnboardingController> _logger;

    public OnboardingController(
        OnboardingService onboarding,
        ITenantOnboardingStore store,
        IOnboardingRepository repository,
        AdminConsentUrlBuilder consentUrls,
        ConsentStateProtector consentState,
        RegionPicker regions,
        MlcpWebOptions options,
        IMemoryCache cache,
        TimeProvider timeProvider,
        ILogger<OnboardingController> logger)
    {
        _onboarding = onboarding;
        _store = store;
        _repository = repository;
        _consentUrls = consentUrls;
        _consentState = consentState;
        _regions = regions;
        _options = options;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The connection status page, chosen by the tenant's state.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        var state = await _onboarding.RecordSignInAsync(user, _regions.CurrentRegion, cancellationToken);
        var tenant = state.Tenant!;

        switch (tenant.Status)
        {
            case TenantStatus.GracePeriod:
                return View("Disconnected", new DisconnectedViewModel(tenant.DeleteScheduledUtc, user.IsDirectoryAdmin));

            case TenantStatus.ConsentPendingVerification:
                return View("FinishingConnection", new FinishingConnectionViewModel(user.IsDirectoryAdmin, _regions.Current()));

            case TenantStatus.NeedsReconsent:
                return View("NeedsReconsent", new NeedsReconsentViewModel(
                    tenant.TenantId,
                    DescribeReconsentReason(tenant.NeedsReconsentReason),
                    tenant.NeedsReconsentSinceUtc,
                    user.IsDirectoryAdmin,
                    _regions.Current()));

            case TenantStatus.Provisioning or TenantStatus.Active:
                var profile = await _store.FindCapabilityProfileAsync(user.TenantId, cancellationToken);

                if (profile is null)
                {
                    // Consent is verified but discovery has not run yet. Saying so is more honest
                    // than rendering a checklist of unknowns, which would read as failures.
                    return View("Provisioning");
                }

                return View("Checklist", new ChecklistViewModel(
                    OnboardingChecklist.Build(tenant, profile),
                    user.IsDirectoryAdmin,
                    _consentUrls.HasUsageInsights));

            default:
                return View("NotConnected", new NotConnectedViewModel(
                    user.DisplayName,
                    user.IsDirectoryAdmin,
                    _regions.Current(),
                    state.PendingRequest?.SentToEmail,
                    state.PendingRequest?.ExpiresUtc));
        }
    }

    /// <summary>
    /// Sends a directory administrator to the Entra tenant-wide consent screen, after they have
    /// confirmed the region their organisation's data will live in.
    /// </summary>
    [HttpPost("connect")]
    [EnableRateLimiting(RateLimitPolicies.Connect)]
    public async Task<IActionResult> Connect([FromForm] string? confirmRegion, CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (!user.IsDirectoryAdmin)
        {
            // Only admins can start admin consent; everyone else gets "ask my admin" (ADR-018).
            return Forbid();
        }

        if (!string.Equals(confirmRegion, _regions.CurrentRegion, StringComparison.OrdinalIgnoreCase))
        {
            TempData[MessageKey] = "Please confirm where your organisation's data will be stored before connecting.";
            return RedirectToAction(nameof(Index));
        }

        var correlationId = HttpContext.TraceIdentifier is { Length: > 0 and <= 64 } trace
            ? trace
            : Guid.NewGuid().ToString("N");

        var outcome = await _onboarding.BeginConnectAsync(user, _regions.CurrentRegion, correlationId, cancellationToken);
        RegionRoutingMiddleware.Invalidate(_cache, user.TenantId);

        switch (outcome.Decision)
        {
            case ConnectDecision.NotDirectoryAdmin:
                return Forbid();

            case ConnectDecision.Disconnected:
                return RedirectToAction(nameof(Index));

            case ConnectDecision.RegisteredElsewhere:
                await RegionRoutingMiddleware.RedirectToRegionAsync(HttpContext, _options, outcome.Region, _logger);
                return new EmptyResult();
        }

        var state = await _consentState.CreateAsync(user, outcome.Region, correlationId, ConsentFlow.Core, cancellationToken);

        return Redirect(_consentUrls.Build(user.TenantId, CallbackUrl(nameof(ConsentCallback)), state));
    }

    /// <summary>
    /// Where Entra returns after an administrator accepts or declines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the query string is trusted. The state must be one this deployment issued to
    /// this person in this tenant within ten minutes, and it works once. Microsoft's
    /// <c>tenant</c> parameter must match the caller's <c>tid</c>; the tenant acted on is still
    /// the token's. Consent is then verified with an app-only call before anything is recorded
    /// as granted.
    /// </para>
    /// <para>
    /// Error text from the query string is never shown; each failure maps to a fixed message.
    /// </para>
    /// </remarks>
    [HttpGet("consent-callback")]
    public async Task<IActionResult> ConsentCallback(
        [FromQuery(Name = "admin_consent")] string? adminConsent,
        [FromQuery(Name = "tenant")] string? consentTenant,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var admin))
        {
            return Challenge();
        }

        var check = await ValidateCallbackAsync(admin, adminConsent, consentTenant, state, error, ConsentFlow.Core, cancellationToken);

        if (check.Failure is { } failure)
        {
            return View("ConsentFailed", new ConsentFailedViewModel(failure));
        }

        var consentState = check.State!;
        var result = await _onboarding.CompleteAdminConsentAsync(admin, consentState.Region, consentState.CorrelationId, cancellationToken);

        return result switch
        {
            ConsentCallbackResult.Verified => RedirectToAction(nameof(Index)),
            ConsentCallbackResult.PendingVerification =>
                View("FinishingConnection", new FinishingConnectionViewModel(admin.IsDirectoryAdmin, _regions.Current())),
            ConsentCallbackResult.NotGranted => View("ConsentFailed", new ConsentFailedViewModel(ConsentFailureReason.NotGranted)),
            ConsentCallbackResult.CouldNotConfirm =>
                View("ConsentFailed", new ConsentFailedViewModel(ConsentFailureReason.CouldNotConfirm)),
            ConsentCallbackResult.NotDirectoryAdmin =>
                View("ConsentFailed", new ConsentFailedViewModel(ConsentFailureReason.NotAdministrator)),
            _ => RedirectToAction(nameof(Index)),
        };
    }

    /// <summary>Sends a directory administrator to the separate Usage Insights consent (Guide D, ADR-015).</summary>
    [HttpPost("connect-usage-insights")]
    [EnableRateLimiting(RateLimitPolicies.Connect)]
    public async Task<IActionResult> ConnectUsageInsights(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (!user.IsDirectoryAdmin)
        {
            return Forbid();
        }

        if (!_consentUrls.HasUsageInsights)
        {
            return NotFound();
        }

        var tenant = await _repository.FindTenantAsync(user.TenantId, cancellationToken);

        if (tenant?.Status is not (TenantStatus.Active or TenantStatus.Provisioning))
        {
            TempData[MessageKey] = "Connect your organisation first; usage insights build on that connection.";
            return RedirectToAction(nameof(Index));
        }

        var state = await _consentState.CreateAsync(
            user,
            tenant.Region,
            Guid.NewGuid().ToString("N"),
            ConsentFlow.UsageInsights,
            cancellationToken);

        return Redirect(_consentUrls.BuildUsageInsights(user.TenantId, CallbackUrl(nameof(UsageInsightsCallback)), state));
    }

    /// <summary>
    /// Where Entra returns after the Usage Insights consent. Nothing is concluded here: the
    /// capability is re-probed by queued discovery with the Usage Insights token.
    /// </summary>
    [HttpGet("usage-insights-callback")]
    public async Task<IActionResult> UsageInsightsCallback(
        [FromQuery(Name = "admin_consent")] string? adminConsent,
        [FromQuery(Name = "tenant")] string? consentTenant,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var admin))
        {
            return Challenge();
        }

        var check = await ValidateCallbackAsync(
            admin,
            adminConsent,
            consentTenant,
            state,
            error,
            ConsentFlow.UsageInsights,
            cancellationToken);

        if (check.Failure is { } failure)
        {
            return View("ConsentFailed", new ConsentFailedViewModel(failure));
        }

        await _onboarding.RecordUsageInsightsConsentAsync(admin, check.State!.CorrelationId, cancellationToken);

        TempData[MessageKey] = "Thanks. We are checking the usage insights permission now; this page updates within a few minutes.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Emails an administrator asking them to connect the tenant.</summary>
    [HttpPost("request-consent")]
    [EnableRateLimiting(RateLimitPolicies.ConsentRequest)]
    public async Task<IActionResult> RequestConsent([FromForm] string? adminEmail, CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        var publicBase = PublicBaseUrl();

        var outcome = await _onboarding.RequestAdminConsentAsync(
            user,
            adminEmail,
            pending => new Uri(publicBase, "onboarding/consent/" + Uri.EscapeDataString(pending.Token)).AbsoluteUri,
            cancellationToken);

        if (outcome.IsSent)
        {
            TempData["ConsentRequestSentTo"] = outcome.Request!.SentToEmail;
        }
        else
        {
            TempData[MessageKey] = DescribeRefusal(outcome);
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Where an emailed administrator lands.
    /// </summary>
    /// <remarks>
    /// The token identifies which request this is; it grants nothing. The administrator still
    /// signs in and completes the Entra consent screen before any permission exists. The token
    /// segment is kept out of request logs (see the request-logging configuration).
    /// </remarks>
    [HttpGet("consent/{token}")]
    public async Task<IActionResult> ConsentLanding(string token, CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        var request = await _repository.FindConsentRequestByTokenAsync(token, cancellationToken);

        if (request is null)
        {
            return View("ConsentLinkExpired");
        }

        return View("ConsentLanding", new ConsentLandingViewModel(
            request.RequestedByUpn,
            request.ExpiresUtc,
            user.IsDirectoryAdmin,
            _regions.Current()));
    }

    /// <summary>The disconnect confirmation page. Admin-only, after a fresh sign-in.</summary>
    [HttpGet("disconnect")]
    public async Task<IActionResult> ConfirmDisconnect(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (!user.IsDirectoryAdmin)
        {
            return Forbid();
        }

        if (!RecentAuthentication.IsRecent(User, _timeProvider.GetUtcNow()))
        {
            return Challenge(RecentAuthentication.ChallengeProperties(Url.Action(nameof(ConfirmDisconnect))!));
        }

        var tenant = await _repository.FindTenantAsync(user.TenantId, cancellationToken);

        if (tenant is null || tenant.Status is TenantStatus.GracePeriod or TenantStatus.Deleted)
        {
            return RedirectToAction(nameof(Index));
        }

        return View("DisconnectConfirm", new DisconnectConfirmViewModel(
            tenant.DisplayName,
            (int)OnboardingService.DeletionGracePeriod.TotalDays));
    }

    /// <summary>
    /// Begins the 30-day deletion clock for this tenant. Requires a live admin claim and a
    /// re-authentication within 15 minutes (ADR-018).
    /// </summary>
    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect([FromForm] bool confirmed, CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (!user.IsDirectoryAdmin)
        {
            return Forbid();
        }

        if (!RecentAuthentication.IsRecent(User, _timeProvider.GetUtcNow()))
        {
            return Challenge(RecentAuthentication.ChallengeProperties(Url.Action(nameof(ConfirmDisconnect))!));
        }

        if (!confirmed)
        {
            return RedirectToAction(nameof(ConfirmDisconnect));
        }

        var tenant = await _onboarding.DisconnectAsync(user, cancellationToken);

        return View("Disconnected", new DisconnectedViewModel(tenant.DeleteScheduledUtc, user.IsDirectoryAdmin));
    }

    /// <summary>
    /// Reverses a disconnect during the grace period — the only way a scheduled deletion is
    /// cancelled. Same gates as disconnect.
    /// </summary>
    [HttpPost("cancel-disconnect")]
    public async Task<IActionResult> CancelDisconnect(CancellationToken cancellationToken)
    {
        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (!user.IsDirectoryAdmin)
        {
            return Forbid();
        }

        if (!RecentAuthentication.IsRecent(User, _timeProvider.GetUtcNow()))
        {
            TempData[MessageKey] = "You signed in again. Select \"Keep my organisation connected\" once more to confirm.";
            return Challenge(RecentAuthentication.ChallengeProperties(Url.Action(nameof(Index))!));
        }

        await _onboarding.CancelDisconnectAsync(user, cancellationToken);

        TempData[MessageKey] = "The disconnect was cancelled. Your data will not be deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>The Owner's "Check again" on a tenant that needs re-consent (ADR-016 rule 5).</summary>
    [HttpPost("check-again")]
    [EnableRateLimiting(RateLimitPolicies.CheckAgain)]
    public async Task<IActionResult> CheckAgain([FromForm] CheckAgainForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!User.TryGetSignedInUser(out var user))
        {
            return Challenge();
        }

        if (!user.IsDirectoryAdmin)
        {
            return Forbid();
        }

        var queued = await _onboarding.RequestReconsentProbeAsync(user, cancellationToken);

        TempData[MessageKey] = queued
            ? "We are checking the connection again. This page updates within a few minutes."
            : "Your organisation's connection does not need checking right now.";

        return RedirectToAction(nameof(Index));
    }

    private async Task<(ConsentFailureReason? Failure, ConsentState? State)> ValidateCallbackAsync(
        SignedInUser admin,
        string? adminConsent,
        string? consentTenant,
        string? state,
        string? error,
        ConsentFlow flow,
        CancellationToken cancellationToken)
    {
        // The nonce is consumed whatever happens next, so a returned state never works twice.
        var validation = await _consentState.ConsumeAsync(state, admin, flow, cancellationToken);

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Admin consent ({Flow}) returned an error for tenant {TenantId}.", flow, admin.TenantId);

            return (
                string.Equals(error, "access_denied", StringComparison.Ordinal)
                    ? ConsentFailureReason.Declined
                    : ConsentFailureReason.EntraError,
                null);
        }

        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Admin consent callback ({Flow}) for tenant {TenantId} refused: state {StateStatus}.",
                flow,
                admin.TenantId,
                validation.Status);

            return (
                validation.Status == ConsentStateStatus.WrongTenant
                    ? ConsentFailureReason.TenantMismatch
                    : ConsentFailureReason.InvalidOrExpired,
                null);
        }

        if (!string.Equals(adminConsent, "True", StringComparison.OrdinalIgnoreCase))
        {
            return (ConsentFailureReason.Declined, null);
        }

        if (!Guid.TryParse(consentTenant, out var reportedTenant) || reportedTenant != admin.TenantId)
        {
            _logger.LogWarning(
                "Admin consent callback ({Flow}) for tenant {TenantId} named a different or missing tenant; refusing.",
                flow,
                admin.TenantId);

            return (ConsentFailureReason.TenantMismatch, null);
        }

        if (!admin.IsDirectoryAdmin)
        {
            return (ConsentFailureReason.NotAdministrator, null);
        }

        return (null, validation.State);
    }

    private string CallbackUrl(string action)
        => Url.Action(action, "Onboarding", values: null, protocol: Request.Scheme)
            ?? throw new InvalidOperationException("Could not build the consent callback URL.");

    /// <summary>
    /// The origin for links that leave the request. Configured in every deployed environment
    /// (enforced at startup); Development falls back to the request's own origin.
    /// </summary>
    private Uri PublicBaseUrl()
        => _options.PublicBaseUrl ?? new Uri($"{Request.Scheme}://{Request.Host}/");

    private static string DescribeRefusal(ConsentRequestOutcome outcome) => outcome.Refusal switch
    {
        ConsentRequestRefusal.InvalidAddress => "Enter a single work email address for your administrator.",
        ConsentRequestRefusal.DomainNotAllowed =>
            "The address must belong to your organisation. Use your administrator's work address in one of your organisation's domains.",
        ConsentRequestRefusal.Cooldown => "A request was sent a few minutes ago. Please wait 15 minutes before sending another.",
        ConsentRequestRefusal.UserDailyLimit => "You have sent the maximum number of requests for today. Please try again tomorrow.",
        ConsentRequestRefusal.TenantDailyLimit =>
            "Your organisation has sent the maximum number of requests for today. Please try again tomorrow.",
        ConsentRequestRefusal.AlreadyConnected => "Your organisation is already connected.",
        _ => "The request could not be sent.",
    };

    private static string DescribeReconsentReason(string? reason)
        => reason is not null && reason.StartsWith("FloorPermissionRemoved", StringComparison.OrdinalIgnoreCase)
            ? "A permission MLCP needs to read your licences was removed from the MLCP application in your organisation."
            : "MLCP's access to your organisation was removed or disabled, so synchronisation has stopped.";
}
