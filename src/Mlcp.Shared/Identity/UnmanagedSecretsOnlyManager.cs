using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace Mlcp.Shared.Identity;

/// <summary>
/// Loads ordinary Key Vault secrets into configuration and skips the ones Key Vault manages.
/// </summary>
/// <remarks>
/// Every Key Vault certificate has a managed secret holding its PFX, private key included.
/// Loading it into <c>IConfiguration</c> would put the app's signing key into a dictionary that
/// configuration dumps, diagnostics and careless logging can all reach. The certificate is read
/// only by <see cref="TenantTokenProvider"/> and Microsoft.Identity.Web, through the certificate
/// API (infra/deploy.md §4).
/// </remarks>
public sealed class UnmanagedSecretsOnlyManager : KeyVaultSecretManager
{
    public override bool Load(SecretProperties secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return !secret.Managed;
    }
}
