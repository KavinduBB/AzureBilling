# Build context is the repository root:
#   docker build -f src/Mlcp.Sync/Dockerfile -t mlcp-sync .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Mlcp.Domain/Mlcp.Domain.csproj src/Mlcp.Domain/
COPY src/Mlcp.Shared/Mlcp.Shared.csproj src/Mlcp.Shared/
COPY src/Mlcp.Application/Mlcp.Application.csproj src/Mlcp.Application/
COPY src/Mlcp.Persistence/Mlcp.Persistence.csproj src/Mlcp.Persistence/
COPY src/Mlcp.Integration.Graph/Mlcp.Integration.Graph.csproj src/Mlcp.Integration.Graph/
COPY src/Mlcp.Integration.Azure/Mlcp.Integration.Azure.csproj src/Mlcp.Integration.Azure/
COPY src/Mlcp.Integration.Partner/Mlcp.Integration.Partner.csproj src/Mlcp.Integration.Partner/
COPY src/Mlcp.Sync/Mlcp.Sync.csproj src/Mlcp.Sync/

RUN dotnet restore src/Mlcp.Sync/Mlcp.Sync.csproj

COPY . .
RUN dotnet publish src/Mlcp.Sync/Mlcp.Sync.csproj -c Release -o /app --no-restore

# The runtime image, not aspnet: the worker serves no HTTP and has no ingress.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS runtime
WORKDIR /app

USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Mlcp.Sync.dll"]
