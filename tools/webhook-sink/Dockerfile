# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f tools/webhook-sink/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PayFlow.Webhooks.Signature/ src/PayFlow.Webhooks.Signature/
COPY tools/webhook-sink/ tools/webhook-sink/
RUN dotnet publish tools/webhook-sink/PayFlow.WebhookSink.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PayFlow.WebhookSink.dll"]
