namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    private async Task CaptureStartupAndReleaseAsync(
        IResponse response,
        SweepAttempt attempt,
        RunJournal journal,
        StartupRequestEvidence? request,
        System.Collections.Concurrent.ConcurrentDictionary<IRequest, StartupRequestEvidence> pending)
    {
        try
        {
            await CaptureStartupAsync(response, attempt, journal, request);
        }
        catch (Exception ex) when (ex is PlaywrightException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            attempt.EvidenceFailures.Add($"Startup ownership capture failed: {ex.Message}");
            throw;
        }
        finally
        {
            pending.TryRemove(response.Request, out _);
        }
    }

    private async Task CaptureStartupAsync(IResponse response, SweepAttempt attempt, RunJournal journal, StartupRequestEvidence? request)
    {
        var origin = new Uri(response.Url).GetLeftPart(UriPartial.Authority);
        if (_allowedOrigins.Count > 0 && !_allowedOrigins.Contains(origin))
        {
            throw new InvalidDataException("Watch started playback at a listener not declared by the selected output.");
        }
        var now = DateTimeOffset.UtcNow;
        var requestStarted = request?.StartedUtc ?? now;
        var outcome = await CreateStartupOutcomeAsync(response, requestStarted, now);
        JsonNode? body = null;
        if (outcome.Succeeded)
        {
            var text = await response.TextAsync();
            body = JsonNode.Parse(text) ?? throw new InvalidDataException("Successful startup returned no ownership.");
        }
        var id = ReadOutcomeField(body, "sessionId");
        var stopUrl = ReadOutcomeField(body, "stopUrl");
        if (outcome.Succeeded && string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException("Successful startup omitted its session identifier.");
        }
        var owner = id is null ? null : OwnedPlayback.CreateOwner(response.Url, id, stopUrl, _generation);
        var source = response.Request.PostData is string requestBody
            ? SafeStartupRequest(requestBody) : null;
        var exchange = new StartupExchange(Guid.NewGuid().ToString("N"), outcome, id, stopUrl,
            ReadOutcomeField(body, "dashManifestUrl"), ReadOutcomeField(body, "hlsManifestUrl"), source);
        await CheckpointAsync(() =>
        {
            lock (attempt.Ownership)
            {
                if (owner is not null && !attempt.Ownership.Contains(owner))
                {
                    attempt.Ownership.Add(owner);
                }
            }
            attempt.Starts.Add(exchange);
            if (request is not null)
            {
                request.ExchangeId = exchange.Id;
            }
        });
        await journal.WriteEventAsync("startup-exchange", new { attempt.Id, exchange });
    }

    private static string SafeStartupRequest(string body)
    {
        var payload = JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("Startup request was not an object.");
        var safe = new JsonObject();
        foreach (var name in new[] { "quality", "preferredVideo", "preferredAudio", "fallbackAudio", "audioTrack", "subtitleTrack", "overrides" })
        {
            if (payload.TryGetPropertyValue(name, out var value))
            {
                safe[name] = value?.DeepClone();
            }
        }
        return safe.ToJsonString();
    }

    private async Task<PlaybackBaseline> CaptureBaselineAsync(MatrixDefinition matrix)
    {
        var status = await GetStatusAsync(matrix.WatchUrl);
        var streams = ReadStreamIds(status);
        var capacities = ReadAcquisitions(status);
        var inventory = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var origins = _allowedOrigins.Count > 0 ? _allowedOrigins.ToArray() : [new Uri(matrix.WatchUrl).GetLeftPart(UriPartial.Authority)];
        foreach (var origin in origins)
        {
            var url = origin + (origin == new Uri(matrix.WatchUrl).GetLeftPart(UriPartial.Authority) ? "/api/stream/cmaf/sessions" : "/media/sessions");
            inventory[url] = await new OwnedPlayback(_http).InventoryAsync(url, _cancellation);
        }
        return new(streams, capacities, inventory, _generation);
    }

    private async Task<bool> VerifyBaselineAsync(
        MatrixDefinition matrix, PlaybackBaseline baseline, System.Collections.Concurrent.ConcurrentDictionary<IRequest, StartupRequestEvidence> pendingStarts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await GetStatusAsync(matrix.WatchUrl).WaitAsync(cancellationToken);
        if (RuntimeIdentity(status) != baseline.Generation && baseline.Generation.Length > 0)
        {
            throw new InvalidDataException("Application generation changed during cleanup.");
        }
        var streams = ReadStreamIds(status);
        if (!baseline.Streams.IsSubsetOf(streams))
        {
            throw new InvalidDataException("Pre-existing consumers changed during the observation; interference cannot be certified.");
        }
        var inventoriesMatch = true;
        foreach (var (url, before) in baseline.Inventory)
        {
            var after = await new OwnedPlayback(_http).InventoryAsync(url, cancellationToken);
            if (!before.IsSubsetOf(after))
            {
                throw new InvalidDataException("A pre-existing viewer disappeared; cleanup isolation is inconclusive.");
            }
            inventoriesMatch &= before.SetEquals(after);
        }
        var acquisitions = ReadAcquisitions(status);
        var capacityReturned = acquisitions.All(pair => pair.Value <= baseline.Acquisitions.GetValueOrDefault(pair.Key));
        return pendingStarts.IsEmpty && baseline.Streams.SetEquals(streams) && inventoriesMatch && capacityReturned;
    }

    private static HashSet<string> ReadStreamIds(JsonNode status)
    {
        var streams = status["streams"]?["items"]?.AsArray() ?? throw new InvalidDataException("Status stream inventory is unavailable.");
        return streams.Select(item => item!["sessionId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, int> ReadAcquisitions(JsonNode status)
    {
        var capacity = status["streamingGraph"]?["capacity"]?.AsArray() ?? throw new InvalidDataException("Streaming capacity diagnostics are unavailable.");
        return capacity.ToDictionary(item => item!["physicalDeviceId"]!.GetValue<string>(), item => item!["activeAcquisitions"]!.GetValue<int>(), StringComparer.Ordinal);
    }

    private sealed record PlaybackBaseline(HashSet<string> Streams, Dictionary<string, int> Acquisitions, Dictionary<string, HashSet<string>> Inventory, string Generation);
}
