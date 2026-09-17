using Mlcp.Web.Models;

namespace Mlcp.Web.Infrastructure;

/// <summary>Builds the region statement and picker shown on the landing and connect pages (ADR-021).</summary>
public sealed class RegionPicker
{
    /// <summary>Remembers a visitor's region choice on this host.</summary>
    public const string PreferenceCookie = "mlcp-region";

    private readonly DeploymentOptions _deployment;
    private readonly MlcpWebOptions _options;

    public RegionPicker(DeploymentOptions deployment, MlcpWebOptions options)
    {
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string CurrentRegion => _deployment.Region;

    public RegionChoiceViewModel Current()
        => new(_deployment.Region, _options.RegionName(_deployment.Region), Alternatives());

    public LandingViewModel Landing(string? preferredRegion)
    {
        var alternatives = Alternatives();
        var preferred = preferredRegion is null
            ? null
            : alternatives.FirstOrDefault(a => string.Equals(a.Code, preferredRegion, StringComparison.OrdinalIgnoreCase));

        return new LandingViewModel(Link(_deployment.Region), alternatives, preferred);
    }

    /// <summary>True when <paramref name="region"/> is this stack or a configured regional host.</summary>
    public bool IsKnown(string region)
        => string.Equals(region, _deployment.Region, StringComparison.OrdinalIgnoreCase)
            || _options.RegionUrl(region, "/") is not null;

    private List<RegionLinkViewModel> Alternatives()
        => [.. _options.RegionHosts.Keys
            .Where(code => !string.Equals(code, _deployment.Region, StringComparison.OrdinalIgnoreCase))
            .Where(code => _options.RegionUrl(code, "/") is not null)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(Link)];

    private RegionLinkViewModel Link(string code)
        => new(code, _options.RegionName(code), "/region/" + Uri.EscapeDataString(code));
}
