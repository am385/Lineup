# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Git commit hash for version stamping (no .git directory in Docker context)
ARG GIT_HASH=unknown
ARG BUILD_VERSION=""

# Copy project files first for better layer caching
COPY Directory.Build.props ./
COPY src/Core/Lineup.Core.Common/Lineup.Core.Common.csproj src/Core/Lineup.Core.Common/
COPY src/Apps/Lineup.App.Web/Lineup.App.Web.csproj src/Apps/Lineup.App.Web/
COPY src/Core/Lineup.Core.Extensibility/Lineup.Core.Extensibility.csproj src/Core/Lineup.Core.Extensibility/
COPY src/Core/Lineup.Core/Lineup.Core.csproj src/Core/Lineup.Core/
COPY src/Core/Lineup.Core.Hosting/Lineup.Core.Hosting.csproj src/Core/Lineup.Core.Hosting/
COPY src/Core/Lineup.Core.Streaming.FFmpeg/Lineup.Core.Streaming.FFmpeg.csproj src/Core/Lineup.Core.Streaming.FFmpeg/
COPY src/Core/Lineup.Core.VirtualOutputs.Protocol/Lineup.Core.VirtualOutputs.Protocol.csproj src/Core/Lineup.Core.VirtualOutputs.Protocol/
COPY src/Core/Lineup.Core.VirtualOutputs.Hosting/Lineup.Core.VirtualOutputs.Hosting.csproj src/Core/Lineup.Core.VirtualOutputs.Hosting/
COPY src/Core/Lineup.Core.VirtualOutputs.Runtime/Lineup.Core.VirtualOutputs.Runtime.csproj src/Core/Lineup.Core.VirtualOutputs.Runtime/
COPY src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Json/Lineup.Plugin.GuideOutputs.Json.csproj src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Json/
COPY src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Json.Plugin/Lineup.Plugin.GuideOutputs.Json.Plugin.csproj src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Json.Plugin/
COPY src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Xmltv/Lineup.Plugin.GuideOutputs.Xmltv.csproj src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Xmltv/
COPY src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Xmltv.Plugin/Lineup.Plugin.GuideOutputs.Xmltv.Plugin.csproj src/Plugins/GuideOutputs/Lineup.Plugin.GuideOutputs.Xmltv.Plugin/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.Api/Lineup.Plugin.SiliconDust.HDHomeRun.Api.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.Api/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.Device/Lineup.Plugin.SiliconDust.HDHomeRun.Device.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.Device/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.GuideSource/Lineup.Plugin.SiliconDust.GuideSource.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.GuideSource/
COPY src/Plugins/LineupLink/Lineup.Plugin.LineupLink/Lineup.Plugin.LineupLink.csproj src/Plugins/LineupLink/Lineup.Plugin.LineupLink/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner/Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime/
COPY src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer/Lineup.Plugin.WebPlayer.csproj src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer/
COPY src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.FFmpeg/Lineup.Plugin.WebPlayer.FFmpeg.csproj src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.FFmpeg/

# Restore dependencies
RUN dotnet restore src/Apps/Lineup.App.Web/Lineup.App.Web.csproj

# Cache browser downloads independently of application source changes.
COPY build/browser-assets.json ./build/browser-assets.json
COPY scripts/Lineup.Tools.BrowserAssets/ ./scripts/Lineup.Tools.BrowserAssets/
RUN dotnet run --project scripts/Lineup.Tools.BrowserAssets --configuration Release --no-launch-profile \
    -p:SourceRevisionId="${GIT_HASH}" -- build/browser-assets.json out/browser-assets

# Copy remaining source code
COPY src/ ./src/
COPY build/ ./build/
COPY scripts/Lineup.Tools.VirtualOutputWorkerPackager/ ./scripts/Lineup.Tools.VirtualOutputWorkerPackager/

# Publish the application
# Note: Cannot use --no-restore here because the publish step needs to resolve
# Microsoft.AspNetCore.App.Internal.Assets which contains Blazor framework JS files.
# This package is only pulled during publish, not during initial restore.
RUN set -eux; \
    set --; \
    if [ -n "${BUILD_VERSION}" ]; then \
        set -- "-p:Version=${BUILD_VERSION}" "-p:IncludeSourceRevisionInInformationalVersion=false"; \
    fi; \
    dotnet publish src/Apps/Lineup.App.Web/Lineup.App.Web.csproj \
        -c Release \
        -p:SourceRevisionId="${GIT_HASH}" \
        "$@" \
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
    apt-get install -y --no-install-recommends fonts-dejavu-core jellyfin-ffmpeg8=8.1.2-4-noble; \
    rm -rf /var/lib/apt/lists/*

ENV PATH="/usr/lib/jellyfin-ffmpeg:${PATH}"

# Fail the build if PATH does not select Jellyfin FFmpeg or required CMAF capabilities are missing.
RUN test "$(readlink -f "$(command -v ffmpeg)")" = "/usr/lib/jellyfin-ffmpeg/ffmpeg" && \
    ffmpeg -hide_banner -decoders 2>/dev/null | grep -Eq '[[:space:]]ac4[[:space:]]' && \
    ffmpeg -hide_banner -encoders 2>/dev/null | grep -Eq '[[:space:]]aac[[:space:]]' && \
    ffmpeg -hide_banner -encoders 2>/dev/null | grep -Eq '[[:space:]]ac3[[:space:]]' && \
    ffmpeg -hide_banner -encoders 2>/dev/null | grep -Eq '[[:space:]]eac3[[:space:]]' && \
    ffmpeg -hide_banner -h muxer=dash 2>&1 | grep -q 'hls_playlist'

# Create directories for persistent data and transient stream artifacts
RUN mkdir -p /appdata /guide /transient

COPY --from=build /app/publish .

# Fixed container ports. Map any desired host ports to these at deployment time.
EXPOSE 8080
EXPOSE 8443
EXPOSE 65001/udp
EXPOSE 1900/udp

# Configure the application
ENV ASPNETCORE_ENVIRONMENT=Production
# Clear the base image default; Lineup configures explicit Kestrel listeners.
# Do not use ASPNETCORE_HTTP_PORTS; configure Lineup__HttpPort instead.
ENV ASPNETCORE_HTTP_PORTS=""
ENV Lineup__AppDataPath=/appdata
ENV Lineup__GuideOutputDir=/guide
ENV Lineup__TransientPath=/transient
ENV Lineup__HttpPort=8080
ENV Lineup__HttpsPort=8443

ENTRYPOINT ["dotnet", "Lineup.App.Web.dll"]
