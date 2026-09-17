# Build context is the repository root:
#   docker build -f src/Mlcp.Web/Dockerfile -t mlcp-web .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Copy the files that decide the restore graph first, so a source-only change does not
# invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Mlcp.Domain/Mlcp.Domain.csproj src/Mlcp.Domain/
COPY src/Mlcp.Shared/Mlcp.Shared.csproj src/Mlcp.Shared/
COPY src/Mlcp.Application/Mlcp.Application.csproj src/Mlcp.Application/
COPY src/Mlcp.Persistence/Mlcp.Persistence.csproj src/Mlcp.Persistence/
COPY src/Mlcp.Integration.Graph/Mlcp.Integration.Graph.csproj src/Mlcp.Integration.Graph/
COPY src/Mlcp.Integration.Azure/Mlcp.Integration.Azure.csproj src/Mlcp.Integration.Azure/
COPY src/Mlcp.Integration.Partner/Mlcp.Integration.Partner.csproj src/Mlcp.Integration.Partner/
COPY src/Mlcp.Web/Mlcp.Web.csproj src/Mlcp.Web/

RUN dotnet restore src/Mlcp.Web/Mlcp.Web.csproj

COPY . .

# The .editorconfig is present, so TreatWarningsAsErrors applies here too: an image cannot be
# built from source that would fail CI.
RUN dotnet publish src/Mlcp.Web/Mlcp.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Container Apps routes to 8080 by default and the non-root images cannot bind 80.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The base image ships a non-root user. Running as root would give a process that holds every
# tenant's data a larger blast radius than it needs.
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Mlcp.Web.dll"]
