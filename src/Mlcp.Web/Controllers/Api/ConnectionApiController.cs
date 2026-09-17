using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Web.Controllers.Api;

/// <summary>The tenant's connection state, for the status pages to poll.</summary>
/// <param name="TenantId">The caller's tenant.</param>
/// <param name="Status">The tenant lifecycle state.</param>
/// <param name="AgreementType">The detected agreement, or <c>Undetermined</c>.</param>
/// <param name="ConsentGrantedUtc">When consent was verified; null until then.</param>
public sealed record ConnectionStatusDto(Guid TenantId, string Status, string AgreementType, DateTimeOffset? ConsentGrantedUtc);

/// <summary>
/// Read-only connection status. Reads SQL only (CLAUDE.md rule 5).
/// </summary>
/// <remarks>
/// The route names a tenant, so isolation layer 4 applies: <see cref="Infrastructure.TenantScopedAttribute"/>
/// answers 404 when it is not the caller's. The data is then selected by the tenant context,
/// never by the route value.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1/tenants/{tenantId:guid}")]
public sealed class ConnectionApiController : ControllerBase
{
    private readonly IOnboardingRepository _repository;
    private readonly ITenantContext _tenantContext;

    public ConnectionApiController(IOnboardingRepository repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    [HttpGet("connection")]
    public async Task<ActionResult<ConnectionStatusDto>> GetConnection(Guid tenantId, CancellationToken cancellationToken)
    {
        _ = tenantId; // validated by TenantScopedAttribute; never used to select data

        var tenant = await _repository.FindTenantAsync(_tenantContext.RequireTenantId(), cancellationToken);

        if (tenant is null)
        {
            return NotFound();
        }

        return new ConnectionStatusDto(
            tenant.TenantId,
            tenant.Status.ToString(),
            tenant.AgreementTypePrimary.ToString(),
            tenant.ConsentGrantedUtc);
    }
}
