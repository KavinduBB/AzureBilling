using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mlcp.Domain.Audit;

/// <summary>
/// Computes the SHA-256 digest of a tenant's audit log, which the deletion certificate records
/// just before the log is purged (ADR-019).
/// </summary>
/// <remarks>
/// <para>
/// The digest shows what existed without keeping any of it. Anyone holding an export of the
/// log can recompute the digest and compare it with the certificate.
/// </para>
/// <para>
/// The canonical form is fixed and versioned, because a digest nobody can reproduce proves
/// nothing:
/// </para>
/// <list type="bullet">
/// <item>The input starts with the header line <see cref="FormatVersion"/>.</item>
/// <item>Rows follow in ascending <see cref="AuditLog.AuditLogId"/> order, one per line.</item>
/// <item>Each row is a compact JSON array with its fields in the order written by
/// <see cref="Append"/>.</item>
/// <item>Timestamps are UTC with seven fractional digits, and money is written with four
/// decimal places to match the column.</item>
/// <item>Enums are written by name, and absent values as JSON null.</item>
/// </list>
/// <para>
/// Rows are streamed into the hash one at a time, so a tenant with a very large log does not
/// have to fit in memory.
/// </para>
/// </remarks>
public sealed class AuditLogDigest : IDisposable
{
    /// <summary>Header written first. Change it whenever the canonical form changes.</summary>
    public const string FormatVersion = "mlcp-audit-digest-v1";

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _lastId = long.MinValue;
    private bool _finished;

    public AuditLogDigest()
    {
        _hash.AppendData(System.Text.Encoding.UTF8.GetBytes(FormatVersion + "\n"));
    }

    /// <summary>Number of rows appended so far.</summary>
    public long RowCount { get; private set; }

    /// <summary>Computes the digest of an in-memory sequence, sorting it first.</summary>
    public static string Compute(IEnumerable<AuditLog> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        using var digest = new AuditLogDigest();

        foreach (var row in rows.OrderBy(r => r.AuditLogId))
        {
            digest.Append(row);
        }

        return digest.Finish();
    }

    /// <summary>Adds the next row. Rows must arrive in strictly ascending id order.</summary>
    /// <exception cref="InvalidOperationException">A row arrived out of order, or the digest is finished.</exception>
    public void Append(AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ObjectDisposedException.ThrowIf(_finished, this);

        if (row.AuditLogId <= _lastId)
        {
            // Out-of-order input would give a digest nobody could reproduce, so it is refused.
            throw new InvalidOperationException("Audit rows must be digested in ascending AuditLogId order.");
        }

        _lastId = row.AuditLogId;

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(row.AuditLogId);
            writer.WriteStringValue(row.TenantId.ToString("D"));
            WriteNullable(writer, row.AttemptAuditLogId);
            WriteString(writer, row.ActorObjectId?.ToString("D"));
            WriteString(writer, row.ActorUpn);
            writer.WriteStringValue(row.Action.ToString());
            writer.WriteStringValue(row.EntityType);
            WriteString(writer, row.EntityId);
            WriteString(writer, row.OldValue);
            WriteString(writer, row.NewValue);
            writer.WriteStringValue(Timestamp(row.OccurredUtc));
            WriteString(writer, row.SourceIp);
            writer.WriteStringValue(row.CorrelationId);
            writer.WriteStringValue(row.Outcome.ToString());
            WriteString(writer, row.Detail);
            WriteString(writer, row.FinancialImpactAmount?.ToString("0.0000", CultureInfo.InvariantCulture));
            WriteString(writer, row.FinancialImpactCurrency);
            writer.WriteStringValue(Timestamp(row.CreatedUtc));
            writer.WriteEndArray();
        }

        buffer.WriteByte((byte)'\n');
        _hash.AppendData(buffer.GetBuffer(), 0, (int)buffer.Length);
        RowCount++;
    }

    /// <summary>Returns the digest as 64 lowercase hex characters. Can be called only once.</summary>
    public string Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        return Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    public void Dispose()
    {
        _finished = true;
        _hash.Dispose();
    }

    private static string Timestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void WriteString(Utf8JsonWriter writer, string? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
