FROM node:24-alpine AS ui
WORKDIR /ui
COPY VERSION /VERSION
COPY frontend/package*.json ./
RUN npm ci --ignore-scripts && npm audit --audit-level=high
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
ARG SOURCE_REVISION=unknown
COPY VERSION ./
COPY src/Grimarr/Grimarr.csproj src/Grimarr/
COPY src/Grimarr/packages.lock.json src/Grimarr/
RUN dotnet restore src/Grimarr/Grimarr.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/Grimarr/Grimarr.csproj -c Release -o /out --no-restore -p:SourceRevisionId=$SOURCE_REVISION
RUN mkdir -p /out/licenses/nuget && find /root/.nuget/packages -type f \( -iname '*license*' -o -iname '*notice*' \) -exec cp --parents '{}' /out/licenses/nuget/ \;

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute
USER root
# Grimarr uses its own entrypoint; the base image's optional supervisor is unused.
RUN apt-get update && apt-get upgrade -y && apt-get install -y --no-install-recommends ffmpeg curl && rm -f /usr/bin/pebble && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ARG GRIMARR_VERSION=development
ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="Grimarr" \
      org.opencontainers.image.version=$GRIMARR_VERSION \
      org.opencontainers.image.revision=$SOURCE_REVISION \
      org.opencontainers.image.source="https://github.com/colonelfrikandel/Grimarr" \
      org.opencontainers.image.licenses="GPL-3.0-only"
COPY --from=build /out ./
COPY --from=ui /ui/dist ./wwwroot/
COPY LICENSE THIRD_PARTY_NOTICES.md ./
ENV ASPNETCORE_URLS=http://+:8787 GRIMARR_CONFIG=/config DOTNET_EnableDiagnostics=0
EXPOSE 8787
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl --fail --silent http://127.0.0.1:8787/health || exit 1
ENTRYPOINT ["dotnet", "Grimarr.dll"]
