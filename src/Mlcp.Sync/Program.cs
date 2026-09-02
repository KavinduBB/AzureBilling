using System.Globalization;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Mlcp.Application;
using Mlcp.Application.Onboarding;
using Mlcp.Integration.Azure;
using Mlcp.Integration.Graph;
using Mlcp.Persistence;
using Mlcp.Persistence.Stores;
using Mlcp.Shared;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Logging;
using Mlcp.Sync.Jobs;
using Mlcp.Sync.Scheduling;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var keyVaultUri = builder.Configuration["Mlcp:KeyVaultUri"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.With<RedactionEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

builder.Services.AddSerilog(logger);

var identityOptions = new MlcpIdentityOptions
{
    ClientId = builder.Configuration["AzureAd:ClientId"] ?? string.Empty,
    CertificateName = builder.Configuration["Mlcp:ClientCertificateName"],
    KeyVaultUri = string.IsNullOrWhiteSpace(keyVaultUri) ? null : new Uri(keyVaultUri),
    ClientSecret = builder.Configuration["AzureAd:ClientSecret"],
};

builder.Services.AddMlcpShared(
    string.IsNullOrWhiteSpace(identityOptions.ClientId) ? null : identityOptions);

builder.Services.AddMlcpApplication();

var connectionString = builder.Configuration.GetConnectionString("MlcpDatabase")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:MlcpDatabase is required. The sync worker has nothing to do without a database.");

builder.Services.AddMlcpPersistence(connectionString);

// The scheduler and the deletion sweep both have to see across tenants to decide what is due.
// Registered after AddMlcpPersistence so the system-context re-consent signal replaces the
// request-scoped one: in the worker there is no request, and the tenant whose credential failed
// is not necessarily the one the current scope is bound to.
builder.Services.AddMlcpSystemPersistence(connectionString);

builder.Services.AddGraphIntegration();
builder.Services.AddAzureIntegration();

builder.Services.AddScoped<ITenantDeletionStore, TenantDeletionStore>();
builder.Services.AddScoped<TenantDeletionService>();

// ---------------------------------------------------------------------------------------------
// Job queue. Managed identity in Azure; a connection string only for the local emulator, which
// has no identity support.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton(new SyncQueueOptions
{
    QueueName = builder.Configuration["Sync:JobQueueName"] ?? "mlcp-sync-jobs",
});

var serviceBusNamespace = builder.Configuration["Mlcp:ServiceBus:FullyQualifiedNamespace"];
var serviceBusConnectionString = builder.Configuration.GetConnectionString("ServiceBus");

var hasServiceBus = !string.IsNullOrWhiteSpace(serviceBusNamespace)
    || !string.IsNullOrWhiteSpace(serviceBusConnectionString);

if (!string.IsNullOrWhiteSpace(serviceBusNamespace))
{
    builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential()));
    builder.Services.AddSingleton<ISyncJobDispatcher, ServiceBusSyncJobDispatcher>();
}
else if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
{
    // Connection string only for the local emulator, which has no managed identity support.
    builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusConnectionString));
    builder.Services.AddSingleton<ISyncJobDispatcher, ServiceBusSyncJobDispatcher>();
}
else
{
    builder.Services.AddSingleton<ISyncJobDispatcher, LoggingSyncJobDispatcher>();
}

builder.Services.AddHostedService<CapabilityDiscoveryScheduler>();

if (hasServiceBus)
{
    // Without a queue there is nothing to consume, and starting a processor against a client
    // that does not exist would fail the worker on a developer machine that only wants the
    // deletion sweep.
    builder.Services.AddHostedService<SyncJobConsumer>();
}
builder.Services.AddHostedService<TenantDeletionJob>();

builder.Services.Configure<HostOptions>(options =>
{
    // One failing hosted service must not take the worker down: the deletion sweep stopping
    // would silently stop honouring deletion promises.
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

var host = builder.Build();
await host.RunAsync();
