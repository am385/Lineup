using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Performs channel scanning on an HDHomeRun device.
/// </summary>
public class HDHomeRunChannelScanner
{
    private readonly HDHomeRunControl _control;
    private readonly ILogger<HDHomeRunChannelScanner> _logger;
    private readonly int _tunerIndex;

    /// <summary>
    /// Event raised when a channel is found during scanning
    /// </summary>
    public event EventHandler<ScannedChannel>? ChannelFound;

    /// <summary>
    /// Event raised when scan progress changes
    /// </summary>
    public event EventHandler<ScanProgress>? ProgressChanged;

    /// <summary>
    /// Creates a new channel scanner
    /// </summary>
    public HDHomeRunChannelScanner(HDHomeRunControl control, int tunerIndex, ILogger<HDHomeRunChannelScanner> logger)
    {
        _control = control;
        _tunerIndex = tunerIndex;
        _logger = logger;
    }

    /// <summary>
    /// Starts a channel scan
    /// </summary>
    /// <param name="channelMap">Channel map to scan (e.g., "us-bcast", "us-cable")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of found channels</returns>
    public async Task<List<ScannedChannel>> ScanAsync(
        string channelMap = "us-bcast",
        CancellationToken cancellationToken = default)
    {
        var channels = new List<ScannedChannel>();

        _logger.LogInformation("Starting channel scan with map: {ChannelMap}", channelMap);

        // Set the channel map
        await _control.SetAsync($"/tuner{_tunerIndex}/channelmap", channelMap, cancellationToken);

        // Start scanning
        await _control.SetAsync($"/tuner{_tunerIndex}/channel", "scan", cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Get scan status
                var status = await _control.GetAsync($"/tuner{_tunerIndex}/status", cancellationToken);
                var streamInfo = await _control.GetAsync($"/tuner{_tunerIndex}/streaminfo", cancellationToken);

                if (string.IsNullOrEmpty(status))
                    break;

                // Parse progress
                var progress = ParseScanProgress(status);
                ProgressChanged?.Invoke(this, progress);

                // Parse found channels from streaminfo
                if (!string.IsNullOrEmpty(streamInfo))
                {
                    var foundChannels = ParseStreamInfo(streamInfo);
                    foreach (var channel in foundChannels)
                    {
                        if (!channels.Any(c => c.VirtualChannel == channel.VirtualChannel))
                        {
                            channels.Add(channel);
                            ChannelFound?.Invoke(this, channel);
                            _logger.LogInformation("Found channel: {Channel} - {Name}",
                                channel.VirtualChannel, channel.Name);
                        }
                    }
                }

                // Check if scan is complete
                if (status.Contains("lock=none") || progress.IsComplete)
                    break;

                // Wait before next poll
                await Task.Delay(500, cancellationToken);
            }
        }
        finally
        {
            // Stop scanning
            try
            {
                await _control.SetAsync($"/tuner{_tunerIndex}/channel", "none", CancellationToken.None);
            }
            catch { /* Ignore */ }
        }

        _logger.LogInformation("Channel scan complete. Found {Count} channels", channels.Count);
        return channels;
    }

    private ScanProgress ParseScanProgress(string status)
    {
        // Status format: "ch=8vsb:177000000 lock=8vsb ss=85 snq=100 seq=100 bps=0 pps=0"
        int currentChannel = 0;
        int totalChannels = 0;
        int signalStrength = 0;
        bool hasLock = false;

        var parts = status.Split(' ');
        foreach (var part in parts)
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;

            switch (kv[0])
            {
                case "ch":
                    // Parse channel number from frequency
                    // Format: "8vsb:177000000" or "scan:N/M"
                    if (kv[1].StartsWith("scan:"))
                    {
                        var scanParts = kv[1][5..].Split('/');
                        if (scanParts.Length == 2)
                        {
                            int.TryParse(scanParts[0], out currentChannel);
                            int.TryParse(scanParts[1], out totalChannels);
                        }
                    }
                    break;
                case "lock":
                    hasLock = kv[1] != "none";
                    break;
                case "ss":
                    int.TryParse(kv[1], out signalStrength);
                    break;
            }
        }

        return new ScanProgress
        {
            CurrentChannel = currentChannel,
            TotalChannels = totalChannels,
            SignalStrength = signalStrength,
            HasLock = hasLock,
            IsComplete = currentChannel >= totalChannels && totalChannels > 0
        };
    }

    private List<ScannedChannel> ParseStreamInfo(string streamInfo)
    {
        var channels = new List<ScannedChannel>();

        // StreamInfo format (one per line):
        // "1: 2.1 WCBS-DT"
        // "2: 2.2 WCBS-SD"
        var lines = streamInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Skip header lines
            if (trimmed.StartsWith("tsid=") || trimmed.StartsWith("pcr="))
                continue;

            // Parse "N: vchannel name"
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0) continue;

            var rest = trimmed[(colonIndex + 1)..].Trim();
            var spaceIndex = rest.IndexOf(' ');
            if (spaceIndex <= 0) continue;

            var vchannel = rest[..spaceIndex];
            var name = rest[(spaceIndex + 1)..].Trim();

            if (!string.IsNullOrEmpty(vchannel))
            {
                channels.Add(new ScannedChannel
                {
                    VirtualChannel = vchannel,
                    Name = name
                });
            }
        }

        return channels;
    }
}

/// <summary>
/// Represents a channel found during scanning
/// </summary>
public record ScannedChannel
{
    /// <summary>
    /// Virtual channel number (e.g., "2.1")
    /// </summary>
    public required string VirtualChannel { get; init; }

    /// <summary>
    /// Channel name (e.g., "WCBS-DT")
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Physical frequency
    /// </summary>
    public long? Frequency { get; init; }

    /// <summary>
    /// Modulation type
    /// </summary>
    public string? Modulation { get; init; }
}

/// <summary>
/// Represents channel scan progress
/// </summary>
public record ScanProgress
{
    /// <summary>
    /// Current channel being scanned
    /// </summary>
    public int CurrentChannel { get; init; }

    /// <summary>
    /// Total number of channels to scan
    /// </summary>
    public int TotalChannels { get; init; }

    /// <summary>
    /// Signal strength at current frequency
    /// </summary>
    public int SignalStrength { get; init; }

    /// <summary>
    /// Whether there's signal lock
    /// </summary>
    public bool HasLock { get; init; }

    /// <summary>
    /// Whether the scan is complete
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    public int ProgressPercent => TotalChannels > 0 ? (CurrentChannel * 100 / TotalChannels) : 0;
}
