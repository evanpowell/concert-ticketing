# Frontend build stage. Restore before copying source so npm layers cache.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# API build stage.
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
# Angular emits to dist/web/browser; its contents go at the wwwroot root so that
# "/" serves index.html rather than "/browser/index.html".
COPY --from=web /web/dist/web/browser/ ./wwwroot/
# The schema travels with the image so a fresh database can be migrated on boot.
COPY db/migrations/ ./migrations/

# The aspnet image defines APP_UID for a non-root user; run as it.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "Ticketing.Api.dll"]
