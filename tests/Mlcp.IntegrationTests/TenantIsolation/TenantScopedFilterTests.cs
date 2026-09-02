using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mlcp.Shared.Tenancy;
using Mlcp.Web.Infrastructure;
using Xunit;

namespace Mlcp.IntegrationTests.TenantIsolation;

/// <summary>
/// Isolation layer 4: an endpoint that names another tenant answers 404, never 403.
/// </summary>
/// <remarks>
/// The distinction is the whole point. 403 tells the caller the resource exists and belongs to
/// someone else, which turns any such endpoint into an enumeration oracle. 404 makes another
/// customer's data indistinguishable from data that never existed.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class TenantScopedFilterTests
{
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private sealed record RequestWithTenant(Guid TenantId, string Note);

    private static ActionExecutingContext BuildContext(
        ITenantContext tenantContext,
        IDictionary<string, object?>? routeValues = null,
        IDictionary<string, object?>? actionArguments = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        services.AddLogging();

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        var routeData = new RouteData();

        foreach (var (key, value) in routeValues ?? new Dictionary<string, object?>())
        {
            routeData.Values[key] = value;
        }

        var actionContext = new ActionContext(httpContext, routeData, new ControllerActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            [],
            actionArguments ?? new Dictionary<string, object?>(),
            controller: null!);
    }

    [Fact]
    public void A_route_naming_another_tenant_returns_404()
    {
        var context = BuildContext(
            FixedTenantContext.For(TenantA),
            routeValues: new Dictionary<string, object?> { ["tenantId"] = TenantB.ToString() });

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void A_route_naming_another_tenant_never_returns_403()
    {
        var context = BuildContext(
            FixedTenantContext.For(TenantA),
            routeValues: new Dictionary<string, object?> { ["tenantId"] = TenantB.ToString() });

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().NotBeOfType<ForbidResult>();
        context.Result.Should().NotBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void A_route_naming_the_callers_own_tenant_is_allowed()
    {
        var context = BuildContext(
            FixedTenantContext.For(TenantA),
            routeValues: new Dictionary<string, object?> { ["tenantId"] = TenantA.ToString() });

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().BeNull();
    }

    [Fact]
    public void A_bound_model_carrying_another_tenant_returns_404()
    {
        // A tenant id smuggled in a request body is treated exactly like one in the route.
        var context = BuildContext(
            FixedTenantContext.For(TenantA),
            actionArguments: new Dictionary<string, object?>
            {
                ["request"] = new RequestWithTenant(TenantB, "please"),
            });

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void An_action_argument_naming_another_tenant_returns_404()
    {
        var context = BuildContext(
            FixedTenantContext.For(TenantA),
            actionArguments: new Dictionary<string, object?> { ["tenantId"] = TenantB });

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void A_request_naming_a_tenant_while_unauthenticated_returns_404()
    {
        // No tenant in scope plus a tenant in the URL is still a probe, and must answer the
        // same way as a mismatch so the two cannot be told apart.
        var context = BuildContext(
            new TenantContext(),
            routeValues: new Dictionary<string, object?> { ["tenantId"] = TenantB.ToString() });

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void A_request_naming_no_tenant_is_left_alone()
    {
        // Most endpoints never mention a tenant; they are protected by layers 1 to 3 and must
        // not be blocked here.
        var context = BuildContext(FixedTenantContext.For(TenantA));

        new TenantScopedAttribute().OnActionExecuting(context);

        context.Result.Should().BeNull();
    }
}
