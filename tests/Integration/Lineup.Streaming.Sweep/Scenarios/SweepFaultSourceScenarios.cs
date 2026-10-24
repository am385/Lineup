namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    private async Task PrimeFaultSourceCatalogAsync(string sourceUrl)
    {
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{sourceUrl.TrimEnd('/')}/api/stream/cmaf/start/807.1", body);
        if ((int)response.StatusCode != 503)
        {
            throw new InvalidOperationException(
                $"Fault-source catalog priming returned HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

    }

    private async Task RestartWithFaultSourceAsync(
        MatrixDefinition matrix,
        string project,
        string faultContainer,
        string protectedContentMode = "ReturnError")
    {
        var matrixRoot = MatrixRoot(matrix);
        var composePath = Path.Combine(matrixRoot, "compose.yml");
        await RunProcessAsync("docker", $"compose -p {project} -f \"{composePath}\" stop", matrixRoot);
        var settingsPath = Path.Combine(matrixRoot, "runtime", "source", "appdata", "settings.json");
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))
            ?? throw new JsonException("Source settings were empty.");
        settings["Streaming"]!["PhysicalDevices"]![0]!["Adapter"]!["Configuration"]!["Address"] = FaultSourceAddress;
        settings["ProtectedContentMode"] = protectedContentMode;
        await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(_json));
        if (matrix.Remote)
        {
            var consumerSettingsPath = Path.Combine(matrixRoot, "runtime", "consumer", "appdata", "settings.json");
            var consumerSettings = JsonNode.Parse(await File.ReadAllTextAsync(consumerSettingsPath))
                ?? throw new JsonException("Consumer settings were empty.");
            consumerSettings["ProtectedContentMode"] = protectedContentMode;
            await File.WriteAllTextAsync(consumerSettingsPath, consumerSettings.ToJsonString(_json));
        }

        var faultPublishPath = await EnsureFaultSourcePublishedAsync();
        var faultSourceAssembly = "/fault/Lineup.Streaming.Sweep.FaultSource.dll";
        await RunProcessAsync(
            "docker",
            $"run -d --name {faultContainer} --network {project}_default --network-alias fault-source "
            + $"-p 127.0.0.1:18990:8090 -v \"{faultPublishPath}:/fault:ro\" --entrypoint dotnet {_image} {faultSourceAssembly}",
            _root);
        await WaitForFaultSourceAsync(TimeSpan.FromSeconds(30));
        await RunProcessAsync("docker", $"compose -p {project} -f \"{composePath}\" up -d --force-recreate", matrixRoot);
        await WaitReadyAsync(matrix.SourceUrl, TimeSpan.FromMinutes(3));
        if (matrix.Remote)
        {
            await WaitReadyAsync(matrix.WatchUrl, TimeSpan.FromMinutes(3));
        }

        if (matrix.Mode != HostingMode.Management)
        {
            await WaitWorkerHealthyAsync(matrix.SourceUrl, GuidFor($"{matrix.Id}:source-virtual"), TimeSpan.FromMinutes(3));
        }
    }

    private async Task<string> EnsureFaultSourcePublishedAsync()
    {
        var publishPath = Path.Combine(_root, "fault-source", "publish");
        var assemblyPath = Path.Combine(publishPath, "Lineup.Streaming.Sweep.FaultSource.dll");
        if (File.Exists(assemblyPath))
        {
            return publishPath;
        }

        Directory.CreateDirectory(publishPath);
        await RunProcessAsync(
            "dotnet",
            $"publish \"{_faultSourceProjectPath}\" -c Release --nologo -o \"{publishPath}\"",
            _repositoryRoot);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("The fault-source publish did not produce Lineup.Streaming.Sweep.FaultSource.dll.", assemblyPath);
        }

        return publishPath;
    }

    private async Task<int> ReadFaultSourceRequestCountAsync(string channel)
    {
        var metrics = await _http.GetFromJsonAsync<JsonNode>("http://127.0.0.1:18990/metrics")
            ?? throw new InvalidOperationException("Fault-source metrics were empty.");
        return metrics["streamRequests"]?[channel]?.GetValue<int>() ?? 0;
    }

    private async Task WaitForFaultSourceAsync(TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            try
            {
                using var response = await _http.GetAsync("http://127.0.0.1:18990/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("The synthetic HDHomeRun source did not become ready.");
    }

    private async Task<DedicatedFailureResult> VerifySourceAcquisitionFailureAsync(
        IBrowserContext context,
        MatrixDefinition matrix,
        string channel,
        int expectedStatus,
        string expectedCode,
        string expectedError)
    {
        var page = await context.NewPageAsync();
        var requestFailures = new List<string>();
        var startUrl = matrix.Remote || matrix.Mode == HostingMode.Management
            ? $"{matrix.WatchUrl.TrimEnd('/')}/api/stream/cmaf/start/{Uri.EscapeDataString(channel)}"
            : $"http://127.0.0.1:{matrix.OutputPort}/media/start/{Uri.EscapeDataString(channel)}";
        string? observedCorsOrigin = null;
        page.RequestFailed += (_, request) => requestFailures.Add($"{request.Url}: {request.Failure}");
        page.Response += (_, response) =>
        {
            if (string.Equals(response.Url, startUrl, StringComparison.OrdinalIgnoreCase)
                && response.Headers.TryGetValue("access-control-allow-origin", out var corsOrigin))
            {
                observedCorsOrigin = corsOrigin;
            }
        };
        try
        {
            await page.GotoAsync($"{matrix.WatchUrl.TrimEnd('/')}/watch", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            var browserResult = await page.EvaluateAsync<JsonElement>(
                """
                async ({ startUrl }) => {
                  try {
                    const response = await fetch(startUrl, {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: '{}'
                    });
                    const text = await response.text();
                    let body = null;
                    try { body = JSON.parse(text); } catch {}
                    return {
                      fetchSucceeded: true,
                      status: response.status,
                      corsOrigin: null,
                      body,
                      error: null
                    };
                  } catch (error) {
                    return {
                      fetchSucceeded: false,
                      status: null,
                      corsOrigin: null,
                      body: null,
                      error: String(error)
                    };
                  }
                }
                """,
                new { startUrl });
            var result = browserResult.Deserialize<DedicatedFailureResult>(_json)
                ?? throw new InvalidOperationException($"{matrix.Id} returned no source-acquisition failure result.");
            result = result with { Matrix = matrix.Id, CorsOrigin = observedCorsOrigin, RequestFailures = requestFailures.ToArray() };
            var corsValid = matrix.Remote || matrix.Mode == HostingMode.Management || result.CorsOrigin == "*";
            if (!result.FetchSucceeded
                || result.Status != expectedStatus
                || result.Body?["code"]?.GetValue<string>() != expectedCode
                || result.Body?["error"]?.GetValue<string>() != expectedError
                || !corsValid
                || result.RequestFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{matrix.Id} did not preserve the structured {expectedCode} response for {channel}: {JsonSerializer.Serialize(result, _json)}");
            }
            return result;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task RemoveContainerAsync(string name)
    {
        await RunProcessAsync("docker", $"rm -f {name}", _root, false);
    }

    private async Task<DedicatedFailureResult> VerifyDedicatedFailureAsync(IBrowserContext context, MatrixDefinition matrix, string channel)
    {
        var page = await context.NewPageAsync();
        var requestFailures = new List<string>();
        var startUrl = $"http://127.0.0.1:{matrix.OutputPort}/media/start/{Uri.EscapeDataString(channel)}";
        string? observedCorsOrigin = null;
        page.RequestFailed += (_, request) => requestFailures.Add($"{request.Url}: {request.Failure}");
        page.Response += (_, response) =>
        {
            if (string.Equals(response.Url, startUrl, StringComparison.OrdinalIgnoreCase)
                && response.Headers.TryGetValue("access-control-allow-origin", out var corsOrigin))
            {
                observedCorsOrigin = corsOrigin;
            }
        };
        try
        {
            await page.GotoAsync($"{matrix.WatchUrl.TrimEnd('/')}/watch", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            var browserResult = await page.EvaluateAsync<JsonElement>(
                """
                async ({ startUrl }) => {
                  try {
                    const response = await fetch(startUrl, {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: '{}'
                    });
                    const text = await response.text();
                    let body = null;
                    try { body = JSON.parse(text); } catch {}
                    return {
                      fetchSucceeded: true,
                      status: response.status,
                      corsOrigin: response.headers.get('access-control-allow-origin'),
                      body,
                      error: null
                    };
                  } catch (error) {
                    return {
                      fetchSucceeded: false,
                      status: null,
                      corsOrigin: null,
                      body: null,
                      error: String(error)
                    };
                  }
                }
                """,
                new { startUrl });
            var result = browserResult.Deserialize<DedicatedFailureResult>(_json)
                ?? throw new InvalidOperationException($"{matrix.Id} returned no dedicated startup failure result.");
            result = result with { Matrix = matrix.Id, CorsOrigin = observedCorsOrigin, RequestFailures = requestFailures.ToArray() };
            if (!result.FetchSucceeded
                || result.Status != 404
                || result.CorsOrigin != "*"
                || result.Body?["code"]?.GetValue<string>() != "channel_not_found"
                || string.IsNullOrWhiteSpace(result.Body?["error"]?.GetValue<string>())
                || result.Body?["stage"]?.GetValue<string>() != "broker"
                || result.RequestFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{matrix.Id} did not return a browser-readable structured channel_not_found response: {JsonSerializer.Serialize(result, _json)}");
            }
            return result;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

}
