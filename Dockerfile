FROM node:24-alpine AS ui
WORKDIR /ui
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Grimarr/Grimarr.csproj src/Grimarr/
RUN dotnet restore src/Grimarr/Grimarr.csproj
COPY src/ src/
RUN dotnet publish src/Grimarr/Grimarr.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY --from=ui /ui/dist ./wwwroot/
ENV ASPNETCORE_URLS=http://+:8787 GRIMARR_CONFIG=/config DOTNET_EnableDiagnostics=0
EXPOSE 8787
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl --fail --silent http://127.0.0.1:8787/health || exit 1
ENTRYPOINT ["dotnet", "Grimarr.dll"]
