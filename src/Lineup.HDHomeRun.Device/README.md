# Lineup.HDHomeRun.Device

A .NET library for interacting with HDHomeRun network tuner devices.

## Overview

This library provides a clean, strongly-typed API for communicating with HDHomeRun devices on your local network. It handles device discovery, authentication, and channel lineup retrieval.

## Features

- **Device Discovery**: Retrieve complete device information including model, firmware, and tuner count
- **Authentication**: Automatic device authentication token retrieval
- **Channel Lineup**: Fetch available channels with technical details (codec, signal strength, etc.)
- **Strongly Typed**: All models use C# records with comprehensive documentation
- **Logging**: Built-in support for Microsoft.Extensions.Logging
- **HttpClient Integration**: Works with IHttpClientFactory for proper lifecycle management

## Installation

Add a project reference to your application:

```xml
<ProjectReference Include="..\Lineup.HDHomeRun.Device\Lineup.HDHomeRun.Device.csproj" />
```

## Usage

### Basic Setup

```csharp
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Extensions.DependencyInjection;

// Configure services
services.AddHttpClient<HDHomeRunDeviceClient>(client =>
{
    client.BaseAddress = new Uri("http://hdhomerun.local");
});
services.AddTransient<HDHomeRunDeviceClient>();
```

### Discover Device

```csharp
var deviceService = serviceProvider.GetRequiredService<HDHomeRunDeviceClient>();

// Get complete device information
var deviceInfo = await deviceService.DiscoverDeviceAsync();
Console.WriteLine($"Found: {deviceInfo.FriendlyName} ({deviceInfo.ModelNumber})");
Console.WriteLine($"Firmware: {deviceInfo.FirmwareVersion}");
Console.WriteLine($"Tuners: {deviceInfo.TunerCount}");
Console.WriteLine($"Device ID: {deviceInfo.DeviceID}");

// Or just get the auth token
var authToken = await deviceService.DiscoverDeviceAuthAsync();
```

### Fetch Channel Lineup

```csharp
var channels = await deviceService.FetchChannelLineupAsync();

foreach (var channel in channels)
{
    Console.WriteLine($"{channel.GuideNumber}: {channel.GuideName}");
    Console.WriteLine($"  Codec: {channel.VideoCodec}/{channel.AudioCodec}");
    Console.WriteLine($"  Signal: {channel.SignalStrength}% strength, {channel.SignalQuality}% quality");
    Console.WriteLine($"  HD: {channel.HD}, DRM: {channel.DRM}");
}
```

## Models

### HDHomeRunDeviceInfo

Contains complete device information from `discover.json`:

- `FriendlyName` - User-friendly device name
- `ModelNumber` - Device model identifier
- `FirmwareName` - Firmware variant
- `FirmwareVersion` - Firmware version
- `DeviceID` - Unique device identifier
- `DeviceAuth` - Authentication token
- `BaseURL` - Device base URL
- `LineupURL` - Channel lineup endpoint
- `TunerCount` - Number of available tuners

### HDHomeRunChannel

Represents a channel from `lineup.json`:

- `GuideNumber` - Channel number (e.g., "2.1")
- `GuideName` - Channel display name
- `VideoCodec` - Video encoding (MPEG2, H264, HEVC)
- `AudioCodec` - Audio encoding (AC3, AC4)
- `HD` - HD channel flag
- `DRM` - DRM protection flag
- `Favorite` - Favorite channel flag
- `SignalStrength` - Reception strength (0-100)
- `SignalQuality` - Reception quality (0-100)
- `URL` - Streaming URL

## Requirements

- .NET 10.0
- C# 14.0
- Microsoft.Extensions.Http
- Microsoft.Extensions.Logging.Abstractions

## API Endpoints

This library interacts with two HDHomeRun device endpoints:

- `GET /discover.json` - Device discovery and authentication
- `GET /lineup.json` - Channel lineup

## Error Handling

All service methods throw `InvalidOperationException` with detailed error messages when:
- Device cannot be reached
- JSON deserialization fails
- No data is returned

Exception details are logged using the provided `ILogger` instance.

## Thread Safety

The service is designed to be used as a transient dependency. Create a new instance per operation or request.

## License

Same as parent project (GPL)
