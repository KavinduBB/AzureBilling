using Xunit;

namespace Mlcp.IntegrationTests.Infrastructure;

/// <summary>
/// Detects whether a container runtime is reachable, so the container-backed suites skip with
/// an explanation on a machine without Docker instead of failing with a connection error.
/// </summary>
/// <remarks>
/// Skipping is only acceptable because these suites are required in CI, where Docker is
/// present. A skipped tenant-isolation test on a developer laptop is a convenience; a skipped
/// one in the pipeline would be a hole, so the CI workflow asserts they actually ran.
/// </remarks>
public static class DockerAvailability
{
    private static readonly Lazy<bool> Detected = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public const string SkipReason =
        "No container runtime detected. These tests need Docker; they are required in CI.";

    /// <summary>
    /// Set <c>MLCP_REQUIRE_DOCKER</c> to turn a skip into a failure. CI sets it, so a pipeline
    /// whose Docker service failed to start reports a broken build rather than a green run in
    /// which the tenant-isolation suite quietly did nothing.
    /// </summary>
    public static bool IsRequired =>
        Environment.GetEnvironmentVariable("MLCP_REQUIRE_DOCKER") is { Length: > 0 } value
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "0", StringComparison.Ordinal);

    /// <summary>
    /// A disposable SQL Server supplied instead of a container (<c>MLCP_TEST_SQL</c>). The
    /// database named in it is dropped and re-created by the fixture, so it must be one that
    /// exists only for tests.
    /// </summary>
    public static string? ExternalSqlConnectionString =>
        Environment.GetEnvironmentVariable("MLCP_TEST_SQL") is { Length: > 0 } value ? value : null;

    /// <summary>True when the SQL-backed suites can run: a container runtime or an external server.</summary>
    public static bool IsAvailable => ExternalSqlConnectionString is not null || Detected.Value;

    /// <summary>Throws when Docker is required but missing, naming the cause.</summary>
    public static void ThrowIfRequiredButMissing()
    {
        if (IsRequired && !IsAvailable)
        {
            throw new InvalidOperationException(
                "MLCP_REQUIRE_DOCKER is set but no container runtime was detected. The tenant-isolation "
                + "suite cannot run, and a build without it must not be reported as passing.");
        }
    }

    private static bool Detect()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return File.Exists(@"\\.\pipe\docker_engine") || FindOnPath("docker.exe");
        }

        return File.Exists("/var/run/docker.sock") || FindOnPath("docker");
    }

    private static bool FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.Split(Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Any(directory => SafeExists(directory, executable));
    }

    private static bool SafeExists(string directory, string executable)
    {
        try
        {
            return File.Exists(Path.Combine(directory, executable));
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry is not a reason to fail the whole test run.
            return false;
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips when no container runtime is available.</summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable && !DockerAvailability.IsRequired)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when no container runtime is available.</summary>
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable && !DockerAvailability.IsRequired)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
