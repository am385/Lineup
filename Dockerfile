# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for better layer caching
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
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install ffmpeg for stream transcoding
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    rm -rf /var/lib/apt/lists/*

# Create a directory for persistent data (database, settings, xmltv output)
RUN mkdir -p /app/data

COPY --from=build /app/publish .

# Listen on port 8080 (the default for ASP.NET Core in containers)
EXPOSE 8080

# Configure the application
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Lineup__AppDataPath=/app/data

ENTRYPOINT ["dotnet", "Lineup.Web.dll"]
