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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install ffmpeg for stream transcoding
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    rm -rf /var/lib/apt/lists/*

# Create directories for persistent data
RUN mkdir -p /config /xmltv

COPY --from=build /app/publish .

# HTTP on 8080 (default), HTTPS on 8443 (default, when certificate is provided)
# Override with Lineup__HttpPort / Lineup__HttpsPort environment variables
EXPOSE ${HTTP_PORT:-8080}
EXPOSE ${HTTPS_PORT:-8443}

# Configure the application
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Lineup__ConfigPath=/config
ENV Lineup__XmltvPath=/xmltv
ENV Lineup__HttpPort=8080
ENV Lineup__HttpsPort=8443

ENTRYPOINT ["dotnet", "Lineup.Web.dll"]
