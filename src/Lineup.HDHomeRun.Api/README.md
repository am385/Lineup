# Lineup.HDHomeRun.Api

A .NET library for interacting with the HDHomeRun remote EPG API.

## Overview

This library provides a clean, strongly-typed API for fetching Electronic Program Guide (EPG) data from HDHomeRun's remote API service (api.hdhomerun.com).

## Features

- **EPG Data Retrieval**: Fetch program guide data for multiple days
- **Single Segment Fetching**: Fetch individual time segments for incremental updates
- **Channel Enrichment**: Combines local device data with remote API metadata
- **Automatic Deduplication**: Handles overlapping time windows gracefully
- **Strongly Typed Models**: All data uses C# records with comprehensive documentation
- **Logging Support**: Built-in support for Microsoft.Extensions.Logging
- **HttpClient Integration**: Works with IHttpClientFactory for proper lifecycle management

## Installation

Add a project reference to your application:

```xml
<ProjectReference Include="..\Lineup.HDHomeRun.Api\Lineup.HDHomeRun.Api.csproj" />
```

## Dependencies

- `Lineup.HDHomeRun.Device` - For device authentication and channel models
- `Microsoft.Extensions.Http` - For HttpClient factory
- `Microsoft.Extensions.Logging.Abstractions` - For logging

## Usage

### Basic Setup

```csharp
using Lineup.HDHomeRun.Api;
using Microsoft.Extensions.DependencyInjection;

// Configure services
services.AddHttpClient<HDHomeRunApiClient>();

// Register auth provider
services.AddTransient<IDeviceAuthProvider, YourAuthProvider>();
services.AddTransient<HDHomeRunApiClient>();
```

### Fetch EPG Data (Multiple Days)

```csharp
var apiClient = serviceProvider.GetRequiredService<HDHomeRunApiClient>();

// Fetch raw EPG data for 7 days, using 3-hour batches
var segments = await apiClient.FetchRawEpgDataAsync(days: 7, hours: 3);

foreach (var segment in segments)
{
    Console.WriteLine($"{segment.GuideNumber}: {segment.GuideName} â€” {segment.Guide.Count} programs");
}
```

### Fetch Single EPG Segment

Use this for incremental fetching to avoid API rate limiting:

```csharp
var apiClient = serviceProvider.GetRequiredService<HDHomeRunApiClient>();

// Fetch a single segment starting now, 4 hours of data
var segments = await apiClient.FetchRawEpgSegmentAsync(
    startTimeUtc: DateTime.UtcNow,
    durationHours: 4);

var totalPrograms = segments.Sum(s => s.Guide.Count);
Console.WriteLine($"Retrieved {totalPrograms} programs");
```

## Models

### HDHomeRunEpgData

Container for aggregated EPG data:
- `Channels` - List of `HDHomeRunEnrichedChannel` (local + API data)
- `Programs` - List of `HDHomeRunProgram` (show listings)

### HDHomeRunEnrichedChannel

Combined channel information from local device and remote API:
- `GuideNumber` - Channel number (e.g., "2.1")
- `GuideName` - Channel display name
- `Affiliate` - Network affiliate (from API)
- `ImageURL` - Channel logo URL (from API)

### HDHomeRunProgram

Program/show information from EPG:
- `Title` - Program title
- `EpisodeTitle` - Episode name
- `Synopsis` - Description
- `StartTime` / `EndTime` - Unix timestamps
- `ImageURL` / `PosterURL` - Artwork URLs
- `EpisodeNumber` - Episode identifier (e.g., "S01E05")
- `OriginalAirdate` - First broadcast date
- `First` - New episode flag
- `SeriesID` - Series identifier
- `Filter` - Categories/genres
- `GuideNumber` - Associated channel

### HDHomeRunChannelEpgSegment

Raw API response for a channel's EPG segment (internal use):
- `GuideNumber` - Channel number
- `GuideName` - Channel name
- `Affiliate` - Network affiliate
- `ImageURL` - Channel icon
- `Guide` - List of programs for this time range

## Interface

### IDeviceAuthProvider

Implement this interface to provide device authentication:

```csharp
public interface IDeviceAuthProvider
{
    Task<string> GetDeviceAuthAsync();
}
```

Typically implemented by `HDHomeRunDeviceClient` from the Lineup.HDHomeRun.Device library.

## API Endpoints

This library interacts with:
- `GET https://api.hdhomerun.com/api/guide?DeviceAuth={token}&Start={timestamp}&Duration={hours}&Channel={channel}`

## How It Works

1. **Authentication**: Gets device auth token from `IDeviceAuthProvider`
2. **Time Windowing**: Fetches EPG data in configurable time windows (default 3 hours)
3. **Deduplication**: Automatically removes duplicate programs from overlapping windows
4. **Enrichment**: Combines local device channel info with remote API metadata
5. **Aggregation**: Returns complete `HDHomeRunEpgData` with all channels and programs

## Error Handling

All service methods throw `InvalidOperationException` with detailed error messages when:
- API cannot be reached
- JSON deserialization fails
- Authentication fails

Exception details are logged using the provided `ILogger` instance.

## Thread Safety

The service is designed to be used as a transient dependency. Create a new instance per operation.

## Performance Considerations

- Uses configurable batch sizes (`hours` parameter) to balance API calls vs memory
- Automatically deduplicates overlapping data
- Logs detailed progress for monitoring
- Use `FetchRawEpgSegmentAsync` for incremental fetching to avoid rate limiting

## Requirements

- .NET 10.0
- C# 14.0
- Lineup.HDHomeRun.Device library
- Microsoft.Extensions.Http
- Microsoft.Extensions.Logging.Abstractions

## License

Same as parent project (GPL)
