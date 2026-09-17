using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Web.Infrastructure;

namespace Mlcp.UnitTests.Web;

/// <summary>
/// ADR-018 rule 1: the consent state is bound to one person in one tenant, expires in ten
/// minutes, and works once.
/// </summary>
public class ConsentStateProtectorTests
{
    private static readonly SignedInUser Admin = new(
        Guid.Parse("a0000000-0000-0000-0000-00000000000a"),
        Guid.Parse("00000000-0000-0000-0000-0000000000a2"),
        "admin@contoso.example",
        "Adam Admin",
        IsDirectoryAdmin: true);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingCache _cache = new();
    private readonly ConsentStateProtector _protector;

    public ConsentStateProtectorTests()
    {
        _protector = new ConsentStateProtector(new EphemeralDataProtectionProvider(), _cache, _clock);
    }

    [Fact]
    public async Task A_fresh_state_is_valid_once_for_the_person_who_started_it()
    {
        var state = await _protector.CreateAsync(Admin, "eu", "corr-1", ConsentFlow.Core, CancellationToken.None);

        var result = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.State.Should().Be(new ConsentState(Admin.TenantId, Admin.ObjectId, "eu", "corr-1", ConsentFlow.Core));
    }

    [Fact]
    public async Task A_replayed_state_is_refused()
    {
        var state = await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);
        await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        var replay = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        replay.Status.Should().Be(ConsentStateStatus.Replayed);
        replay.State.Should().BeNull();
    }

    [Fact]
    public async Task Another_person_in_the_same_tenant_cannot_use_it_or_burn_it()
    {
        var state = await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);
        var colleague = Admin with { ObjectId = Guid.NewGuid() };

        var stolen = await _protector.ConsumeAsync(state, colleague, ConsentFlow.Core, CancellationToken.None);
        var legitimate = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        stolen.Status.Should().Be(ConsentStateStatus.WrongUser);
        legitimate.IsValid.Should().BeTrue("a refused attempt must not consume the nonce");
    }

    [Fact]
    public async Task Someone_in_another_tenant_cannot_use_it()
    {
        var state = await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);
        var outsider = Admin with { TenantId = Guid.NewGuid() };

        var result = await _protector.ConsumeAsync(state, outsider, ConsentFlow.Core, CancellationToken.None);

        result.Status.Should().Be(ConsentStateStatus.WrongTenant);
    }

    [Fact]
    public async Task A_state_older_than_ten_minutes_is_refused()
    {
        var state = await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var result = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        result.Status.Should().Be(ConsentStateStatus.Expired);
    }

    [Fact]
    public async Task A_state_whose_nonce_was_never_stored_is_refused()
    {
        // A valid-looking state from another deployment, or one whose nonce was evicted.
        var state = await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);

        foreach (var key in await NonceKeysAsync(state))
        {
            await _cache.RemoveAsync(key);
        }

        var result = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        result.Status.Should().Be(ConsentStateStatus.Replayed);
    }

    [Fact]
    public async Task A_core_state_cannot_complete_the_usage_insights_flow()
    {
        var state = await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);

        var result = await _protector.ConsumeAsync(state, Admin, ConsentFlow.UsageInsights, CancellationToken.None);

        result.Status.Should().Be(ConsentStateStatus.WrongFlow);
    }

    [Theory]
    [InlineData(null, ConsentStateStatus.Missing)]
    [InlineData("", ConsentStateStatus.Missing)]
    [InlineData("forged", ConsentStateStatus.Invalid)]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAA", ConsentStateStatus.Invalid)]
    public async Task Forged_or_missing_states_are_refused(string? state, ConsentStateStatus expected)
    {
        var result = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        result.Status.Should().Be(expected);
    }

    [Fact]
    public async Task A_state_protected_under_a_different_key_ring_is_refused()
    {
        var foreign = new ConsentStateProtector(new EphemeralDataProtectionProvider(), _cache, _clock);
        var state = await foreign.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);

        var result = await _protector.ConsumeAsync(state, Admin, ConsentFlow.Core, CancellationToken.None);

        result.Status.Should().Be(ConsentStateStatus.Invalid);
    }

    [Fact]
    public async Task The_nonce_is_stored_under_its_documented_key_and_expires_with_the_state()
    {
        await _protector.CreateAsync(Admin, "eu", "corr", ConsentFlow.Core, CancellationToken.None);

        var (key, options) = _cache.Writes.Should().ContainSingle().Subject;
        key.Should().StartWith(ConsentStateProtector.NonceKeyPrefix);
        options.AbsoluteExpirationRelativeToNow.Should().Be(ConsentStateProtector.Lifetime);
    }

    private Task<IEnumerable<string>> NonceKeysAsync(string state)
    {
        _ = state;
        return Task.FromResult(_cache.Writes.Select(w => w.Key));
    }

    /// <summary>A distributed cache that remembers what was written, since the nonce is not exposed.</summary>
    private sealed class RecordingCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));

        public List<(string Key, DistributedCacheEntryOptions Options)> Writes { get; } = [];

        public byte[]? Get(string key) => _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Writes.Add((key, options));
            _inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Writes.Add((key, options));
            return _inner.SetAsync(key, value, options, token);
        }
    }
}
