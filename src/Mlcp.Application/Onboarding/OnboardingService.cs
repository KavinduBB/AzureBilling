using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Sync;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>Identity of the person driving an onboarding action.</summary>
/// <param name="TenantId">Their Entra tenant, from the validated <c>tid</c> claim.</param>
/// <param name="ObjectId">Their Entra object id.</param>
/// <param name="Upn">Their user principal name.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="IsDirectoryAdmin">
/// True when the current token's <c>wids</c> claim holds a role that can grant tenant-wide
/// consent (<see cref="AdminRoles"/>). Read live from the token on every request (ADR-018).
/// </param>
public sealed record SignedInUser(
    Guid TenantId,
    Guid ObjectId,
    string Upn,
    string DisplayName,
    bool IsDirectoryAdmin = false);

/// <summary>What the UI needs to decide which onboarding screen to show.</summary>
/// <param name="Tenant">The tenant record, or null when nothing is registered yet.</param>
/// <param name="Role">The signed-in person's in-app role after this sign-in.</param>
/// <param name="PendingRequest">An outstanding request to an admin, if any.</param>
public sealed record OnboardingState(Tenant? Tenant, AppRole Role, PendingConsentRequest? PendingRequest)
{
    /// <summary>True once discovery has completed and the floor is syncing.</summary>
    public bool IsConnected => Tenant?.Status == TenantStatus.Active;
}

/// <summary>Result of an administrator asking to start admin consent.</summary>
public enum ConnectDecision
{
    /// <summary>Send the administrator to Entra.</summary>
    Proceed = 0,

    /// <summary>The caller is not a directory admin; offer "ask my admin" instead.</summary>
    NotDirectoryAdmin = 1,

    /// <summary>The tenant is already registered in another region; send the caller there.</summary>
    RegisteredElsewhere = 2,

    /// <summary>The tenant is in its disconnect grace period; an Owner must cancel it first.</summary>
    Disconnected = 3,
}

/// <param name="Decision">What to do next.</param>
/// <param name="Region">The tenant's region (this one, or where it is registered).</param>
public sealed record ConnectOutcome(ConnectDecision Decision, string Region);

/// <summary>What the consent callback concluded.</summary>
public enum ConsentCallbackResult
{
    /// <summary>An app-only call succeeded; discovery is queued.</summary>
    Verified = 0,

    /// <summary>Microsoft has not finished propagating; a verification job is queued.</summary>
    PendingVerification = 1,

    /// <summary>Microsoft says the app is not consented.</summary>
    NotGranted = 2,

    /// <summary>Consent could not be confirmed; a retry is queued.</summary>
    CouldNotConfirm = 3,

    /// <summary>The caller is not a directory admin.</summary>
    NotDirectoryAdmin = 4,

    /// <summary>The tenant is disconnected; the callback never reverses that.</summary>
    Disconnected = 5,
}

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

    Task AddAppUserAsync(AppUser user, CancellationToken cancellationToken);

    /// <summary>The most recent usable request for the tenant, for display.</summary>
    Task<PendingConsentRequest?> FindOpenConsentRequestAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The usable request one person sent to one address, if any.</summary>
    Task<PendingConsentRequest?> FindOpenConsentRequestAsync(
        Guid tenantId,
        Guid requestedByObjectId,
        string sentToEmail,
        CancellationToken cancellationToken);

    /// <summary>Every request in the tenant last sent at or after <paramref name="sinceUtc"/>, for the rate limits.</summary>
    Task<IReadOnlyList<PendingConsentRequest>> ListConsentRequestsSentSinceAsync(
        Guid tenantId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>Finds a usable request by its emailed token, within the tenant in scope.</summary>
    Task<PendingConsentRequest?> FindConsentRequestByTokenAsync(string token, CancellationToken cancellationToken);

    Task AddConsentRequestAsync(PendingConsentRequest request, CancellationToken cancellationToken);

    Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Drives the onboarding state machine in docs/03-architecture.md §3, hardened by ADR-018.
/// </summary>
/// <remarks>
/// <para>
/// The first person from an organisation to arrive is usually not an administrator (ADR-008),
/// so signing in works before any consent exists. Owner is never handed out for arriving
/// first: it mirrors the directory's own admin roles and is re-derived at every sign-in.
/// </para>
/// <para>
/// The consent callback is verified, not trusted. It only records that an admin came back, then
/// makes the single permitted in-request Microsoft call (<see cref="IConsentVerifier"/>). All
/// further Microsoft work is queued (CLAUDE.md rule 5).
/// </para>
/// <para>
/// Every transition is idempotent: callbacks are replayed, users refresh pages, and an admin may
/// consent twice.
/// </para>
/// </remarks>
public sealed class OnboardingService
{
    /// <summary>How long an emailed consent request stays usable.</summary>
    public static TimeSpan ConsentRequestLifetime { get; } = TimeSpan.FromDays(14);

    /// <summary>Retention after a customer disconnects, before data is destroyed.</summary>
    public static TimeSpan DeletionGracePeriod { get; } = TimeSpan.FromDays(30);

    /// <summary>First retry of consent verification after propagation delay (ADR-016 rule 3).</summary>
    public static TimeSpan ConsentVerificationDelay { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Granularity of the "Check again" de-duplication key (ADR-016 rule 5).</summary>
    public static TimeSpan ReconsentProbeInterval { get; } = TimeSpan.FromMinutes(5);

    private readonly IOnboardingRepository _repository;
    private readonly IConsentEmailSender _emailSender;
    private readonly IConsentVerifier _consentVerifier;
    private readonly ISyncJobEnqueuer _jobs;
    private readonly IRegionDirectory _regions;
    private readonly ITenantDirectoryInfo _directoryInfo;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OnboardingService> _logger;

    public OnboardingService(
        IOnboardingRepository repository,
        IConsentEmailSender emailSender,
        IConsentVerifier consentVerifier,
        ISyncJobEnqueuer jobs,
        IRegionDirectory regions,
        ITenantDirectoryInfo directoryInfo,
        TimeProvider timeProvider,
        ILogger<OnboardingService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _emailSender = emailSender ?? throw new ArgumentNullException(nameof(emailSender));
        _consentVerifier = consentVerifier ?? throw new ArgumentNullException(nameof(consentVerifier));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _regions = regions ?? throw new ArgumentNullException(nameof(regions));
        _directoryInfo = directoryInfo ?? throw new ArgumentNullException(nameof(directoryInfo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records a tenant and user on sign-in, re-derives Owner from the directory, and reports
    /// where onboarding stands.
    /// </summary>
    /// <remarks>
    /// The tenant is registered locally as <see cref="TenantStatus.NotConnected"/>. The global
    /// region directory is written only when an administrator confirms the region at connection
    /// (<see cref="BeginConnectAsync"/>): a colleague's sign-in on one stack must not decide
    /// where the organisation's data will live.
    /// </remarks>
    public async Task<OnboardingState> RecordSignInAsync(
        SignedInUser user,
        string region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var now = _timeProvider.GetUtcNow();
        var tenant = await FindOrRegisterTenantAsync(user, region, now, cancellationToken).ConfigureAwait(false);

        var appUser = await _repository.FindAppUserAsync(user.TenantId, user.ObjectId, cancellationToken)
            .ConfigureAwait(false);

        if (appUser is null)
        {
            appUser = AppUser.FirstSignIn(
                user.TenantId,
                user.ObjectId,
                user.Upn,
                user.DisplayName,
                user.IsDirectoryAdmin,
                now);

            await _repository.AddAppUserAsync(appUser, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            appUser.UpdateProfile(user.Upn, user.DisplayName, now);

            if (appUser.ApplyDirectoryAdminStatus(user.IsDirectoryAdmin, now) is { } previousRole)
            {
                await AuditAsync(
                    user,
                    AuditAction.AppUserRoleChanged,
                    nameof(AppUser),
                    appUser.AppUserId.ToString(),
                    AuditOutcome.Succeeded,
                    NewCorrelationId(),
                    now,
                    oldValue: Json(new { role = previousRole.ToString() }),
                    newValue: Json(new { role = appUser.Role.ToString(), source = AdminRoles.DirectoryRolesClaim }),
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "User {ObjectId} in tenant {TenantId} changed role from {PreviousRole} to {Role} at sign-in.",
                    user.ObjectId,
                    user.TenantId,
                    previousRole,
                    appUser.Role);
            }
        }

        appUser.RecordSeen(now);

        var pending = await _repository.FindOpenConsentRequestAsync(user.TenantId, cancellationToken)
            .ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new OnboardingState(tenant, appUser.Role, pending);
    }

    /// <summary>
    /// An administrator confirmed this region and asked to connect. Claims the region in the
    /// global directory and writes the audit Attempt row before the redirect to Entra.
    /// </summary>
    /// <param name="admin">The caller.</param>
    /// <param name="region">This stack's region, which the administrator confirmed on the page.</param>
    /// <param name="correlationId">Carried in the consent state, so the callback's rows correlate.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<ConnectOutcome> BeginConnectAsync(
        SignedInUser admin,
        string region,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (!admin.IsDirectoryAdmin)
        {
            return new ConnectOutcome(ConnectDecision.NotDirectoryAdmin, region);
        }

        var now = _timeProvider.GetUtcNow();
        var tenant = await FindOrRegisterTenantAsync(admin, region, now, cancellationToken).ConfigureAwait(false);

        if (tenant.Status is TenantStatus.GracePeriod or TenantStatus.Deleted)
        {
            return new ConnectOutcome(ConnectDecision.Disconnected, tenant.Region);
        }

        var registration = await _regions.TryRegisterAsync(admin.TenantId, region, cancellationToken)
            .ConfigureAwait(false);

        if (registration.IsElsewhere(region))
        {
            _logger.LogWarning(
                "Tenant {TenantId} tried to connect in {Region} but is registered in {RegisteredRegion}.",
                admin.TenantId,
                region,
                registration.Region);

            await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ConnectOutcome(ConnectDecision.RegisteredElsewhere, registration.Region);
        }

        await AuditAsync(
            admin,
            AuditAction.ConsentGranted,
            nameof(Tenant),
            admin.TenantId.ToString(),
            AuditOutcome.Attempted,
            correlationId,
            now,
            oldValue: null,
            newValue: Json(new { region, regionConfirmed = true, regionRegisteredNow = registration.RegisteredNow }),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ConnectOutcome(ConnectDecision.Proceed, region);
    }

    /// <summary>
    /// Handles an administrator's return from the Entra consent screen, after the web layer has
    /// validated the single-use state and Microsoft's <c>tenant</c> parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tenant is taken from the administrator's own validated token. The callback records
    /// only that an admin came back, then confirms consent with one app-only call. It never
    /// cancels a scheduled deletion: that is <see cref="CancelDisconnectAsync"/>.
    /// </para>
    /// <para>
    /// Discovery is queued, never run here.
    /// </para>
    /// </remarks>
    public async Task<ConsentCallbackResult> CompleteAdminConsentAsync(
        SignedInUser admin,
        string region,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (!admin.IsDirectoryAdmin)
        {
            return ConsentCallbackResult.NotDirectoryAdmin;
        }

        var now = _timeProvider.GetUtcNow();
        var tenant = await FindOrRegisterTenantAsync(admin, region, now, cancellationToken).ConfigureAwait(false);

        if (tenant.Status is TenantStatus.GracePeriod or TenantStatus.Deleted)
        {
            _logger.LogWarning(
                "Consent callback for disconnected tenant {TenantId} ignored; cancelling the disconnect is a separate Owner action.",
                admin.TenantId);

            return ConsentCallbackResult.Disconnected;
        }

        tenant.AwaitConsentVerification(admin.ObjectId, now);
        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The one Microsoft call a request handler may make here (CLAUDE.md rule 5, ADR-018).
        var verification = await _consentVerifier.VerifyAsync(admin.TenantId, cancellationToken).ConfigureAwait(false);
        now = _timeProvider.GetUtcNow();

        switch (verification.Status)
        {
            case ConsentVerificationStatus.Verified:
                tenant.ConfirmConsent(admin.ObjectId, now);

                var pending = await _repository.FindOpenConsentRequestAsync(admin.TenantId, cancellationToken)
                    .ConfigureAwait(false);
                pending?.Complete(now);

                await AuditAsync(
                    admin,
                    AuditAction.ConsentGranted,
                    nameof(Tenant),
                    admin.TenantId.ToString(),
                    AuditOutcome.Succeeded,
                    correlationId,
                    now,
                    oldValue: null,
                    newValue: Json(new { region = tenant.Region, verifiedBy = "app-only /organization" }),
                    cancellationToken).ConfigureAwait(false);

                await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await TryEnqueueAsync(
                    admin.TenantId,
                    SyncJobType.CapabilityDiscovery,
                    $"discovery:{admin.TenantId:N}:{correlationId}",
                    notBeforeUtc: null,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Admin consent verified for tenant {TenantId}; discovery queued.", admin.TenantId);
                return ConsentCallbackResult.Verified;

            case ConsentVerificationStatus.PendingPropagation:
                await ScheduleVerificationAsync(admin.TenantId, correlationId, now, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Admin consent for tenant {TenantId} is still propagating; verification queued.",
                    admin.TenantId);
                return ConsentCallbackResult.PendingVerification;

            case ConsentVerificationStatus.NotGranted:
                await AuditAsync(
                    admin,
                    AuditAction.ConsentGranted,
                    nameof(Tenant),
                    admin.TenantId.ToString(),
                    AuditOutcome.Failed,
                    correlationId,
                    now,
                    oldValue: null,
                    newValue: Json(new { verification = "NotGranted" }),
                    cancellationToken).ConfigureAwait(false);

                await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogWarning("Admin consent callback for tenant {TenantId} could not be verified: not granted.", admin.TenantId);
                return ConsentCallbackResult.NotGranted;

            default:
                // Transient or unclassified: conclude nothing, try again shortly.
                await ScheduleVerificationAsync(admin.TenantId, correlationId, now, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Admin consent for tenant {TenantId} could not be confirmed yet; verification queued.",
                    admin.TenantId);
                return ConsentCallbackResult.CouldNotConfirm;
        }
    }

    /// <summary>
    /// An administrator returned from the separate Usage Insights consent (ADR-015). Nothing is
    /// concluded from the callback: discovery re-probes usage with that registration's token and
    /// records the verdict.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not a directory admin.</exception>
    public async Task RecordUsageInsightsConsentAsync(
        SignedInUser admin,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        EnsureDirectoryAdmin(admin);

        var tenant = await _repository.FindTenantAsync(admin.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null || !tenant.IsSyncEligible)
        {
            return;
        }

        await AuditAsync(
            admin,
            AuditAction.CapabilityUnlocked,
            nameof(Tenant),
            admin.TenantId.ToString(),
            AuditOutcome.Attempted,
            correlationId,
            _timeProvider.GetUtcNow(),
            oldValue: null,
            newValue: Json(new { capability = "GraphUsage", registration = "UsageInsights" }),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Tier-2 consent propagates like tier 1, so the re-probe waits the same short delay.
        await TryEnqueueAsync(
            admin.TenantId,
            SyncJobType.CapabilityDiscovery,
            $"discovery:{admin.TenantId:N}:{correlationId}",
            _timeProvider.GetUtcNow() + ConsentVerificationDelay,
            correlationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or refreshes a request asking an administrator to connect the tenant, within the
    /// limits of <see cref="ConsentRequestPolicy"/>.
    /// </summary>
    /// <remarks>
    /// A usable request from the same person to the same address is re-sent rather than
    /// replaced, so the link in an earlier email keeps working. A different address always gets
    /// a new request.
    /// </remarks>
    public async Task<ConsentRequestOutcome> RequestAdminConsentAsync(
        SignedInUser requester,
        string? adminEmail,
        Func<PendingConsentRequest, string> landingUrlBuilder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(landingUrlBuilder);

        if (!ConsentRequestPolicy.TryNormaliseAddress(adminEmail, out var address))
        {
            return ConsentRequestOutcome.Refused(ConsentRequestRefusal.InvalidAddress);
        }

        var now = _timeProvider.GetUtcNow();

        var tenant = await _repository.FindTenantAsync(requester.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant?.ConsentGrantedUtc is not null && tenant.Status is TenantStatus.Active or TenantStatus.Provisioning)
        {
            return ConsentRequestOutcome.Refused(ConsentRequestRefusal.AlreadyConnected);
        }

        var verifiedDomains = await _directoryInfo.GetVerifiedDomainsAsync(requester.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!ConsentRequestPolicy.IsDomainAllowed(address, verifiedDomains, requester.Upn))
        {
            return ConsentRequestOutcome.Refused(ConsentRequestRefusal.DomainNotAllowed);
        }

        var recent = await _repository
            .ListConsentRequestsSentSinceAsync(requester.TenantId, now - ConsentRequestPolicy.Window, cancellationToken)
            .ConfigureAwait(false);

        var refusal = ConsentRequestPolicy.EvaluateRateLimits(requester.ObjectId, recent, now, out var retryAfter);

        if (refusal != ConsentRequestRefusal.None)
        {
            _logger.LogInformation(
                "Consent request by {ObjectId} in tenant {TenantId} refused: {Refusal}.",
                requester.ObjectId,
                requester.TenantId,
                refusal);

            return ConsentRequestOutcome.Refused(refusal, retryAfter);
        }

        var request = await _repository
            .FindOpenConsentRequestAsync(requester.TenantId, requester.ObjectId, address, cancellationToken)
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
                address,
                ConsentRequestLifetime,
                now);

            await _repository.AddConsentRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(
            requester,
            AuditAction.ConsentRequested,
            nameof(PendingConsentRequest),
            request.PendingConsentRequestId.ToString(),
            AuditOutcome.Succeeded,
            NewCorrelationId(),
            now,
            oldValue: null,
            newValue: Json(new { sendCount = request.SendCount, recipientDomain = ConsentRequestPolicy.DomainOf(address) }),
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

        return new ConsentRequestOutcome(request, ConsentRequestRefusal.None);
    }

    /// <summary>
    /// Starts the disconnect clock. Data is retained for the grace period so an accidental or
    /// contested disconnect can be undone (docs/03-architecture.md §8).
    /// </summary>
    /// <remarks>
    /// The web layer has already required a live admin claim and a recent re-authentication;
    /// the admin check here is a second line, not the only one.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The caller is not a directory admin.</exception>
    public async Task<Tenant> DisconnectAsync(SignedInUser actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDirectoryAdmin(actor);

        var now = _timeProvider.GetUtcNow();
        var correlationId = NewCorrelationId();

        var tenant = await _repository.FindTenantAsync(actor.TenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {actor.TenantId} is not registered.");

        await AuditAsync(
            actor,
            AuditAction.TenantDisconnected,
            nameof(Tenant),
            actor.TenantId.ToString(),
            AuditOutcome.Attempted,
            correlationId,
            now,
            oldValue: Json(new { status = tenant.Status.ToString() }),
            newValue: null,
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        tenant.BeginGracePeriod(now, DeletionGracePeriod);

        await AuditAsync(
            actor,
            AuditAction.TenantDisconnected,
            nameof(Tenant),
            actor.TenantId.ToString(),
            AuditOutcome.Succeeded,
            correlationId,
            now,
            oldValue: null,
            newValue: Json(new { deleteScheduledUtc = tenant.DeleteScheduledUtc }),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Tenant {TenantId} disconnected; data scheduled for deletion at {DeleteScheduledUtc:u}.",
            actor.TenantId,
            tenant.DeleteScheduledUtc);

        return tenant;
    }

    /// <summary>
    /// Reverses a disconnect inside the grace period. The only path that cancels a scheduled
    /// deletion (ADR-018).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not a directory admin.</exception>
    public async Task<Tenant> CancelDisconnectAsync(SignedInUser actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDirectoryAdmin(actor);

        var now = _timeProvider.GetUtcNow();
        var correlationId = NewCorrelationId();

        var tenant = await _repository.FindTenantAsync(actor.TenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {actor.TenantId} is not registered.");

        if (tenant.Status != TenantStatus.GracePeriod)
        {
            return tenant;
        }

        var scheduled = tenant.DeleteScheduledUtc;

        await AuditAsync(
            actor,
            AuditAction.TenantConnected,
            nameof(Tenant),
            actor.TenantId.ToString(),
            AuditOutcome.Attempted,
            correlationId,
            now,
            oldValue: Json(new { status = tenant.Status.ToString(), deleteScheduledUtc = scheduled }),
            newValue: Json(new { action = "cancel-disconnect" }),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        tenant.CancelDeletion(now);

        await AuditAsync(
            actor,
            AuditAction.TenantConnected,
            nameof(Tenant),
            actor.TenantId.ToString(),
            AuditOutcome.Succeeded,
            correlationId,
            now,
            oldValue: null,
            newValue: Json(new { action = "cancel-disconnect", status = tenant.Status.ToString() }),
            cancellationToken).ConfigureAwait(false);

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (tenant.ConsentGrantedUtc is not null)
        {
            // Grants may have changed while the tenant was disconnected; re-probe from the worker.
            await TryEnqueueAsync(
                actor.TenantId,
                SyncJobType.CapabilityDiscovery,
                $"discovery:{actor.TenantId:N}:{correlationId}",
                notBeforeUtc: null,
                correlationId,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Tenant {TenantId} disconnect cancelled by {ObjectId}.", actor.TenantId, actor.ObjectId);
        return tenant;
    }

    /// <summary>
    /// The Owner's "Check again" button on a tenant that needs re-consent (ADR-016 rule 5).
    /// Queues a floor-only re-probe; the probe itself runs in the worker.
    /// </summary>
    /// <returns>True when a probe was queued.</returns>
    /// <exception cref="UnauthorizedAccessException">The caller is not a directory admin.</exception>
    public async Task<bool> RequestReconsentProbeAsync(SignedInUser actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDirectoryAdmin(actor);

        var tenant = await _repository.FindTenantAsync(actor.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant?.Status != TenantStatus.NeedsReconsent)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        // One key per five-minute slot: Service Bus duplicate detection collapses repeated
        // clicks across instances, behind the per-instance rate limiter in the web layer.
        var slot = now.ToUnixTimeSeconds() / (long)ReconsentProbeInterval.TotalSeconds;

        await _jobs.EnqueueAsync(
            actor.TenantId,
            SyncJobType.ReconsentProbe,
            string.Create(CultureInfo.InvariantCulture, $"reconsent-probe:{actor.TenantId:N}:{slot}"),
            notBeforeUtc: null,
            NewCorrelationId(),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Owner {ObjectId} requested a re-consent probe for tenant {TenantId}.", actor.ObjectId, actor.TenantId);
        return true;
    }

    private async Task<Tenant> FindOrRegisterTenantAsync(
        SignedInUser user,
        string region,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenant = await _repository.FindTenantAsync(user.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is not null)
        {
            return tenant;
        }

        // The display name is a placeholder until discovery reads /organization.
        tenant = Tenant.Register(user.TenantId, user.DisplayName, defaultDomain: null, region, now);
        await _repository.AddTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Registered tenant {TenantId} on first sign-in.", user.TenantId);
        return tenant;
    }

    private async Task ScheduleVerificationAsync(
        Guid tenantId,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The worker owns the +5 and +15 minute retries (ADR-018); the request queues the first.
        await TryEnqueueAsync(
            tenantId,
            SyncJobType.ConsentVerification,
            $"consent-verification:{tenantId:N}:{correlationId}",
            now + ConsentVerificationDelay,
            correlationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueues after the database commit. A failed send is logged, not thrown: the tenant state
    /// is already durable and the worker's scheduler sweeps Provisioning tenants, so failing the
    /// request would only show the admin an error for work that will still happen.
    /// </summary>
    private async Task TryEnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _jobs.EnqueueAsync(tenantId, jobType, deduplicationKey, notBeforeUtc, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Could not enqueue {JobType} for tenant {TenantId} (correlation {CorrelationId}); the scheduler will pick it up.",
                jobType,
                tenantId,
                correlationId);
        }
    }

    private Task AuditAsync(
        SignedInUser actor,
        AuditAction action,
        string entityType,
        string? entityId,
        AuditOutcome outcome,
        string correlationId,
        DateTimeOffset now,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken)
        => _repository.AddAuditAsync(
            AuditLog.ForUser(
                actor.TenantId,
                actor.ObjectId,
                actor.Upn,
                action,
                entityType,
                entityId,
                outcome,
                correlationId,
                now)
                .WithValues(oldValue, newValue),
            cancellationToken);

    private static void EnsureDirectoryAdmin(SignedInUser actor)
    {
        if (!actor.IsDirectoryAdmin)
        {
            throw new UnauthorizedAccessException("Only a directory administrator can perform this action.");
        }
    }

    private static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
