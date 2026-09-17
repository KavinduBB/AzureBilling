using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>One row of the onboarding checklist.</summary>
/// <param name="Key">Stable identifier for links and telemetry.</param>
/// <param name="Title">Short label, for example "Unlock Azure costs".</param>
/// <param name="Unlocks">What the customer gets by completing it.</param>
/// <param name="IsComplete">Whether the capability is available.</param>
/// <param name="Unavailable">The typed reason, when it is not.</param>
/// <param name="IsActionable">
/// False when nothing the customer does can change the outcome — a MOSA tenant cannot unlock
/// seat prices, and showing them a "fix this" button would be a lie.
/// </param>
public sealed record ChecklistItem(
    string Key,
    string Title,
    string Unlocks,
    bool IsComplete,
    CapabilityUnavailable? Unavailable,
    bool IsActionable)
{
    public RemediationGuide Guide => Unavailable?.Guide ?? RemediationGuide.None;

    public string? Detail => Unavailable?.Detail;
}

/// <summary>
/// The onboarding checklist, built from the capability profile (P0-9).
/// </summary>
/// <remarks>
/// <para>
/// The three authorisation systems appear as three separate rows because they are genuinely
/// independent: Entra consent grants nothing on Azure, and Azure RBAC grants nothing on billing
/// scopes. Collapsing them into one "connect" step is the single most common way a customer
/// ends up believing they have finished when they have not (docs/03-architecture.md §4.1).
/// </para>
/// <para>
/// Rows whose capability is blocked by the agreement rather than by a missing grant are marked
/// not actionable, so the UI can explain instead of prompting.
/// </para>
/// </remarks>
public sealed record OnboardingChecklist(
    Guid TenantId,
    AgreementType AgreementType,
    bool IsPartnerManaged,
    IReadOnlyList<ChecklistItem> Items)
{
    /// <summary>The Guide C prompt for a tenant whose agreement cannot be seen yet (ADR-020).</summary>
    public const string UndeterminedBillingCopy =
        "We can't see how your organisation buys Microsoft licences yet. If you have a Microsoft Customer "
        + "Agreement, grant billing read access to unlock prices and invoices. If you bought online before "
        + "2023, pricing may not be available yet — you can enter seat prices meanwhile.";

    public int CompletedCount => Items.Count(i => i.IsComplete);

    /// <summary>
    /// True when entering seat prices by hand is offered: every tenant except an MCA tenant whose
    /// billing role is already granted, because that tenant gets real prices (ADR-020, ADR-006).
    /// </summary>
    public bool OffersManualPricing =>
        !(AgreementType == AgreementType.Mca && Items.Any(i => i.Key == BillingRoleKey && i.IsComplete));

    private const string BillingRoleKey = "billing-role";

    /// <summary>Items still worth prompting the customer about.</summary>
    public IReadOnlyList<ChecklistItem> OutstandingActions =>
        [.. Items.Where(i => !i.IsComplete && i.IsActionable)];

    /// <summary>Items that will never complete for this tenant, with the reason.</summary>
    public IReadOnlyList<ChecklistItem> Explanations =>
        [.. Items.Where(i => !i.IsComplete && !i.IsActionable)];

    public static OnboardingChecklist Build(Tenant tenant, TenantCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(profile);

        return new OnboardingChecklist(
            tenant.TenantId,
            tenant.AgreementTypePrimary,
            tenant.IsPartnerManaged,
            [
                Item(
                    profile,
                    Capability.GraphLicensing,
                    "graph-consent",
                    "Connect your organisation",
                    "Licences, who has what, renewal dates and waste detection."),

                Item(
                    profile,
                    Capability.CostManagement,
                    "azure-rbac",
                    "Unlock Azure costs",
                    "Azure spend by subscription, resource group, resource and tag, with forecast."),

                BillingItem(
                    tenant,
                    Item(
                        profile,
                        Capability.BillingTransactions,
                        BillingRoleKey,
                        "Unlock prices and invoices",
                        "What you actually pay per seat, monthly invoices, auto-renew and term dates.")),

                Item(
                    profile,
                    Capability.GraphUsage,
                    "usage-reports",
                    "Turn on usage insights",
                    "Which licensed services people actually use, and who is paying for an unused seat."),
            ]);
    }

    private static ChecklistItem Item(
        TenantCapabilityProfile profile,
        Capability capability,
        string key,
        string title,
        string unlocks)
    {
        var unavailable = profile.GetUnavailable(capability);

        return new ChecklistItem(
            key,
            title,
            unlocks,
            IsComplete: unavailable is null,
            unavailable,
            IsActionable: unavailable is null || IsActionable(unavailable.Reason));
    }

    /// <summary>
    /// For an <see cref="AgreementType.Undetermined"/> tenant, Guide C is the one step that can
    /// reveal the agreement, so it is always offered, whatever the probe concluded (ADR-020).
    /// A tenant that is actually MOSA or CSP-managed is classified on positive evidence and never
    /// reaches this branch.
    /// </summary>
    private static ChecklistItem BillingItem(Tenant tenant, ChecklistItem item)
    {
        if (item.IsComplete || tenant.AgreementTypePrimary != AgreementType.Undetermined)
        {
            return item;
        }

        var reason = item.Unavailable?.Reason ?? CapabilityUnavailableReason.BillingRoleMissing;

        return item with
        {
            Unavailable = new CapabilityUnavailable(
                Capability.BillingTransactions,
                reason,
                RemediationGuide.BillingRole,
                UndeterminedBillingCopy),
            IsActionable = true,
        };
    }

    /// <summary>
    /// Whether a customer action can plausibly resolve this reason.
    /// </summary>
    /// <remarks>
    /// The unactionable set is where Microsoft, not permissions, is the constraint. Prompting
    /// on any of these sends the customer to a portal screen that cannot help, which erodes
    /// trust in the rest of the checklist.
    /// </remarks>
    private static bool IsActionable(CapabilityUnavailableReason reason) => reason switch
    {
        CapabilityUnavailableReason.Mosa => false,
        CapabilityUnavailableReason.CspManaged => false,
        CapabilityUnavailableReason.ClassicCsp => false,
        CapabilityUnavailableReason.NotMca => false,
        CapabilityUnavailableReason.IndirectReseller => false,
        CapabilityUnavailableReason.NotPartner => false,
        _ => true,
    };
}
