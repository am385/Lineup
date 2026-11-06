namespace Lineup.Streaming.Sweep;

/// <summary>Persists terminal run disposition after execution and its bounded teardown, independently of live dependencies.</summary>
internal static class SweepRunLifecycle
{
    /// <summary>Runs owned execution through final reports without allowing a failed teardown to bypass checkpoint persistence.</summary>
    public static async Task<int> RunAsync(SweepRun run, Func<Task> execute, Func<Task> persist, CancellationToken cancellationToken)
    {
        try
        {
            await execute();
            run.State = "complete";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.State = "cancelled";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or HttpRequestException or PlaywrightException
            or JsonException or TimeoutException or OperationCanceledException)
        {
            run.State = "inconclusive";
            run.Violations.Add($"{error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (run.State == "running")
            {
                run.State = "inconclusive";
                run.Violations.Add("Execution exited without a terminal disposition; inspect the propagated exception.");
            }
            run.FinishedUtc = DateTimeOffset.UtcNow;
            await persist();
        }
        return SweepPolicy.ExitCode(run);
    }
}
