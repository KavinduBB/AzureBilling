using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mlcp.Integration.Azure.Regions;
using Mlcp.UnitTests.Onboarding;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;

namespace Mlcp.UnitTests.Web;

/// <summary>ADR-021: a signed-in user is served by the region that holds their tenant.</summary>
public sealed class RegionRoutingMiddlewareTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");

    private readonly FakeRegionDirectory _directory = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly DeploymentOptions _deployment = new("eu");

    private readonly MlcpWebOptions _options = new()
    {
        RegionHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eu"] = "eu.app.example.com",
            ["us"] = "us.app.example.com",
        },
    };

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task A_tenant_registered_elsewhere_is_redirected_to_the_same_path_there_and_signed_out_here()
    {
        _directory.Regions[TenantA] = "us";
        var (context, next) = Build(signedIn: true, path: "/onboarding", query: "?x=1");

        await Invoke(context, next);

        next.Called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be("https://us.app.example.com/onboarding?x=1");
        context.Response.Headers.SetCookie.ToString().Should().Contain(".AspNetCore.Cookies=;", "the local session cookie is cleared");
    }

    [Fact]
    public async Task A_tenant_registered_here_continues()
    {
        _directory.Regions[TenantA] = "EU";
        var (context, next) = Build(signedIn: true);

        await Invoke(context, next);

        next.Called.Should().BeTrue();
    }

    [Fact]
    public async Task An_unregistered_tenant_continues_here()
    {
        var (context, next) = Build(signedIn: true);

        await Invoke(context, next);

        next.Called.Should().BeTrue();
    }

    [Fact]
    public async Task Anonymous_requests_are_not_looked_up()
    {
        _directory.Regions[TenantA] = "us";
        var (context, next) = Build(signedIn: false);

        await Invoke(context, next);

        next.Called.Should().BeTrue();
    }

    [Fact]
    public async Task A_region_with_no_configured_host_is_refused_rather_than_served_here()
    {
        _directory.Regions[TenantA] = "apac";
        var (context, next) = Build(signedIn: true);

        await Invoke(context, next);

        next.Called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status421MisdirectedRequest);
    }

    [Fact]
    public async Task Lookups_are_cached()
    {
        var counting = new CountingDirectory(_directory);
        _directory.Regions[TenantA] = "eu";

        await RegionRoutingMiddleware.LookupAsync(counting, _cache, TenantA, CancellationToken.None);
        await RegionRoutingMiddleware.LookupAsync(counting, _cache, TenantA, CancellationToken.None);

        counting.Lookups.Should().Be(1);

        RegionRoutingMiddleware.Invalidate(_cache, TenantA);
        await RegionRoutingMiddleware.LookupAsync(counting, _cache, TenantA, CancellationToken.None);

        counting.Lookups.Should().Be(2);
    }

    [Fact]
    public async Task The_in_memory_directory_lets_the_first_region_win()
    {
        var directory = new InMemoryRegionDirectory();

        var first = await directory.TryRegisterAsync(TenantA, "eu", CancellationToken.None);
        var second = await directory.TryRegisterAsync(TenantA, "us", CancellationToken.None);

        first.Should().Be(new Mlcp.Application.Onboarding.RegionRegistration("eu", true));
        second.Should().Be(new Mlcp.Application.Onboarding.RegionRegistration("eu", false));
        second.IsElsewhere("us").Should().BeTrue();
        (await directory.GetRegionAsync(TenantA, CancellationToken.None)).Should().Be("eu");
    }

    [Theory]
    [InlineData("https://acct.table.core.windows.net/TenantRegions", "https://acct.table.core.windows.net/", "TenantRegions")]
    [InlineData("https://acct.table.core.windows.net/TenantRegions/", "https://acct.table.core.windows.net/", "TenantRegions")]
    public void The_table_uri_is_split_into_endpoint_and_table(string uri, string endpoint, string table)
    {
        var (serviceUri, tableName) = RegionDirectoryServiceCollectionExtensions.SplitTableUri(new Uri(uri));

        serviceUri.Should().Be(new Uri(endpoint));
        tableName.Should().Be(table);
    }

    [Theory]
    [InlineData("http://acct.table.core.windows.net/TenantRegions")]
    [InlineData("https://acct.table.core.windows.net/")]
    [InlineData("https://acct.table.core.windows.net/a/b")]
    public void A_table_uri_that_does_not_name_one_https_table_is_rejected(string uri)
    {
        var act = () => RegionDirectoryServiceCollectionExtensions.SplitTableUri(new Uri(uri));

        act.Should().Throw<ArgumentException>();
    }

    private Task Invoke(HttpContext context, NextRecorder next)
        => new RegionRoutingMiddleware(next.Invoke, NullLogger<RegionRoutingMiddleware>.Instance)
            .InvokeAsync(context, _directory, _cache, _deployment, _options);

    private static (DefaultHttpContext Context, NextRecorder Next) Build(bool signedIn, string path = "/", string query = "")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };

        context.Request.Scheme = "https";
        context.Request.Host = new HostString("eu.app.example.com");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        if (signedIn)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", TenantA.ToString()), new Claim("oid", Guid.NewGuid().ToString())],
                authenticationType: "Test"));
        }

        return (context, new NextRecorder());
    }

    private sealed class NextRecorder
    {
        public bool Called { get; private set; }

        public Task Invoke(HttpContext context)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingDirectory(FakeRegionDirectory inner) : Mlcp.Application.Onboarding.IRegionDirectory
    {
        public int Lookups { get; private set; }

        public Task<string?> GetRegionAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            Lookups++;
            return inner.GetRegionAsync(tenantId, cancellationToken);
        }

        public Task<Mlcp.Application.Onboarding.RegionRegistration> TryRegisterAsync(
            Guid tenantId,
            string region,
            CancellationToken cancellationToken)
            => inner.TryRegisterAsync(tenantId, region, cancellationToken);
    }
}
