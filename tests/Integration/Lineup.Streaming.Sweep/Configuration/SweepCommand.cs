namespace Lineup.Streaming.Sweep;

/// <summary>Strict command-line parsing shared by every execution alias.</summary>
internal sealed record SweepCommand(string[] Arguments, SweepOptions Options, string? OutputRoot, string? InputRun)
{
    /// <summary>Parses named options without silently accepting misspellings or conflicting modes.</summary>
    public static SweepCommand Parse(string[] arguments)
    {
        var positional = new List<string>();
        var options = new SweepOptions();
        string? output = null;
        string? input = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = arguments[index];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(name);
                continue;
            }
            string Value()
            {
                if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) || arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for {name}.");
                }
                return arguments[index];
            }
            double Seconds() => double.Parse(Value(), CultureInfo.InvariantCulture);
            int Count() => int.Parse(Value(), CultureInfo.InvariantCulture);
            options = name switch
            {
                "--existing-instance" => options with { ExistingInstance = true },
                "--base-url" => options with { ExistingInstance = true, BaseUrl = Value().TrimEnd('/') },
                "--include" => options with { Include = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
                "--exclude" => options with { Exclude = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
                "--matrices" => options with { Matrices = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
                "--max-channels" => options with { MaximumChannels = Count() },
                "--startup-seconds" => options with { StartupSeconds = Seconds() },
                "--stable-seconds" => options with { StableSeconds = Seconds() },
                "--media-advance-seconds" => options with { MediaAdvanceSeconds = Seconds() },
                "--observation-seconds" => options with { ObservationSeconds = Seconds() },
                "--cleanup-seconds" => options with { CleanupSeconds = Seconds() },
                "--attempts" => options with { MaximumAttempts = Count() },
                "--retry-delay-seconds" => options with { RetryDelaySeconds = Seconds() },
                "--max-sequence-delta" => options with { MaximumSequenceDelta = Count() },
                "--max-product-failures" => options with { MaximumProductFailures = Count() },
                "--browser" => options with { BrowserExecutable = Value() },
                "--profile" => options with { BrowserProfile = Path.GetFullPath(Value()) },
                "--setup-compatibility" => options with { SetupCompatibility = true },
                "--allow-matrix-recovery" => options with { AllowMatrixRecovery = true },
                "--sync-only" => options with { SyncOnly = true },
                "--output" or "--input-run" => options,
                _ => throw new ArgumentException($"Unknown option '{name}'.")
            };
            if (name == "--output")
            {
                output = Value();
            }
            if (name == "--input-run")
            {
                input = Path.GetFullPath(Value());
            }
        }
        var command = positional.FirstOrDefault()?.ToLowerInvariant();
        if (command is not null)
        {
            positional[0] = command;
        }
        if (input is not null && command is not ("report-only" or "resume" or "recheck-post-fix-failures"))
        {
            throw new ArgumentException("--input-run is only valid for resume, failure retry or report-only.");
        }
        var operands = positional.Count - 1;
        if ((command is "execute-channel" or "execute-local-channel" && operands != 1)
            || (command is "execute-channels" or "execute-remote-channels" && operands < 1)
            || (command is "verify-issue6" or "verify-issue7" or "verify-issue8" or "verify-issue9" or "verify-issue10" && operands > 1))
        {
            throw new ArgumentException("Unexpected or missing channel/matrix operands.");
        }
        if (operands > 0 && command is not ("execute-channel" or "execute-local-channel" or "execute-channels" or "execute-remote-channels"
            or "verify-issue6" or "verify-issue7" or "verify-issue8" or "verify-issue9" or "verify-issue10"))
        {
            throw new ArgumentException("This command accepts named options only.");
        }
        if (options.Include.Length > 0 && command is "execute-channel" or "execute-local-channel" or "execute-channels" or "execute-remote-channels")
        {
            throw new ArgumentException("Use positional channels or --include, not both.");
        }
        if (command == "existing")
        {
            options = options with { ExistingInstance = true };
        }
        if (command == "sync-only")
        {
            options = options with { ExistingInstance = true, SyncOnly = true };
        }
        if (options.ExistingInstance && command is not ("existing" or "sync-only" or "execute" or "execute-fresh" or "execute-channel" or "execute-channels"))
        {
            throw new ArgumentException("Existing-instance mode cannot invoke Docker/regression deployment commands.");
        }
        options.Validate();
        return new(positional.ToArray(), options, output, input);
    }
}
