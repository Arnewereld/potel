# Bouwt het hele portaal (frontend + API) tot één image.
FROM node:20-bookworm-slim AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY global.json ./
COPY backend/Potel.Api/Potel.Api.csproj backend/Potel.Api/
RUN dotnet restore backend/Potel.Api/Potel.Api.csproj
COPY backend/ backend/
COPY --from=web /src/backend/Potel.Api/wwwroot backend/Potel.Api/wwwroot
RUN dotnet publish backend/Potel.Api/Potel.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
# De database en de sleutels voor inlogcookies staan in /data; koppel daar een volume aan.
ENV ASPNETCORE_URLS=http://+:8080 \
    DatabasePath=/data/potel.db \
    KeysPath=/data/keys
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Potel.Api.dll"]
