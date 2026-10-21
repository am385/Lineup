namespace Lineup.Streaming.Sweep;

/// <summary>Ownership-qualified cleanup that never enumerates sessions to stop other viewers.</summary>
internal sealed class OwnedPlayback(HttpClient http)
{
    /// <summary>Validates a returned stop URL against the selected listener before it can be invoked.</summary>
    public static OwnedSession CreateOwner(string startUrl, string sessionId, string? stopUrl, string generation)
    {
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https")
            || sessionId.Length is < 1 or > 128 || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidDataException("Startup ownership contains an invalid origin or session identifier.");
        }
        var dedicated = start.AbsolutePath.StartsWith("/media/start/", StringComparison.Ordinal);
        var compatibility = start.AbsolutePath.StartsWith("/api/stream/watch-test/start", StringComparison.Ordinal);
        var managementStop = compatibility ? "/api/stream/watch-test/stop/" : "/api/stream/cmaf/stop/";
        var stop = stopUrl is null && !dedicated
            ? new Uri(start, managementStop + Uri.EscapeDataString(sessionId))
            : new Uri(start, stopUrl ?? throw new InvalidDataException("Dedicated startup omitted its stop URL."));
        var expected = dedicated ? $"/media/stop/{sessionId}" : managementStop + sessionId;
        if (stop.GetLeftPart(UriPartial.Authority) != start.GetLeftPart(UriPartial.Authority) || stop.UserInfo.Length > 0
            || stop.AbsolutePath != expected || stop.Query.Length > 0 || stop.Fragment.Length > 0)
        {
            throw new InvalidDataException("Startup returned an unsafe or mismatched stop URL.");
        }
        var origin = start.GetLeftPart(UriPartial.Authority);
        return new(origin, generation, sessionId, stop.AbsoluteUri, origin + (dedicated ? "/media/sessions" : "/api/stream/cmaf/sessions"));
    }

    /// <summary>Reads passive viewer inventory. Dedicated workers must support per-ID inventory.</summary>
    public async Task<HashSet<string>> InventoryAsync(string url, CancellationToken cancellationToken, string? expectedGeneration = null)
    {
        var text = await http.GetStringAsync(url, cancellationToken);
        var node = JsonNode.Parse(text) ?? throw new InvalidDataException("Session inventory is null.");
        if (node is JsonArray array)
        {
            if (expectedGeneration?.Contains('|', StringComparison.Ordinal) == true)
            {
                var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
                var status = JsonNode.Parse(await http.GetStringAsync(origin + "/api/v1/status", cancellationToken))
                    ?? throw new InvalidDataException("Application identity is unavailable during cleanup.");
                var identity = $"{status["application"]?["instanceId"]}|{status["application"]?["startedAtUtc"]}|";
                if (!expectedGeneration.StartsWith(identity, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Management generation changed; refusing cleanup against reused viewer identifiers.");
                }
            }
            return array.Select(item => item?["sessionId"]?.GetValue<string>() ?? throw new InvalidDataException("Session inventory omitted an ID."))
                .ToHashSet(StringComparer.Ordinal);
        }
        var ids = node["sessionIds"]?.AsArray() ?? throw new InvalidDataException("Per-ID worker inventory is unavailable; cleanup cannot be certified.");
        if (!string.IsNullOrEmpty(expectedGeneration) && node["generation"]?.ToString() != expectedGeneration.Split('|')[^1])
        {
            throw new InvalidDataException("Worker generation changed; refusing ownership claims or emergency stop against reused identifiers.");
        }
        return ids.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Normal stop, bounded absence verification, then emergency stop only for still-owned IDs.</summary>
    public async Task<CleanupEvidence> CleanupAsync(
        IReadOnlyList<OwnedSession> owners,
        Func<Task> normalStop,
        TimeSpan timeout,
        Func<CancellationToken, Task<bool>>? verifyBaseline = null)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var errors = new List<string>();
        var forced = false;
        try
        {
            await normalStop().WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is PlaywrightException or HttpRequestException or OperationCanceledException or TimeoutException
            or IOException or JsonException or InvalidOperationException)
        {
            errors.Add($"Normal Watch stop: {ex.Message}");
        }
        try
        {
            var normalDeadline = Stopwatch.StartNew();
            var grace = TimeSpan.FromSeconds(Math.Min(10, timeout.TotalSeconds / 3));
            while (true)
            {
                var remaining = new List<OwnedSession>();
                foreach (var group in Snapshot(owners).GroupBy(owner => owner.InventoryUrl))
                {
                    var inventory = await InventoryAsync(group.Key, deadline.Token, group.First().Generation);
                    remaining.AddRange(group.Where(owner => inventory.Contains(owner.SessionId)));
                }
                if (remaining.Count == 0)
                {
                    if (verifyBaseline is null || await verifyBaseline(deadline.Token))
                    {
                        return new(true, forced, true, Snapshot(owners), errors);
                    }
                }
                if (remaining.Count > 0 && normalDeadline.Elapsed >= grace)
                {
                    forced = true;
                    foreach (var owner in remaining)
                    {
                        using var response = await http.PostAsync(owner.StopUrl, null, deadline.Token);
                        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                        {
                            errors.Add($"Emergency stop {owner.SessionId}: HTTP {(int)response.StatusCode}.");
                        }
                    }
                }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            errors.Add($"Cleanup could not prove absence: {ex.Message}");
            return new(true, forced, false, Snapshot(owners), errors);
        }
    }

    private static OwnedSession[] Snapshot(IReadOnlyList<OwnedSession> owners)
    {
        lock (owners)
        {
            return owners.ToArray();
        }
    }
}
