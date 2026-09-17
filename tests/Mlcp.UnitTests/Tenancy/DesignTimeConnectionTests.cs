using FluentAssertions;
using Microsoft.Data.SqlClient;
using Mlcp.Persistence;

namespace Mlcp.UnitTests.Tenancy;

/// <summary>CLAUDE.md rule 4: no secrets in code, including the design-time fallback.</summary>
public class DesignTimeConnectionTests
{
    [Fact]
    public void The_fallback_connection_carries_no_password()
    {
        var builder = new SqlConnectionStringBuilder(MlcpDbContextFactory.PasswordlessFallbackConnection);

        builder.Password.Should().BeEmpty();
        builder.UserID.Should().BeEmpty();
        builder.IntegratedSecurity.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_variable_falls_back_to_integrated_security(string? configured)
    {
        MlcpDbContextFactory.ResolveConnectionString(configured)
            .Should().Be(MlcpDbContextFactory.PasswordlessFallbackConnection);
    }

    [Fact]
    public void The_variable_wins_when_set()
    {
        const string configured = "Server=ci;Database=MlcpCi;Integrated Security=true";

        MlcpDbContextFactory.ResolveConnectionString(configured).Should().Be(configured);
    }

    [Fact]
    public void The_factory_builds_a_context_without_touching_the_database()
    {
        using var context = new MlcpDbContextFactory().CreateDbContext([]);

        context.IsSystemContext.Should().BeTrue();
        context.Model.Should().NotBeNull();
    }
}
