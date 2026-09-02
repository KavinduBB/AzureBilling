using System.Globalization;
using System.Net;
using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Web.Services;

/// <summary>Configuration for outbound onboarding email.</summary>
public sealed record EmailOptions
{
    /// <summary>Azure Communication Services endpoint, for example <c>https://mlcp-comms.communication.azure.com</c>.</summary>
    public Uri? CommunicationServiceEndpoint { get; init; }

    /// <summary>Verified sender address on the Communication Services domain.</summary>
    public string? SenderAddress { get; init; }

    /// <summary>True when a real sender is configured.</summary>
    public bool IsConfigured =>
        CommunicationServiceEndpoint is not null && !string.IsNullOrWhiteSpace(SenderAddress);
}

/// <summary>
/// Sends the consent request through Azure Communication Services.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated with the host's Managed Identity rather than a connection string, so no
/// sending credential exists in configuration to be leaked or rotated (CLAUDE.md rule 4).
/// </para>
/// <para>
/// The message is deliberately plain. It goes to an administrator who has not heard of us,
/// asks for directory permissions, and will be judged as a possible phishing attempt — so it
/// states who asked, what is being requested, and links only to our own domain.
/// </para>
/// </remarks>
public sealed class AzureCommunicationConsentEmailSender : IConsentEmailSender
{
    private readonly EmailClient _client;
    private readonly EmailOptions _options;
    private readonly ILogger<AzureCommunicationConsentEmailSender> _logger;

    public AzureCommunicationConsentEmailSender(
        EmailOptions options,
        ILogger<AzureCommunicationConsentEmailSender> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!_options.IsConfigured)
        {
            throw new ArgumentException("Communication Services endpoint and sender address are required.", nameof(options));
        }

        _client = new EmailClient(_options.CommunicationServiceEndpoint, new DefaultAzureCredential());
    }

    public async Task SendConsentRequestAsync(
        PendingConsentRequest request,
        string consentLandingUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(consentLandingUrl);

        var message = new EmailMessage(
            _options.SenderAddress,
            new EmailRecipients([new EmailAddress(request.SentToEmail)]),
            new EmailContent($"{request.RequestedByUpn} is asking you to connect your Microsoft tenant to MLCP")
            {
                PlainText = BuildPlainText(request, consentLandingUrl),
                Html = BuildHtml(request, consentLandingUrl),
            });

        // Started, not awaited to completion: delivery takes seconds to minutes and the user is
        // waiting on a page. Failure to queue is what matters here and does throw.
        await _client.SendAsync(WaitUntil.Started, message, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued consent request email for tenant {TenantId} (send #{SendCount}).",
            request.TenantId,
            request.SendCount);
    }

    private static string BuildPlainText(PendingConsentRequest request, string url) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
        {request.RequestedByUpn} would like to connect your organisation to MLCP, a licence and
        cost management tool for Microsoft 365 and Azure.

        MLCP asks to read two things:
          - your organisation's purchased licences and renewal dates
          - your users' basic profiles, so it can show which licences are assigned to whom

        It cannot read email, files or messages, cannot change anything, and cannot buy, assign
        or remove licences with these permissions. You can revoke access at any time from the
        Entra admin centre under Enterprise applications.

        To review the permissions and decide:
        {url}

        This link expires on {request.ExpiresUtc:yyyy-MM-dd}. If you were not expecting this,
        you can ignore it and nothing will happen.
        """);

    private static string BuildHtml(PendingConsentRequest request, string url)
    {
        // Every interpolated value is HTML-encoded: the requester's display name and address
        // come from a directory we do not control.
        var requester = WebUtility.HtmlEncode(request.RequestedByUpn);
        var href = WebUtility.HtmlEncode(url);
        var expires = WebUtility.HtmlEncode(request.ExpiresUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return $"""
        <p><strong>{requester}</strong> would like to connect your organisation to MLCP, a licence
        and cost management tool for Microsoft 365 and Azure.</p>
        <p>MLCP asks to read two things:</p>
        <ul>
          <li>your organisation's purchased licences and renewal dates</li>
          <li>your users' basic profiles, so it can show which licences are assigned to whom</li>
        </ul>
        <p>It cannot read email, files or messages, cannot change anything, and cannot buy, assign
        or remove licences with these permissions. You can revoke access at any time from the Entra
        admin centre under Enterprise applications.</p>
        <p><a href="{href}">Review the permissions and decide</a></p>
        <p>This link expires on {expires}. If you were not expecting this, you can ignore it and
        nothing will happen.</p>
        """;
    }
}

/// <summary>
/// Writes the consent link to the log instead of sending it. Development only.
/// </summary>
/// <remarks>
/// Registered when no Communication Services endpoint is configured, so a developer can run the
/// whole onboarding flow locally by copying the link out of the console. It logs the URL, which
/// contains a live token, and so must never be registered in a deployed environment — the
/// composition root selects on configuration and logs a warning when this one is chosen.
/// </remarks>
public sealed class LoggingConsentEmailSender : IConsentEmailSender
{
    private readonly ILogger<LoggingConsentEmailSender> _logger;

    public LoggingConsentEmailSender(ILogger<LoggingConsentEmailSender> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task SendConsentRequestAsync(
        PendingConsentRequest request,
        string consentLandingUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogWarning(
            "Email is not configured. Consent link for tenant {TenantId} that would have gone to {Recipient}: {ConsentUrl}",
            request.TenantId,
            request.SentToEmail,
            consentLandingUrl);

        return Task.CompletedTask;
    }
}
