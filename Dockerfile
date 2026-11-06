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
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost/
COPY src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost.csproj src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost/
COPY src/Plugins/LineupLink/Lineup.Plugin.LineupLink.VirtualOutputHost/Lineup.Plugin.LineupLink.VirtualOutputHost.csproj src/Plugins/LineupLink/Lineup.Plugin.LineupLink.VirtualOutputHost/
COPY src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer/Lineup.Plugin.WebPlayer.csproj src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer/
COPY src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.FFmpeg/Lineup.Plugin.WebPlayer.FFmpeg.csproj src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.FFmpeg/
COPY src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.VirtualOutputHost/Lineup.Plugin.WebPlayer.VirtualOutputHost.csproj src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.VirtualOutputHost/

# Restore dependencies
RUN dotnet restore src/Apps/Lineup.App.Web/Lineup.App.Web.csproj

# Copy remaining source code
COPY src/ ./src/

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
        -o /app/publish; \
    dotnet publish src/Plugins/LineupLink/Lineup.Plugin.LineupLink.VirtualOutputHost/Lineup.Plugin.LineupLink.VirtualOutputHost.csproj \
        -c Release -p:SourceRevisionId="${GIT_HASH}" "$@" -o /app/workers/lineup-link; \
    dotnet publish src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost.csproj \
        -c Release -p:SourceRevisionId="${GIT_HASH}" "$@" -o /app/workers/hdhomerun; \
    dotnet publish src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost.csproj \
        -c Release -p:SourceRevisionId="${GIT_HASH}" "$@" -o /app/workers/hdhomerun-discovery; \
    dotnet publish src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer.VirtualOutputHost/Lineup.Plugin.WebPlayer.VirtualOutputHost.csproj \
        -c Release -p:SourceRevisionId="${GIT_HASH}" "$@" -o /app/workers/web-player; \
    lineup_link_hash="$(sha256sum /app/workers/lineup-link/Lineup.Plugin.LineupLink.VirtualOutputHost.dll | cut -d ' ' -f 1)"; \
    hdhomerun_hash="$(sha256sum /app/workers/hdhomerun/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost.dll | cut -d ' ' -f 1)"; \
    hdhomerun_discovery_hash="$(sha256sum /app/workers/hdhomerun-discovery/Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost.dll | cut -d ' ' -f 1)"; \
    web_player_hash="$(sha256sum /app/workers/web-player/Lineup.Plugin.WebPlayer.VirtualOutputHost.dll | cut -d ' ' -f 1)"; \
    printf '%s' "{\"schemaVersion\":1,\"workerId\":\"lineup-link\",\"version\":\"${BUILD_VERSION:-3.0.0-beta}\",\"publisher\":\"Lineup\",\"executablePath\":\"Lineup.Plugin.LineupLink.VirtualOutputHost.dll\",\"executableSha256\":\"${lineup_link_hash}\",\"adapterKinds\":[\"lineup-link\"],\"platforms\":[],\"minimumProtocolVersion\":1,\"maximumProtocolVersion\":1,\"minimumLineupVersion\":\"${BUILD_VERSION:-3.0.0-beta}\",\"settingsDescriptor\":{\"adapterKind\":\"lineup-link\",\"fields\":[]}}" \
        > /app/workers/lineup-link/lineup-link.virtual-output.json; \
    printf '%s' "{\"schemaVersion\":1,\"workerId\":\"hdhomerun\",\"version\":\"${BUILD_VERSION:-3.0.0-beta}\",\"publisher\":\"Lineup\",\"executablePath\":\"Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost.dll\",\"executableSha256\":\"${hdhomerun_hash}\",\"adapterKinds\":[\"hdhomerun\"],\"platforms\":[],\"minimumProtocolVersion\":1,\"maximumProtocolVersion\":1,\"minimumLineupVersion\":\"${BUILD_VERSION:-3.0.0-beta}\",\"settingsDescriptor\":{\"adapterKind\":\"hdhomerun\",\"fields\":[]}}" \
        > /app/workers/hdhomerun/hdhomerun.virtual-output.json; \
    printf '%s' "{\"schemaVersion\":1,\"workerId\":\"hdhomerun-discovery\",\"version\":\"${BUILD_VERSION:-3.0.0-beta}\",\"publisher\":\"Lineup\",\"executablePath\":\"Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost.dll\",\"executableSha256\":\"${hdhomerun_discovery_hash}\",\"adapterKinds\":[\"hdhomerun-discovery\"],\"platforms\":[],\"minimumProtocolVersion\":1,\"maximumProtocolVersion\":1,\"minimumLineupVersion\":\"${BUILD_VERSION:-3.0.0-beta}\",\"settingsDescriptor\":{\"adapterKind\":\"hdhomerun-discovery\",\"fields\":[]}}" \
        > /app/workers/hdhomerun-discovery/hdhomerun-discovery.virtual-output.json; \
    printf '%s' "{\"schemaVersion\":1,\"workerId\":\"web-player\",\"version\":\"${BUILD_VERSION:-3.0.0-beta}\",\"publisher\":\"Lineup\",\"executablePath\":\"Lineup.Plugin.WebPlayer.VirtualOutputHost.dll\",\"executableSha256\":\"${web_player_hash}\",\"adapterKinds\":[\"web-player\"],\"platforms\":[],\"minimumProtocolVersion\":1,\"maximumProtocolVersion\":1,\"minimumLineupVersion\":\"${BUILD_VERSION:-3.0.0-beta}\",\"settingsDescriptor\":{\"adapterKind\":\"web-player\",\"fields\":[]}}" \
        > /app/workers/web-player/web-player.virtual-output.json

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
COPY --from=build /app/workers ./virtual-output-workers

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
