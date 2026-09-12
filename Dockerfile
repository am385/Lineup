# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Git commit hash for version stamping (no .git directory in Docker context)
ARG GIT_HASH=unknown

# Copy project files first for better layer caching
COPY Directory.Build.props ./
COPY src/Lineup.HDHomeRun.Api/Lineup.HDHomeRun.Api.csproj src/Lineup.HDHomeRun.Api/
COPY src/Lineup.HDHomeRun.Device/Lineup.HDHomeRun.Device.csproj src/Lineup.HDHomeRun.Device/
COPY src/Lineup.Core/Lineup.Core.csproj src/Lineup.Core/
COPY src/Lineup.Web/Lineup.Web.csproj src/Lineup.Web/

# Restore dependencies
RUN dotnet restore src/Lineup.Web/Lineup.Web.csproj

# Copy remaining source code
COPY src/Lineup.HDHomeRun.Api/ src/Lineup.HDHomeRun.Api/
COPY src/Lineup.HDHomeRun.Device/ src/Lineup.HDHomeRun.Device/
COPY src/Lineup.Core/ src/Lineup.Core/
COPY src/Lineup.Web/ src/Lineup.Web/

# Publish the application
# Note: Cannot use --no-restore here because the publish step needs to resolve
# Microsoft.AspNetCore.App.Internal.Assets which contains Blazor framework JS files.
# This package is only pulled during publish, not during initial restore.
RUN dotnet publish src/Lineup.Web/Lineup.Web.csproj \
-c Release \
-p:SourceRevisionId=$GIT_HASH \
-o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Install Jellyfin FFmpeg for AC-4 stream decoding
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl gnupg; \
    mkdir -p /etc/apt/keyrings; \
    curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key \
        | gpg --dearmor --yes -o /etc/apt/keyrings/jellyfin.gpg; \
    chmod 0644 /etc/apt/keyrings/jellyfin.gpg; \
    printf '%s\n' \
        'Types: deb' \
        'URIs: https://repo.jellyfin.org/ubuntu' \
        'Suites: noble' \
        'Components: main' \
        "Architectures: $(dpkg --print-architecture)" \
        'Signed-By: /etc/apt/keyrings/jellyfin.gpg' \
        > /etc/apt/sources.list.d/jellyfin.sources; \
    apt-get update; \
    apt-get install -y --no-install-recommends jellyfin-ffmpeg8=8.1.2-4-noble; \
    rm -rf /var/lib/apt/lists/*

ENV PATH="/usr/lib/jellyfin-ffmpeg:${PATH}"

# Fail the build if PATH does not select Jellyfin FFmpeg or AC-4 support is missing.
RUN test "$(readlink -f "$(command -v ffmpeg)")" = "/usr/lib/jellyfin-ffmpeg/ffmpeg" && \
    ffmpeg -hide_banner -decoders 2>/dev/null | grep -Eq '[[:space:]]ac4[[:space:]]'

# Create directories for persistent data
RUN mkdir -p /appdata /xmltv

COPY --from=build /app/publish .

# HTTP on 8080 (default), HTTPS on 8443 (default, when certificate is provided)
# Override with Lineup__HttpPort / Lineup__HttpsPort environment variables
EXPOSE ${HTTP_PORT:-8080}
EXPOSE ${HTTPS_PORT:-8443}

# Configure the application
ENV ASPNETCORE_ENVIRONMENT=Production
# Clear the base image default; Lineup configures explicit Kestrel listeners.
# Do not use ASPNETCORE_HTTP_PORTS; configure Lineup__HttpPort instead.
ENV ASPNETCORE_HTTP_PORTS=""
ENV Lineup__AppDataPath=/appdata
ENV Lineup__XmltvPath=/xmltv
ENV Lineup__HttpPort=8080
ENV Lineup__HttpsPort=8443

ENTRYPOINT ["dotnet", "Lineup.Web.dll"]
