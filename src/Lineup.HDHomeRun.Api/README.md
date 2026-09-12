# Lineup.HDHomeRun.Api

A .NET client for downloading the official SiliconDust XMLTV guide.

## API

`HDHomeRunApiClient.FetchXmltvAsync` sends:

```http
GET https://api.hdhomerun.com/api/xmltv?DeviceAuth={device-auth}
Accept: application/xml
Accept-Encoding: gzip
User-Agent: Lineup/2.0 (+https://github.com/am385/Lineup)
```

It returns the validated non-empty response bytes unchanged. The Core project parses the document into its SQLite query index and retains the canonical
bytes for publication. SiliconDust requires a valid `User-Agent` that identifies the requesting application and version; requests without one receive
HTTP 403.

`IDeviceAuthProvider` supplies the current authentication value immediately before each request. For multiple tuners, its implementation must concatenate
the current `DeviceAuth` strings from every device. These values rotate every 16-24 hours and must not be cached between guide downloads.

## Registration

Configure automatic decompression when registering the typed client:

```csharp
services.AddHttpClient<HDHomeRunApiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
services.AddSingleton<IDeviceAuthProvider, YourDeviceAuthProvider>();
```

SiliconDust provides 2 days of guide data to everyone or 14 days with an HDHomeRun DVR guide subscription. Clients must download the complete guide on a
randomized 20-28 hour schedule rather than at midnight or another fixed daily time.

## Requirements

- .NET 10.0
- C# 14.0
- Microsoft.Extensions.Http
- Microsoft.Extensions.Logging.Abstractions

## License

Same as parent project (GPL)
