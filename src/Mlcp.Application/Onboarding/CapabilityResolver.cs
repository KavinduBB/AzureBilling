using Mlcp.Application.Costs;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>The classification and capability verdict for one tenant.</summary>
/// <param name="AgreementType">Which commercial scenario from docs/01 §4 the tenant is in (ADR-020).</param>
/// <param name="IsPartnerManaged">Licences or Azure are sold by a CSP partner.</param>
/// <param name="ManagingPartnerTenantId">The partner's tenant, when known from <c>ownerTenantId</c>.</param>
/// <param name="Capabilities">
/// Every capability with either availability or a typed reason. Never partial: a capability
/// absent from this map would render as an empty chart.
/// </param>
/// <param name="Denied">
/// Capabilities whose negative verdict came from an explicit refusal (ADR-016 CapabilityDenied), so
/// a previously available one becomes <c>RoleRevoked</c>.
/// </param>
/// <param name="Inconclusive">
/// Capabilities whose probe failed transiently: the verdict here is only <c>ProviderError</c>, and
/// discovery keeps the previous verdict instead (ADR-016).
/// </param>
/// <param name="CostScope">The ADR-017 scope ladder decision.</param>
public sealed record CapabilityResolution(
    AgreementType AgreementType,
    bool IsPartnerManaged,
    Guid? ManagingPartnerTenantId,
    IReadOnlyDictionary<Capability, CapabilityUnavailable?> Capabilities,
    IReadOnlySet<Capability>? Denied = null,
    IReadOnlySet<Capability>? Inconclusive = null,
    CostScopePlan? CostScope = null)
{
    public IReadOnlySet<Capability> DeniedCapabilities => Denied ?? new HashSet<Capability>();

    public IReadOnlySet<Capability> InconclusiveCapabilities => Inconclusive ?? new HashSet<Capability>();
}

/// <summary>
/// Turns three probe results into a tenant classification and a full capability verdict.
/// </summary>
/// <remarks>
/// <para>
/// Pure and synchronous. The rules are the scenario table in docs/01 §4 as amended by ADR-020:
/// an agreement is only classified on positive evidence, and "we can't see it yet" is
/// <see cref="AgreementType.Undetermined"/>, never MOSA. An undetermined tenant is pointed at
/// Guide C (billing role), the one step that would reveal the truth.
/// </para>
/// <para>
/// Every negative verdict carries a reason, a guide and a detail. A capability that is
/// unavailable because Microsoft exposes no API (MOSA seat prices, CSP customer prices) points at
/// manual pricing rather than at a consent screen that cannot help (ADR-006).
/// </para>
/// </remarks>
public static class CapabilityResolver
{
    /// <summary>Checklist copy for Guide C when the agreement is undetermined (ADR-020).</summary>
    public const string UndeterminedBillingCopy =
        "We can't see how your organisation buys Microsoft licences yet. If you have a Microsoft Customer Agreement, grant billing read access to unlock prices and invoices. If you bought online before 2023, pricing may not be available yet — you can enter seat prices meanwhile.";

    public static CapabilityResolution Resolve(
        GraphProbeResult graph,
        ArmProbeResult arm,
        BillingProbeResult billing,
        Guid? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(billing);

        var agreementType = ClassifyAgreement(graph, arm, billing);
        var denied = new HashSet<Capability>();
        var inconclusive = new HashSet<Capability>();

        var costScope = CostScopeResolver.Resolve(BuildCostScopeInput(tenantId ?? Guid.Empty, agreementType, arm, billing));

        var capabilities = new Dictionary<Capability, CapabilityUnavailable?>
        {
            [Capability.GraphLicensing] = ResolveGraphLicensing(graph, inconclusive),
            [Capability.GraphUsage] = ResolveGraphUsage(graph, denied, inconclusive),
            [Capability.ArmAccess] = ResolveArmAccess(arm, denied, inconclusive),
            [Capability.CostManagement] = ResolveCostManagement(arm, agreementType, costScope, denied, inconclusive),
            [Capability.BillingAccount] = ResolveBillingAccount(billing, agreementType, denied, inconclusive),
            [Capability.BillingTransactions] = ResolveBillingTransactions(billing, agreementType, denied, inconclusive),
            [Capability.PartnerCenter] = ResolvePartnerCenter(agreementType),
        };

        return new CapabilityResolution(
            agreementType,
            IsPartnerManaged: agreementType == AgreementType.CspManaged,
            graph.OwnerTenantId,
            capabilities,
            denied,
            inconclusive,
            costScope);
    }

    /// <summary>
    /// Places the tenant in a commercial scenario on positive evidence only (ADR-020).
    /// </summary>
    /// <remarks>
    /// Order: partner-created licences (<c>ownerTenantId</c>) → a visible MCA/EA billing account →
    /// a visible MPA account (the tenant is itself a partner) → a subscription billing property of
    /// MCA/EA → a subscription billing property of MPA (CSP-managed Azure) → MOSA evidence (an
    /// <c>MicrosoftOnlineServicesProgram</c> billing property or account, or a successful billing
    /// role read with no MCA/EA account) → Undetermined when Graph is readable → NotDiscovered.
    /// </remarks>
    public static AgreementType ClassifyAgreement(GraphProbeResult graph, ArmProbeResult arm, BillingProbeResult billing)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(billing);

        if (graph.IsPartnerManaged)
        {
            return AgreementType.CspManaged;
        }

        var accountTypes = billing.Accounts.Select(a => a.AgreementType).ToHashSet();

        if (accountTypes.Contains(AgreementType.Mca))
        {
            return AgreementType.Mca;
        }

        if (accountTypes.Contains(AgreementType.Ea))
        {
            return AgreementType.Ea;
        }

        if (accountTypes.Contains(AgreementType.Mpa))
        {
            return AgreementType.Mpa;
        }

        var subscriptionTypes = arm.Subscriptions.Select(s => s.AgreementType).ToHashSet();

        if (subscriptionTypes.Contains(AgreementType.Mca))
        {
            return AgreementType.Mca;
        }

        if (subscriptionTypes.Contains(AgreementType.Ea))
        {
            return AgreementType.Ea;
        }

        if (subscriptionTypes.Contains(AgreementType.Mpa))
        {
            return AgreementType.CspManaged;
        }

        if (subscriptionTypes.Contains(AgreementType.Mosa)
            || accountTypes.Contains(AgreementType.Mosa)
            || billing.RoleOutcome.Succeeded)
        {
            return AgreementType.Mosa;
        }

        return graph.FloorOutcome.Succeeded ? AgreementType.Undetermined : AgreementType.NotDiscovered;
    }

    private static CostScopeInput BuildCostScopeInput(
        Guid tenantId,
        AgreementType agreementType,
        ArmProbeResult arm,
        BillingProbeResult billing)
    {
        var sampleSucceeded = arm.CostQueryOutcome.Succeeded;
        var sampleDenied = arm.CostQueryOutcome.IsDenied;

        // Only the first subscription is queried during discovery. Its answer is recorded as
        // such; the rest are left unknown for the sync to verify.
        var subscriptions = arm.Subscriptions
            .Select((s, index) => new SubscriptionCostAccess(
                s.SubscriptionId,
                s.AgreementType,
                index == 0 ? (sampleSucceeded ? true : sampleDenied ? false : null) : null))
            .ToList();

        var eaAccount = agreementType == AgreementType.Ea
            ? billing.Accounts.FirstOrDefault(a => a.AgreementType == AgreementType.Ea)?.Name
            : null;

        return new CostScopeInput(
            tenantId,
            agreementType,
            billing.RoleOutcome.Succeeded,
            billing.BillingScopeCostOutcome.Succeeded,
            billing.BillingProfileIds ?? [],
            eaAccount is null ? null : $"/providers/Microsoft.Billing/billingAccounts/{eaAccount}",
            arm.RootManagementGroupOutcome.Succeeded,
            subscriptions);
    }

    private static CapabilityUnavailable? ResolveGraphLicensing(GraphProbeResult graph, HashSet<Capability> inconclusive)
    {
        var floor = graph.FloorOutcome;

        if (floor.Succeeded)
        {
            return null;
        }

        if (floor.RequiresReconsent)
        {
            return new CapabilityUnavailable(
                Capability.GraphLicensing,
                CapabilityUnavailableReason.ConsentRevoked,
                RemediationGuide.ConnectOrganisation,
                "Directory permissions are missing or were revoked. An administrator needs to connect the organisation again.");
        }

        return Inconclusive(Capability.GraphLicensing, floor, inconclusive);
    }

    private static CapabilityUnavailable? ResolveGraphUsage(
        GraphProbeResult graph,
        HashSet<Capability> denied,
        HashSet<Capability> inconclusive)
    {
        var usage = graph.UsageOutcome;

        if (usage.Succeeded)
        {
            return null;
        }

        if (usage.IsInconclusive)
        {
            return Inconclusive(Capability.GraphUsage, usage, inconclusive);
        }

        denied.Add(Capability.GraphUsage);

        return new CapabilityUnavailable(
            Capability.GraphUsage,
            CapabilityUnavailableReason.Tier2NotGranted,
            RemediationGuide.UsageInsights,
            "The MLCP Usage Insights app (Reports.Read.All) has not been granted. Usage insights are an optional, separate consent.");
    }

    private static CapabilityUnavailable? ResolveArmAccess(
        ArmProbeResult arm,
        HashSet<Capability> denied,
        HashSet<Capability> inconclusive)
    {
        var list = arm.SubscriptionListOutcome;

        if (list.Succeeded)
        {
            return arm.HasAnySubscription
                ? null
                : new CapabilityUnavailable(
                    Capability.ArmAccess,
                    CapabilityUnavailableReason.NoAzure,
                    RemediationGuide.AzureRbac,
                    "No Azure subscriptions are visible. Admin consent grants nothing on Azure; a role assignment is needed.");
        }

        if (list.IsDenied)
        {
            denied.Add(Capability.ArmAccess);

            return new CapabilityUnavailable(
                Capability.ArmAccess,
                CapabilityUnavailableReason.RbacMissing,
                RemediationGuide.AzureRbac,
                "Azure refused to list subscriptions for MLCP. Assign the Reader role.");
        }

        return Inconclusive(Capability.ArmAccess, list, inconclusive);
    }

    private static CapabilityUnavailable? ResolveCostManagement(
        ArmProbeResult arm,
        AgreementType agreementType,
        CostScopePlan costScope,
        HashSet<Capability> denied,
        HashSet<Capability> inconclusive)
    {
        if (costScope.Rung != CostQueryScopeRung.None)
        {
            return null;
        }

        var list = arm.SubscriptionListOutcome;

        if (!list.Succeeded)
        {
            if (list.IsDenied)
            {
                denied.Add(Capability.CostManagement);

                return new CapabilityUnavailable(
                    Capability.CostManagement,
                    CapabilityUnavailableReason.RbacMissing,
                    RemediationGuide.AzureRbac,
                    "Assign the Cost Management Reader role to MLCP on your subscriptions or management group.");
            }

            return Inconclusive(Capability.CostManagement, list, inconclusive);
        }

        if (!arm.HasAnySubscription)
        {
            return new CapabilityUnavailable(
                Capability.CostManagement,
                CapabilityUnavailableReason.NoAzure,
                RemediationGuide.AzureRbac,
                "No Azure subscriptions are visible to MLCP.");
        }

        // Classic CSP Azure exposes no cost data regardless of roles, so pointing the customer
        // at a role assignment would send them on an errand that cannot succeed.
        if (arm.IsClassicCspOnly)
        {
            return new CapabilityUnavailable(
                Capability.CostManagement,
                CapabilityUnavailableReason.ClassicCsp,
                RemediationGuide.PartnerManaged,
                "These subscriptions use the classic CSP Azure offer. Cost data requires migration to an Azure Plan.");
        }

        var query = arm.CostQueryOutcome;

        if (query.Attempted && query.IsInconclusive && !arm.RootManagementGroupOutcome.IsDenied)
        {
            return Inconclusive(Capability.CostManagement, query, inconclusive);
        }

        if (query.IsDenied)
        {
            denied.Add(Capability.CostManagement);
        }

        return new CapabilityUnavailable(
            Capability.CostManagement,
            CapabilityUnavailableReason.RbacMissing,
            RemediationGuide.AzureRbac,
            agreementType switch
            {
                AgreementType.Ea =>
                    "Assign Cost Management Reader. On an Enterprise Agreement, also enable 'AO view charges' and 'DA view charges'.",
                AgreementType.Mca or AgreementType.CspManaged =>
                    "Assign Cost Management Reader on your subscriptions. Management groups are not supported for this agreement; grant a billing role (Guide C) to see purchases.",
                _ => "Assign the Cost Management Reader role to MLCP on your subscriptions or management group.",
            });
    }

    private static CapabilityUnavailable? ResolveBillingAccount(
        BillingProbeResult billing,
        AgreementType agreementType,
        HashSet<Capability> denied,
        HashSet<Capability> inconclusive)
    {
        var list = billing.AccountListOutcome;
        var role = billing.RoleOutcome;

        if (billing.HasAnyAccount && role.Succeeded)
        {
            return null;
        }

        switch (agreementType)
        {
            case AgreementType.CspManaged:
                return new CapabilityUnavailable(
                    Capability.BillingAccount,
                    CapabilityUnavailableReason.CspManaged,
                    RemediationGuide.PartnerManaged,
                    "Your licences are managed by a partner, so Microsoft holds no billing relationship with you.");

            case AgreementType.Mosa:
                return new CapabilityUnavailable(
                    Capability.BillingAccount,
                    CapabilityUnavailableReason.Mosa,
                    RemediationGuide.ManualPricing,
                    "Microsoft moves accounts like yours to the Customer Agreement at renewal; billing data unlocks then.");

            case AgreementType.NotDiscovered:
                return new CapabilityUnavailable(
                    Capability.BillingAccount,
                    CapabilityUnavailableReason.NotDiscovered,
                    RemediationGuide.ConnectOrganisation,
                    "The organisation must be connected before billing access can be checked.");
        }

        if (list.IsInconclusive && list.Attempted)
        {
            return Inconclusive(Capability.BillingAccount, list, inconclusive);
        }

        if (role.Attempted && role.IsInconclusive)
        {
            return Inconclusive(Capability.BillingAccount, role, inconclusive);
        }

        if (list.Kind == Shared.Resilience.MicrosoftFailureKind.NotFound)
        {
            return new CapabilityUnavailable(
                Capability.BillingAccount,
                CapabilityUnavailableReason.NoBillingAccount,
                RemediationGuide.BillingRole,
                "No billing account is visible to MLCP.");
        }

        if (list.IsDenied || role.IsDenied || !billing.HasAnyAccount)
        {
            denied.Add(Capability.BillingAccount);
        }

        return new CapabilityUnavailable(
            Capability.BillingAccount,
            CapabilityUnavailableReason.BillingRoleMissing,
            RemediationGuide.BillingRole,
            agreementType == AgreementType.Undetermined
                ? UndeterminedBillingCopy
                : billing.HasAnyAccount
                    ? "A billing account exists but MLCP holds no billing role on it. Billing account reader is enough."
                    : "MLCP holds no billing role, so your billing account is not visible. Grant billing account reader or billing profile reader.");
    }

    private static CapabilityUnavailable? ResolveBillingTransactions(
        BillingProbeResult billing,
        AgreementType agreementType,
        HashSet<Capability> denied,
        HashSet<Capability> inconclusive)
    {
        var transactions = billing.TransactionsOutcome;

        if (transactions.Succeeded && agreementType is AgreementType.Mca or AgreementType.Mpa)
        {
            return null;
        }

        switch (agreementType)
        {
            case AgreementType.CspManaged:
                return new CapabilityUnavailable(
                    Capability.BillingTransactions,
                    CapabilityUnavailableReason.CspManaged,
                    RemediationGuide.PartnerManaged,
                    "Microsoft does not expose what your partner charges you. Enter your prices, or invite your partner.");

            case AgreementType.Mosa:
                return new CapabilityUnavailable(
                    Capability.BillingTransactions,
                    CapabilityUnavailableReason.Mosa,
                    RemediationGuide.ManualPricing,
                    "No price API exists for this agreement. Enter seat prices meanwhile; they unlock at your next renewal.");

            // An EA exposes Azure invoices and a price sheet, but not Microsoft 365 seat prices
            // at enrolment level, so the honest answer is a price-sheet upload (docs/01 §5.9).
            case AgreementType.Ea:
                return new CapabilityUnavailable(
                    Capability.BillingTransactions,
                    CapabilityUnavailableReason.NotMca,
                    RemediationGuide.ManualPricing,
                    "Enterprise Agreements do not expose Microsoft 365 seat prices through the API. Upload your price sheet.");

            case AgreementType.Mca:
            case AgreementType.Mpa:
            case AgreementType.Undetermined:
                break;

            default:
                return new CapabilityUnavailable(
                    Capability.BillingTransactions,
                    CapabilityUnavailableReason.NotDiscovered,
                    RemediationGuide.ConnectOrganisation,
                    "The organisation must be connected before prices can be checked.");
        }

        foreach (var outcome in new[] { billing.AccountListOutcome, billing.RoleOutcome, transactions })
        {
            if (outcome.Attempted && outcome.IsInconclusive)
            {
                return Inconclusive(Capability.BillingTransactions, outcome, inconclusive);
            }
        }

        denied.Add(Capability.BillingTransactions);

        return new CapabilityUnavailable(
            Capability.BillingTransactions,
            CapabilityUnavailableReason.BillingRoleMissing,
            RemediationGuide.BillingRole,
            agreementType == AgreementType.Undetermined
                ? UndeterminedBillingCopy
                : "Grant MLCP a billing role to read transactions, which is where real seat prices come from.");
    }

    private static CapabilityUnavailable? ResolvePartnerCenter(AgreementType agreementType)
        => new(
            Capability.PartnerCenter,
            agreementType == AgreementType.Mpa
                ? CapabilityUnavailableReason.NotDiscovered
                : CapabilityUnavailableReason.NotPartner,
            RemediationGuide.None,
            agreementType == AgreementType.Mpa
                ? "Partner features arrive in Phase 6."
                : "This tenant is not a Microsoft partner billing account.");

    private static CapabilityUnavailable Inconclusive(Capability capability, ProbeCallOutcome outcome, HashSet<Capability> inconclusive)
    {
        inconclusive.Add(capability);
        return CapabilityUnavailable.ProviderError(capability, $"Microsoft did not answer conclusively ({outcome.Describe()}). The previous result is kept and re-checked on the next discovery.");
    }
}
