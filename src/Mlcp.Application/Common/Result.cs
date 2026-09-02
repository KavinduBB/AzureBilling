using System.Diagnostics.CodeAnalysis;
using Mlcp.Domain.Capabilities;

namespace Mlcp.Application.Common;

/// <summary>
/// The result of an operation that depends on a Microsoft data source: either a value, or the
/// typed reason that data source is unavailable for this tenant.
/// </summary>
/// <remarks>
/// <para>
/// The type exists to make "we have no data" unrepresentable without an explanation. A null,
/// an empty list or a zero would all render as a blank chart, and a blank chart tells a
/// customer nothing about whether they have no spend, have not granted a role, or are on an
/// agreement Microsoft exposes no prices for (CLAUDE.md rule 9, ADR-002).
/// </para>
/// <para>
/// Views branch on <see cref="IsAvailable"/> and render the remediation guide when it is false.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Available and NotAvailable are the canonical constructors for a result type. "
        + "The rule's concern is discoverability without type inference, which the non-generic "
        + "Result helper class below addresses.")]
public readonly struct Result<T> : IEquatable<Result<T>>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Unavailable = null;
    }

    private Result(CapabilityUnavailable unavailable)
    {
        _value = default;
        Unavailable = unavailable;
    }

    /// <summary>True when <see cref="Value"/> holds data.</summary>
    [MemberNotNullWhen(false, nameof(Unavailable))]
    public bool IsAvailable => Unavailable is null;

    /// <summary>Why the data could not be produced, or null when it could.</summary>
    public CapabilityUnavailable? Unavailable { get; }

    /// <summary>The value. Throws when the capability was unavailable.</summary>
    public T Value => IsAvailable
        ? _value!
        : throw new InvalidOperationException(
            $"No value: capability {Unavailable!.Capability} is unavailable ({Unavailable.Reason}).");

    public static Result<T> Available(T value) => new(value);

    public static Result<T> NotAvailable(CapabilityUnavailable unavailable)
        => new(unavailable ?? throw new ArgumentNullException(nameof(unavailable)));

    public static implicit operator Result<T>(T value) => Available(value);

    public static implicit operator Result<T>(CapabilityUnavailable unavailable) => NotAvailable(unavailable);

    /// <summary>Projects the value, carrying an unavailable reason through untouched.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsAvailable ? Result<TOut>.Available(map(_value!)) : Result<TOut>.NotAvailable(Unavailable!);
    }

    /// <summary>Collapses both cases into one value, so callers cannot forget the unavailable branch.</summary>
    public TOut Match<TOut>(Func<T, TOut> onAvailable, Func<CapabilityUnavailable, TOut> onUnavailable)
    {
        ArgumentNullException.ThrowIfNull(onAvailable);
        ArgumentNullException.ThrowIfNull(onUnavailable);
        return IsAvailable ? onAvailable(_value!) : onUnavailable(Unavailable!);
    }

    /// <summary>The value, or <paramref name="fallback"/> when unavailable.</summary>
    public T ValueOr(T fallback) => IsAvailable ? _value! : fallback;

    public bool Equals(Result<T> other)
        => EqualityComparer<CapabilityUnavailable?>.Default.Equals(Unavailable, other.Unavailable)
            && EqualityComparer<T?>.Default.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_value, Unavailable);

    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}

/// <summary>Helpers that let call sites omit the type argument.</summary>
public static class Result
{
    public static Result<T> Available<T>(T value) => Result<T>.Available(value);

    public static Result<T> NotAvailable<T>(CapabilityUnavailable unavailable) => Result<T>.NotAvailable(unavailable);

    public static Result<T> NotAvailable<T>(
        Capability capability,
        CapabilityUnavailableReason reason,
        RemediationGuide guide,
        string? detail = null)
        => Result<T>.NotAvailable(new CapabilityUnavailable(capability, reason, guide, detail));
}
