using System.Globalization;
using Mlcp.Persistence;
using Mlcp.Shared;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.With<Mlcp.Shared.Logging.RedactionEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

builder.Services.AddSerilog(logger);

builder.Services.AddMlcpShared();

var connectionString = builder.Configuration.GetConnectionString("MlcpDatabase")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:MlcpDatabase is required. The sync worker has nothing to do without a database.");

builder.Services.AddMlcpPersistence(connectionString);

// The scheduler needs to see across tenants to decide which are due. The web application
// never registers this.
builder.Services.AddMlcpSystemPersistence(connectionString);

var host = builder.Build();
await host.RunAsync();
