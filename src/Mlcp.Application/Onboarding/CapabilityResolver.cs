using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>The classification and capability verdict for one tenant.</summary>
/// <param name="AgreementType">Which commercial scenario from docs/01 §4 the tenant is in.</param>
/// <param name="IsPartnerManaged">Licences are sold by a CSP partner.</param>
/// <param name="ManagingPartnerTenantId">The partner's tenant, when known.</param>
/// <param name="Capabilities">
/// Every capability with either availability or a typed reason. Never partial: a capability
/// absent from this map would render as an empty chart.
/// </param>
public sealed record CapabilityResolution(
    AgreementType AgreementType,
    bool IsPartnerManaged,
    Guid? ManagingPartnerTenantId,
    IReadOnlyDictionary<Capability, CapabilityUnavailable?> Capabilities);

/// <summary>
/// Turns three probe results into a tenant classification and a full capability verdict.
/// </summary>
/// <remarks>
/// <para>
/// Pure and synchronous by design. The rules encoded here are the scenario table in
/// docs/01-scope-and-scenarios.md §4, and they are the part most likely to be wrong or to drift
/// as Microsoft changes what it exposes. Keeping them free of I/O means the table can be tested
/// directly, one case per scenario, without a container or a fake HTTP server.
/// </para>
/// <para>
/// Every negative verdict carries a reason and, where the customer can act, a remediation
/// guide. A capability that is unavailable because Microsoft exposes no API — MOSA seat prices,
/// CSP customer costs — is deliberately pointed at manual pricing rather than at a consent
/// screen that would not help (ADR-006).
/// </para>
/// </remarks>
public static class CapabilityResolver
{
    public static CapabilityResolution Resolve(
        GraphProbeResult graph,
        ArmProbeResult arm,
        BillingProbeResult billing)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(billing);

        var agreementType = ClassifyAgreement(graph, arm, billing);

        var capabilities = new Dictionary<Capability, CapabilityUnavailable?>
        {
            [Capability.GraphLicensing] = ResolveGraphLicensing(graph),
            [Capability.GraphUsage] = ResolveGraphUsage(graph),
            [Capability.ArmAccess] = ResolveArmAccess(arm),
            [Capability.CostManagement] = ResolveCostManagement(arm, agreementType),
            [Capability.BillingAccount] = ResolveBillingAccount(billing, agreementType),
            [Capability.BillingTransactions] = ResolveBillingTransactions(billing, agreementType),
            [Capability.PartnerCenter] = ResolvePartnerCenter(agreementType),
        };

        return new CapabilityResolution(
            agreementType,
            graph.IsPartnerManaged,
            graph.OwnerTenantId,
            capabilities);
    }

    /// <summary>
    /// Places the tenant in one of the commercial scenarios.
    /// </summary>
    /// <remarks>
    /// Partner management is checked first and wins outright. A CSP-managed customer can also
    /// have a billing account visible through an Azure Plan, and treating that as a direct
    /// agreement would promise prices and invoices the partner, not Microsoft, controls.
    /// </remarks>
    private static AgreementType ClassifyAgreement(
        GraphProbeResult graph,
        ArmProbeResult arm,
        BillingProbeResult billing)
    {
        if (graph.IsPartnerManaged)
        {
            return AgreementType.CspManaged;
        }

        var fromBilling = billing.PrimaryAgreementType;

        if (fromBilling is AgreementType.Mca or AgreementType.Ea or AgreementType.Mpa)
        {
            return fromBilling;
        }

        // No readable billing account. Azure's per-subscription billing property still reveals
        // the agreement, and it is readable with plain Reader — a common state for a customer
        // who granted RBAC but not a billing role.
        foreach (var subscription in arm.Subscriptions)
        {
            if (subscription.AgreementType is AgreementType.Mca or AgreementType.Ea or AgreementType.Mpa)
            {
                return subscription.AgreementType;
            }
        }

        if (fromBilling == AgreementType.Mosa)
        {
            return AgreementType.Mosa;
        }

        // Licences exist but nothing exposes a billing account: the legacy web-direct case.
        if (graph.SubscribedSkusReadable && graph.DirectorySubscriptionsReadable)
        {
            return AgreementType.Mosa;
        }

        return graph.SubscribedSkusReadable ? AgreementType.Unknown : AgreementType.NotDiscovered;
    }

    private static CapabilityUnavailable? ResolveGraphLicensing(GraphProbeResult graph)
        => graph.SubscribedSkusReadable
            ? null
            : new CapabilityUnavailable(
                Capability.GraphLicensing,
                CapabilityUnavailableReason.ConsentRevoked,
                RemediationGuide.ConnectOrganisation,
                "Directory permissions are missing or were revoked.");

    private static CapabilityUnavailable? ResolveGraphUsage(GraphProbeResult graph)
    {
        if (graph.UsageReportsReadable)
        {
            return null;
        }

        return new CapabilityUnavailable(
            Capability.GraphUsage,
            CapabilityUnavailableReason.Tier2NotGranted,
            RemediationGuide.UsageInsights,
            "Reports.Read.All has not been granted. Usage insights are an optional, separate consent.");
    }

    private static CapabilityUnavailable? ResolveArmAccess(ArmProbeResult arm)
        => arm.HasAnySubscription
            ? null
            : new CapabilityUnavailable(
                Capability.ArmAccess,
                CapabilityUnavailableReason.NoAzure,
                RemediationGuide.AzureRbac,
                "No Azure subscriptions are visible. Admin consent grants nothing on Azure; a role assignment is needed.");

    private static CapabilityUnavailable? ResolveCostManagement(ArmProbeResult arm, AgreementType agreementType)
    {
        if (arm.CostManagementQueryable)
        {
            return null;
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

        return new CapabilityUnavailable(
            Capability.CostManagement,
            CapabilityUnavailableReason.RbacMissing,
            RemediationGuide.AzureRbac,
            agreementType == AgreementType.Ea
                ? "Assign Cost Management Reader. On an Enterprise Agreement, also enable 'AO view charges' and 'DA view charges'."
                : "Assign the Cost Management Reader role to MLCP on your subscriptions or management group.");
    }

    private static CapabilityUnavailable? ResolveBillingAccount(BillingProbeResult billing, AgreementType agreementType)
    {
        if (billing.HasAnyAccount && billing.HasBillingRole)
        {
            return null;
        }

        if (agreementType == AgreementType.CspManaged)
        {
            return new CapabilityUnavailable(
                Capability.BillingAccount,
                CapabilityUnavailableReason.CspManaged,
                RemediationGuide.PartnerManaged,
                "Your licences are managed by a partner, so Microsoft holds no billing relationship with you.");
        }

        if (agreementType == AgreementType.Mosa)
        {
            return new CapabilityUnavailable(
                Capability.BillingAccount,
                CapabilityUnavailableReason.Mosa,
                RemediationGuide.ManualPricing,
                "Microsoft moves accounts like yours to the Customer Agreement at renewal; billing data unlocks then.");
        }

        if (!billing.HasAnyAccount)
        {
            return new CapabilityUnavailable(
                Capability.BillingAccount,
                CapabilityUnavailableReason.NoBillingAccount,
                RemediationGuide.BillingRole,
                "No billing account is visible to MLCP.");
        }

        return new CapabilityUnavailable(
            Capability.BillingAccount,
            CapabilityUnavailableReason.BillingRoleMissing,
            RemediationGuide.BillingRole,
            "A billing account exists but MLCP holds no billing role on it. Billing account reader is enough.");
    }

    private static CapabilityUnavailable? ResolveBillingTransactions(BillingProbeResult billing, AgreementType agreementType)
    {
        if (billing.TransactionsReadable && agreementType is AgreementType.Mca or AgreementType.Mpa)
        {
            return null;
        }

        return agreementType switch
        {
            AgreementType.CspManaged => new CapabilityUnavailable(
                Capability.BillingTransactions,
                CapabilityUnavailableReason.CspManaged,
                RemediationGuide.PartnerManaged,
                "Microsoft does not expose what your partner charges you. Enter your prices, or invite your partner."),

            AgreementType.Mosa => new CapabilityUnavailable(
                Capability.BillingTransactions,
                CapabilityUnavailableReason.Mosa,
                RemediationGuide.ManualPricing,
                "No price API exists for this agreement. Enter seat prices meanwhile; they unlock at your next renewal."),

            // An EA exposes Azure invoices and a price sheet, but not Microsoft 365 seat prices
            // at enrolment level, so the honest answer is a price-sheet upload (docs/01 §5.9).
            AgreementType.Ea => new CapabilityUnavailable(
                Capability.BillingTransactions,
                CapabilityUnavailableReason.NotMca,
                RemediationGuide.ManualPricing,
                "Enterprise Agreements do not expose Microsoft 365 seat prices through the API. Upload your price sheet."),

            AgreementType.Mca or AgreementType.Mpa => new CapabilityUnavailable(
                Capability.BillingTransactions,
                CapabilityUnavailableReason.BillingRoleMissing,
                RemediationGuide.BillingRole,
                "Grant MLCP a billing role to read transactions, which is where real seat prices come from."),

            _ => new CapabilityUnavailable(
                Capability.BillingTransactions,
                CapabilityUnavailableReason.NotDiscovered,
                RemediationGuide.ConnectOrganisation),
        };
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
}
