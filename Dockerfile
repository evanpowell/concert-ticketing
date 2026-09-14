# Build stage. Restore before copying source so dependency layers cache.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY api/Ticketing.Api/Ticketing.Api.csproj ./Ticketing.Api/
RUN dotnet restore ./Ticketing.Api/Ticketing.Api.csproj

COPY api/Ticketing.Api/ ./Ticketing.Api/
RUN dotnet publish ./Ticketing.Api/Ticketing.Api.csproj -c Release -o /app --no-restore

# Runtime stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app ./
# The schema travels with the image so a fresh database can be migrated on boot.
COPY db/migrations/ ./migrations/

# The aspnet image defines APP_UID for a non-root user; run as it.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "Ticketing.Api.dll"]
