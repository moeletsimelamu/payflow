# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f src/PayFlow.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching: project files change less often than sources.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PayFlow.SharedKernel/*.csproj src/PayFlow.SharedKernel/
COPY src/PayFlow.Infrastructure/*.csproj src/PayFlow.Infrastructure/
COPY src/Modules/PayFlow.Modules.Identity/*.csproj src/Modules/PayFlow.Modules.Identity/
COPY src/Modules/PayFlow.Modules.Audit/*.csproj src/Modules/PayFlow.Modules.Audit/
COPY src/Modules/PayFlow.Modules.Merchants/*.csproj src/Modules/PayFlow.Modules.Merchants/
COPY src/Modules/PayFlow.Modules.Ledger/*.csproj src/Modules/PayFlow.Modules.Ledger/
COPY src/Modules/PayFlow.Modules.Payments/*.csproj src/Modules/PayFlow.Modules.Payments/
COPY src/Modules/PayFlow.Modules.Refunds/*.csproj src/Modules/PayFlow.Modules.Refunds/
COPY src/Modules/PayFlow.Modules.Settlement/*.csproj src/Modules/PayFlow.Modules.Settlement/
COPY src/Modules/PayFlow.Modules.Reconciliation/*.csproj src/Modules/PayFlow.Modules.Reconciliation/
COPY src/Modules/PayFlow.Modules.Webhooks/*.csproj src/Modules/PayFlow.Modules.Webhooks/
COPY src/PayFlow.Webhooks.Signature/*.csproj src/PayFlow.Webhooks.Signature/
COPY src/PayFlow.Migrations/*.csproj src/PayFlow.Migrations/
COPY src/PayFlow.Api/*.csproj src/PayFlow.Api/
RUN dotnet restore src/PayFlow.Api/PayFlow.Api.csproj

COPY src/ src/
RUN dotnet publish src/PayFlow.Api/PayFlow.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled runtime: no shell or package manager, runs as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "PayFlow.Api.dll"]
