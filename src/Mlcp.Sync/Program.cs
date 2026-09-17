using System.Globalization;
using Azure.Identity;
using Mlcp.Application;
using Mlcp.Application.Onboarding;
using Mlcp.Integration.Azure;
using Mlcp.Integration.Azure.Messaging;
using Mlcp.Integration.Graph;
using Mlcp.Persistence;
using Mlcp.Persistence.Stores;
using Mlcp.Shared;
using Mlcp.Shared.Logging;
using Mlcp.Shared.Resilience;
using Mlcp.Sync.Jobs;
using Mlcp.Sync.Scheduling;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var keyVaultUri = builder.Configuration["Mlcp:KeyVaultUri"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Services.AddMlcpSerilog(builder.Configuration);

// Fails at startup outside Development when either app registration or the certificate is
// missing, rather than at the first Microsoft call. Worker budget: long retry hints re-queue.
builder.Services.AddMlcpShared(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    MicrosoftCallBudget.Worker);

builder.Services.AddMlcpApplication();

var connectionString = builder.Configuration.GetConnectionString("MlcpDatabase")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:MlcpDatabase is required. The sync worker has nothing to do without a database.");

builder.Services.AddMlcpPersistence(connectionString);

// The scheduler and the deletion sweep have to see across tenants to decide what is due. Nothing
// here writes a tenant out of band any more: the service owning the tenant aggregate does, on its
// own scoped context (ADR-016 rule 2).
builder.Services.AddMlcpSystemPersistence(connectionString);

builder.Services.AddGraphIntegration(builder.Configuration);
builder.Services.AddAzureIntegration(builder.Configuration);

builder.Services.AddScoped<ITenantDeletionStore, TenantDeletionStore>();
builder.Services.AddScoped<TenantDeletionService>();

// ---------------------------------------------------------------------------------------------
// Job queue: one sender implementation shared with the web host. Managed identity in Azure; a
// connection string only for the local emulator; a logging stand-in when neither is configured.
// ---------------------------------------------------------------------------------------------
builder.Services.AddMlcpServiceBusEnqueuer(builder.Configuration);
builder.Services.AddSingleton(new SyncConsumerOptions());
builder.Services.AddScoped<SyncJobProcessor>();
builder.Services.AddSingleton<DiscoverySchedulePass>();
builder.Services.AddSingleton<ReconsentProbeSchedulePass>();

builder.Services.AddHostedService<SyncScheduler>();

var queueConfigured = builder.Configuration[ServiceBusRegistration.NamespaceKey] is { Length: > 0 }
    || builder.Configuration.GetConnectionString(ServiceBusRegistration.ConnectionStringName) is { Length: > 0 };

if (queueConfigured)
{
    // Without a queue there is nothing to consume; a developer machine that only wants the
    // deletion sweep still starts.
    builder.Services.AddHostedService<SyncJobConsumer>();
}

builder.Services.AddHostedService<TenantDeletionJob>();

builder.Services.Configure<HostOptions>(options =>
{
    // A hosted service that fails to start or dies takes the worker down, so the container
    // platform restarts it instead of leaving a process that consumes nothing. The long-running
    // loops (scheduler, deletion sweep) catch their own per-pass failures.
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

var host = builder.Build();
await host.RunAsync();
