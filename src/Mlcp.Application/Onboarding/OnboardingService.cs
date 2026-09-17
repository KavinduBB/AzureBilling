using Microsoft.Extensions.Logging;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>Identity of the person driving an onboarding action.</summary>
/// <param name="TenantId">Their Entra tenant, from the validated <c>tid</c> claim.</param>
/// <param name="ObjectId">Their Entra object id.</param>
/// <param name="Upn">Their user principal name.</param>
/// <param name="DisplayName">Their display name.</param>
public sealed record SignedInUser(Guid TenantId, Guid ObjectId, string Upn, string DisplayName);

/// <summary>What the UI needs to decide which onboarding screen to show.</summary>
/// <param name="Tenant">The tenant record, or null when nothing is registered yet.</param>
/// <param name="IsConnected">True once discovery has completed and the floor is syncing.</param>
/// <param name="PendingRequest">An outstanding request to an admin, if any.</param>
public sealed record OnboardingState(Tenant? Tenant, bool IsConnected, PendingConsentRequest? PendingRequest);

/// <summary>Sends the "please connect our tenant" message to an administrator.</summary>
public interface IConsentEmailSender
{
    Task SendConsentRequestAsync(
        PendingConsentRequest request,
        string consentLandingUrl,
        CancellationToken cancellationToken);
}

/// <summary>Persistence for the parts of onboarding outside the capability profile.</summary>
public interface IOnboardingRepository
{
    Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken);

    Task<AppUser?> FindAppUserAsync(Guid tenantId, Guid entraObjectId, CancellationToken cancellationToken);

    Task<bool> HasAnyAppUserAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddAppUserAsync(AppUser user, CancellationToken cancellationToken);

    Task<PendingConsentRequest?> FindOpenConsentRequestAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds a request by its token across every tenant. Used by the consent landing page,
    /// which runs before any tenant is in scope, so it must read outside the tenant filter.
    /// </summary>
    Task<PendingConsentRequest?> FindConsentRequestByTokenAsync(string token, CancellationToken cancellationToken);

    Task AddConsentRequestAsync(PendingConsentRequest request, CancellationToken cancellationToken);

    Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Drives the onboarding state machine in docs/03-architecture.md §3.
/// </summary>
/// <remarks>
/// <para>
/// The first person from an organisation to arrive is usually not an administrator (ADR-008),
/// so signing in must work before any consent exists. This registers the tenant and the user
/// on first sight, leaves the tenant short of connected, and offers both the "I am an admin"
/// and the "ask my admin" paths.
/// </para>
/// <para>
/// Every transition is idempotent: consent callbacks can be replayed, users refresh pages, and
/// an admin may consent twice. Running any of these twice must reach the same state.
/// </para>
/// </remarks>
public sealed class OnboardingService
{
    /// <summary>How long an emailed consent request stays usable.</summary>
    public static TimeSpan ConsentRequestLifetime { get; } = TimeSpan.FromDays(14);

    /// <summary>Retention after a customer disconnects, before data is destroyed.</summary>
    public static TimeSpan DeletionGracePeriod { get; } = TimeSpan.FromDays(30);

    private readonly IOnboardingRepository _repository;
    private readonly IConsentEmailSender _emailSender;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OnboardingService> _logger;

    public OnboardingService(
        IOnboardingRepository repository,
        IConsentEmailSender emailSender,
        TimeProvider timeProvider,
        ILogger<OnboardingService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _emailSender = emailSender ?? throw new ArgumentNullException(nameof(emailSender));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records a tenant and user on sign-in, then reports where onboarding stands.
    /// </summary>
    /// <remarks>
    /// The first user in a tenant becomes Owner, because otherwise nobody could ever grant the
    /// role. Later arrivals default to Viewer: an unknown colleague from the same directory is
    /// a legitimate user of the product but not automatically an administrator of it.
    /// </remarks>
    public async Task<OnboardingState> RecordSignInAsync(
        SignedInUser user,
        string region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = _timeProvider.GetUtcNow();
        var tenant = await _repository.FindTenantAsync(user.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null)
        {
            tenant = Tenant.Register(user.TenantId, user.DisplayName, defaultDomain: null, region, now);
            await _repository.AddTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Registered tenant {TenantId} on first sign-in.", user.TenantId);
        }

        var appUser = await _repository.FindAppUserAsync(user.TenantId, user.ObjectId, cancellationToken)
            .ConfigureAwait(false);

        if (appUser is null)
        {
            var isFirstUser = !await _repository.HasAnyAppUserAsync(user.TenantId, cancellationToken)
                .ConfigureAwait(false);

            appUser = AppUser.Create(
                user.TenantId,
                user.ObjectId,
                user.Upn,
                user.DisplayName,
                isFirstUser ? AppRole.Owner : AppRole.Viewer,
                now);

            await _repository.AddAppUserAsync(appUser, cancellationToken).ConfigureAwait(false);
        }

        appUser.RecordSeen(now);

        var pending = await _repository.FindOpenConsentRequestAsync(user.TenantId, cancellationToken)
            .ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new OnboardingState(tenant, tenant.Status == TenantStatus.Active, pending);
    }

    /// <summary>
    /// Creates or refreshes a request asking an administrator to connect the tenant.
    /// </summary>
    /// <remarks>
    /// An existing usable request is re-sent rather than replaced. Minting a new token each
    /// time would invalidate the link in an email the admin may be about to click, and would
    /// let a user generate unlimited valid tokens.
    /// </remarks>
    public async Task<PendingConsentRequest> RequestAdminConsentAsync(
        SignedInUser requester,
        string adminEmail,
        Func<PendingConsentRequest, string> landingUrlBuilder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(landingUrlBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminEmail);

        var now = _timeProvider.GetUtcNow();

        var request = await _repository.FindOpenConsentRequestAsync(requester.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (request is not null && request.IsUsable(now))
        {
            request.Resend(ConsentRequestLifetime, now);
        }
        else
        {
            request = PendingConsentRequest.Create(
                requester.TenantId,
                requester.ObjectId,
                requester.Upn,
                adminEmail,
                ConsentRequestLifetime,
                now);

            await _repository.AddConsentRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }

        await _repository.AddAuditAsync(
            AuditLog.ForUser(
                requester.TenantId,
                requester.ObjectId,
                requester.Upn,
                AuditAction.ConsentRequested,
                nameof(PendingConsentRequest),
                request.PendingConsentRequestId.ToString(),
                AuditOutcome.Succeeded,
                Guid.NewGuid().ToString("N"),
                now),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Sending after the commit: an email referencing a token that was rolled back would
        // send the admin to a dead link.
        await _emailSender.SendConsentRequestAsync(request, landingUrlBuilder(request), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Consent request for tenant {TenantId} sent to an administrator (send #{SendCount}).",
            requester.TenantId,
            request.SendCount);

        return request;
    }

    /// <summary>
    /// Handles the admin consent callback and moves the tenant to Provisioning.
    /// </summary>
    /// <remarks>
    /// The tenant id is taken from the signed-in administrator's own validated token, never
    /// from the callback's query string. Microsoft does return a <c>tenant</c> parameter, but
    /// trusting it would let anyone mark an arbitrary tenant as consented by crafting a URL.
    /// </remarks>
    public async Task<Tenant> CompleteAdminConsentAsync(
        SignedInUser admin,
        string region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admin);

        var now = _timeProvider.GetUtcNow();
        var tenant = await _repository.FindTenantAsync(admin.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null)
        {
            tenant = Tenant.Register(admin.TenantId, admin.DisplayName, defaultDomain: null, region, now);
            await _repository.AddTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
        }

        tenant.ConfirmConsent(admin.ObjectId, now);

        var pending = await _repository.FindOpenConsentRequestAsync(admin.TenantId, cancellationToken)
            .ConfigureAwait(false);

        pending?.Complete(now);

        // The consenting admin is by definition able to administer the tenant here too.
        var appUser = await _repository.FindAppUserAsync(admin.TenantId, admin.ObjectId, cancellationToken)
            .ConfigureAwait(false);

        if (appUser is null)
        {
            await _repository.AddAppUserAsync(
                AppUser.Create(admin.TenantId, admin.ObjectId, admin.Upn, admin.DisplayName, AppRole.Owner, now),
                cancellationToken).ConfigureAwait(false);
        }
        else if (appUser.Role != AppRole.Owner)
        {
            appUser.ChangeRole(AppRole.Owner, now);
        }

        await _repository.AddAuditAsync(
            AuditLog.ForUser(
                admin.TenantId,
                admin.ObjectId,
                admin.Upn,
                AuditAction.ConsentGranted,
                nameof(Tenant),
                admin.TenantId.ToString(),
                AuditOutcome.Succeeded,
                Guid.NewGuid().ToString("N"),
                now),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Admin consent recorded for tenant {TenantId}.", admin.TenantId);

        return tenant;
    }

    /// <summary>
    /// Starts the disconnect clock. Data is retained for the grace period so an accidental or
    /// contested disconnect can be undone (docs/03-architecture.md §8).
    /// </summary>
    public async Task<Tenant> DisconnectAsync(SignedInUser actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var now = _timeProvider.GetUtcNow();

        var tenant = await _repository.FindTenantAsync(actor.TenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {actor.TenantId} is not registered.");

        tenant.BeginGracePeriod(now, DeletionGracePeriod);

        await _repository.AddAuditAsync(
            AuditLog.ForUser(
                actor.TenantId,
                actor.ObjectId,
                actor.Upn,
                AuditAction.TenantDisconnected,
                nameof(Tenant),
                actor.TenantId.ToString(),
                AuditOutcome.Succeeded,
                Guid.NewGuid().ToString("N"),
                now)
                .WithValues(null, $"{{\"deleteScheduledUtc\":\"{tenant.DeleteScheduledUtc:O}\"}}"),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Tenant {TenantId} disconnected; data scheduled for deletion at {DeleteScheduledUtc:u}.",
            actor.TenantId,
            tenant.DeleteScheduledUtc);

        return tenant;
    }
}
