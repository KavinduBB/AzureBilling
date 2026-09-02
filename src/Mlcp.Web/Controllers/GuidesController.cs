using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mlcp.Web.Controllers;

/// <summary>
/// The customer-facing remediation guides linked from the onboarding checklist.
/// </summary>
/// <remarks>
/// One route per <c>RemediationGuide</c> value, so a capability's typed reason leads to a page
/// that actually addresses it. The copy is the authoritative text in docs/07-onboarding-guides.md;
/// keeping the two in step is a review requirement rather than a build one, so any change here
/// should be mirrored there.
/// </remarks>
[AllowAnonymous]
[Route("guides")]
public sealed class GuidesController : Controller
{
    /// <summary>Guide A — Graph admin consent.</summary>
    [HttpGet("connect-organisation")]
    public IActionResult ConnectOrganisation() => View();

    /// <summary>Guide B — Azure RBAC for Cost Management.</summary>
    [HttpGet("azure-costs")]
    public IActionResult AzureCosts() => View();

    /// <summary>Guide C — billing role for prices and invoices.</summary>
    [HttpGet("prices-and-invoices")]
    public IActionResult PricesAndInvoices() => View();

    /// <summary>Guide D — usage insights and the anonymisation setting.</summary>
    [HttpGet("usage-insights")]
    public IActionResult UsageInsights() => View();

    /// <summary>Guide E — licences that come from a CSP partner.</summary>
    [HttpGet("partner-managed")]
    public IActionResult PartnerManaged() => View();
}
