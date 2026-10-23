# Backend API (.NET 10). Built by deploy/docker-compose.yml (context: repository root, for db/sql).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/src/Api/Api.csproj backend/src/Api/
RUN dotnet restore backend/src/Api/Api.csproj
COPY backend/src/ backend/src/
COPY db/sql/ db/sql/
COPY backend/models/ backend/models/
RUN dotnet publish backend/src/Api/Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Npgsql probes the Kerberos library on connect; without it an error line goes to the log.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
# Data protection keys live on a volume; the non-root "app" user owns it.
RUN mkdir -p /data/keys && chown -R app:app /data/keys
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=5 \
  CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Api.dll"]
