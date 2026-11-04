using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using MmoEngine.Harness.Bots;
using MmoEngine.Harness.Network;
using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Cli;

/// <summary>The network-profile, probe, calibration, stub-target and bot commands (contracts/harness-cli.md).</summary>
internal static class NetworkCommands
{
    public static IEnumerable<Command> All(CliContext context)
    {
        yield return ProfileCommand(context);
        yield return ProbeCommand(context);
        yield return CalibrateCommand(context);
        yield return StubTargetCommand(context);
        yield return BotsCommand(context);
    }

    private static Option<DirectoryInfo> ProfilesOption() => new("--profiles")
    {
        Description = "The profiles folder.",
        DefaultValueFactory = _ => new DirectoryInfo("harness/profiles"),
    };

    private static Option<string> ProfileNameOption(bool required = true) => new("--profile")
    {
        Description = "A profile name, such as Typical or Typical-reorder.",
        Required = required,
    };

    private static Command ProfileCommand(CliContext context)
    {
        var profiles = ProfilesOption();
        var list = new Command("list", "Lists the profile catalogue, with each profile's reordering variant.") { profiles };
        list.SetAction(parse => WithProfiles(context, parse.GetValue(profiles)!, catalog =>
        {
            context.Stdout.WriteLine(new JsonArray([.. catalog.Values.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["version"] = p.Version,
                ["roundTripMs"] = p.RoundTripMs,
                ["jitterMs"] = p.JitterMs,
                ["lossPercent"] = p.LossPercent,
                ["allowReordering"] = p.AllowReordering,
                ["calibrated"] = p.Calibrated is not null,
            })]).ToJsonString());
            return HarnessExitCodes.Ok;
        }));

        var netemProfiles = ProfilesOption();
        var netemName = ProfileNameOption();
        var device = new Option<string>("--device") { Description = "The interface to impair.", DefaultValueFactory = _ => "lo" };
        var seed = new Option<ulong>("--seed") { Description = "netem's loss seed, recorded with the run.", DefaultValueFactory = _ => 1UL };
        var tcp = new Option<bool>("--tcp-ingress") { Description = "Impair the receiver's ingress through ifb0, for TCP runs." };
        var netem = new Command("netem", "Prints a tc netem script that applies a profile on one machine.") { netemProfiles, netemName, device, seed, tcp };
        netem.SetAction(parse => WithProfile(context, parse.GetValue(netemProfiles)!, parse.GetValue(netemName)!, profile =>
        {
            context.Stdout.Write(NetemScript.Script(profile, parse.GetValue(device)!, parse.GetValue(seed), parse.GetValue(tcp)));
            return HarnessExitCodes.Ok;
        }));

        var chaosProfiles = ProfilesOption();
        var chaosName = ProfileNameOption();
        var ns = new Option<string>("--namespace") { Description = "The target pods' namespace.", DefaultValueFactory = _ => "harness" };
        var labels = new Option<string[]>("--label")
        {
            Description = "A pod label selector, key=value; repeatable.",
            DefaultValueFactory = _ => ["app.kubernetes.io/part-of=harness-probe"],
            AllowMultipleArgumentsPerToken = true,
        };
        var chaos = new Command("chaos", "Prints a Chaos Mesh NetworkChaos resource that applies a profile in Kubernetes.") { chaosProfiles, chaosName, ns, labels };
        chaos.SetAction(parse => WithProfile(context, parse.GetValue(chaosProfiles)!, parse.GetValue(chaosName)!, profile =>
        {
            var selector = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var label in parse.GetValue(labels)!)
            {
                var equals = label.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    context.Logger.Error("Invalid --label {Label}: expected key=value", label);
                    return HarnessExitCodes.Invalid;
                }

                selector[label[..equals]] = label[(equals + 1)..];
            }

            context.Stdout.Write(NetworkChaosManifest.Yaml(profile, parse.GetValue(ns)!, selector));
            return HarnessExitCodes.Ok;
        }));

        return new Command("profile", "Network profiles (FR-021 to FR-023).") { list, netem, chaos };
    }

    private static Command ProbeCommand(CliContext context)
    {
        var bind = new Option<string>("--bind") { Description = "The address and port to serve on.", DefaultValueFactory = _ => "127.0.0.1:7400" };
        var serveSeconds = new Option<double?>("--seconds") { Description = "Serve for this long; default: until stopped." };
        var serve = new Command("serve", "Runs the probe server.") { bind, serveSeconds };
        serve.SetAction(async (parse, cancellationToken) =>
        {
            if (!TryEndPoint(context, parse.GetValue(bind)!, out var endPoint, allowAnyPort: true))
            {
                return HarnessExitCodes.Invalid;
            }

            await using var server = new ProbeServer(endPoint);
            context.Logger.Information("Probe server on {EndPoint}", server.LocalEndPoint);
            context.Stdout.WriteLine(new JsonObject { ["listening"] = server.LocalEndPoint.ToString() }.ToJsonString());
            await Wait(parse.GetValue(serveSeconds), cancellationToken).ConfigureAwait(false);
            return HarnessExitCodes.Ok;
        });

        var target = new Option<string>("--target") { Description = "The probe server, host:port.", Required = true };
        var profiles = ProfilesOption();
        var name = ProfileNameOption();
        var seconds = new Option<double>("--seconds") { Description = "Probe duration (FR-024: 300).", DefaultValueFactory = _ => 300 };
        var rate = new Option<double>("--rate") { Description = "Probes per second (FR-024: 30).", DefaultValueFactory = _ => 30 };
        var runId = new Option<string?>("--run-id") { Description = "The run ID; default: probe-<profile>-<time>." };
        var clickHouse = new Option<string?>("--clickhouse") { Description = $"ClickHouse connection string; default: ${HarnessSettings.ClickHouseVariable}." };
        var run = new Command("run", "Probes a server, writes every packet to net_samples, and calibrates against the profile; exit 0 within tolerance, 9 outside.")
        {
            target, profiles, name, seconds, rate, runId, clickHouse,
        };
        run.SetAction(async (parse, cancellationToken) =>
        {
            var settings = context.Settings(parse.GetValue(clickHouse));
            if (settings is null || !TryEndPoint(context, parse.GetValue(target)!, out var endPoint) || !TryProfile(context, parse.GetValue(profiles)!, parse.GetValue(name)!, out var profile))
            {
                return HarnessExitCodes.Invalid;
            }

            var id = parse.GetValue(runId) ?? $"probe-{profile.Name.ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
            var started = DateTime.UtcNow;
            var result = await ProbeClient.RunAsync(endPoint, parse.GetValue(rate), TimeSpan.FromSeconds(parse.GetValue(seconds)), TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            var calibration = Calibration.Evaluate(profile, result.Records);
            return await WithStoreAsync(context, settings, async store =>
            {
                await store.InsertAsync("net_samples", NetSamples.Columns, NetSamples.ToRows(id, result), cancellationToken).ConfigureAwait(false);
                await store.UpsertRunAsync(RunRow(id, "probe", profile.Name, started, (ulong)result.Records.Count), cancellationToken).ConfigureAwait(false);
                return Report(context, id, calibration);
            }).ConfigureAwait(false);
        });

        return new Command("probe", "The UDP probe pair (FR-024).") { serve, run };
    }

    private static Command CalibrateCommand(CliContext context)
    {
        var runId = new Option<string>("--run-id") { Description = "A loaded probe run.", Required = true };
        var profiles = ProfilesOption();
        var name = ProfileNameOption();
        var write = new Option<bool>("--write-calibrated")
        {
            Description = "If the profile passes, store the per-direction settings, runner class and kernel in its file (FR-026).",
        };
        var clickHouse = new Option<string?>("--clickhouse") { Description = $"ClickHouse connection string; default: ${HarnessSettings.ClickHouseVariable}." };
        var command = new Command("calibrate", "Calibrates a stored probe run against a profile; exit 0 within tolerance, 9 outside.")
        {
            runId, profiles, name, write, clickHouse,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var settings = context.Settings(parse.GetValue(clickHouse));
            if (settings is null || !TryProfile(context, parse.GetValue(profiles)!, parse.GetValue(name)!, out var profile))
            {
                return HarnessExitCodes.Invalid;
            }

            var id = parse.GetValue(runId)!;
            return await WithStoreAsync(context, settings, async store =>
            {
                var records = NetSamples.FromRows(await store.QueryRowsAsync(NetSamples.Query, id, cancellationToken).ConfigureAwait(false));
                if (records.Count == 0)
                {
                    context.Logger.Error("Run {RunId} has no net_samples rows", id);
                    return HarnessExitCodes.Invalid;
                }

                var calibration = Calibration.Evaluate(profile, records);
                var file = profile.SourceFile;
                if (calibration.Passed && parse.GetValue(write) && file is null)
                {
                    // A reordering variant has no file of its own: the catalog derives it from its base profile.
                    context.Logger.Warning("Profile {Profile} is a derived reordering variant with no file of its own; its calibrated settings are not stored", profile.Name);
                }
                else if (calibration.Passed && parse.GetValue(write))
                {
                    var calibrated = profile with
                    {
                        Calibrated = new CalibratedSettings
                        {
                            PerDirection = profile.PerDirection(),
                            RunnerClass = RunnerClass.Current().Id,
                            Kernel = Environment.OSVersion.VersionString,
                            MeasuredAt = DateTime.UtcNow,
                            SourceRunId = id,
                        },
                    };
                    await File.WriteAllTextAsync(file!, calibrated.ToJson(), cancellationToken).ConfigureAwait(false);
                }

                return Report(context, id, calibration);
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command StubTargetCommand(CliContext context)
    {
        var bind = new Option<string>("--bind") { Description = "The address and port to serve on.", DefaultValueFactory = _ => "127.0.0.1:7500" };
        var seconds = new Option<double?>("--seconds") { Description = "Serve for this long; default: until stopped." };
        var command = new Command("stub-target", "Runs the stub target: fake 30 Hz snapshots and input echoes (FR-028).") { bind, seconds };
        command.SetAction(async (parse, cancellationToken) =>
        {
            if (!TryEndPoint(context, parse.GetValue(bind)!, out var endPoint, allowAnyPort: true))
            {
                return HarnessExitCodes.Invalid;
            }

            await using var target = new StubTarget(endPoint);
            context.Stdout.WriteLine(new JsonObject { ["listening"] = target.LocalEndPoint.ToString() }.ToJsonString());
            await Wait(parse.GetValue(seconds), cancellationToken).ConfigureAwait(false);
            context.Logger.Information("Stub target: {Inputs} inputs, {Echoed} echoed, {Snapshots} snapshots",
                target.InputsReceived, target.InputsEchoed, target.SnapshotsSent);
            return HarnessExitCodes.Ok;
        });
        return command;
    }

    private static Command BotsCommand(CliContext context)
    {
        var target = new Option<string>("--target") { Description = "The target, host:port.", Required = true };
        var bots = new Option<int>("--bots") { Description = "Bots in this host.", DefaultValueFactory = _ => 4 };
        var seconds = new Option<double>("--seconds") { Description = "Run duration.", DefaultValueFactory = _ => 10 };
        var runId = new Option<string?>("--run-id") { Description = "The run ID; default: bot-<time>." };
        var profile = new Option<string>("--profile") { Description = "The network profile the run is impaired with.", DefaultValueFactory = _ => "none" };
        var clickHouse = new Option<string?>("--clickhouse") { Description = $"ClickHouse connection string; default: ${HarnessSettings.ClickHouseVariable}." };
        var run = new Command("run", "Runs a bot host against a target, writing bot_frames; then run `rules run` on the run.")
        {
            target, bots, seconds, runId, profile, clickHouse,
        };
        run.SetAction(async (parse, cancellationToken) =>
        {
            var settings = context.Settings(parse.GetValue(clickHouse));
            if (settings is null || !TryEndPoint(context, parse.GetValue(target)!, out var endPoint))
            {
                return HarnessExitCodes.Invalid;
            }

            if (parse.GetValue(bots) <= 0)
            {
                context.Logger.Error("--bots must be at least 1");
                return HarnessExitCodes.Invalid;
            }

            var id = parse.GetValue(runId) ?? $"bot-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
            return await WithStoreAsync(context, settings, async store =>
            {
                var started = DateTime.UtcNow;
                var result = await BotHost.RunAsync(endPoint, new BotHostOptions
                {
                    RunId = id,
                    Bots = parse.GetValue(bots),
                    Duration = TimeSpan.FromSeconds(parse.GetValue(seconds)),
                }, new StoreFrameSink(store), cancellationToken).ConfigureAwait(false);
                await store.UpsertRunAsync(RunRow(id, "bot", parse.GetValue(profile)!, started, (ulong)result.Frames), cancellationToken).ConfigureAwait(false);
                context.Stdout.WriteLine(new JsonObject
                {
                    ["runId"] = id,
                    ["hostId"] = result.HostId,
                    ["frames"] = result.Frames,
                    ["deadlineMisses"] = result.DeadlineMisses,
                    ["valid"] = result.Valid,
                }.ToJsonString());
                return HarnessExitCodes.Ok;
            }).ConfigureAwait(false);
        });
        return new Command("bots", "Headless bots (FR-027, FR-029).") { run };
    }

    internal static RunRow RunRow(string id, string kind, string profile, DateTime started, ulong ticks)
    {
        var runnerClass = RunnerClass.Current();
        return new RunRow
        {
            RunId = id,
            Kind = kind,
            Profile = profile,
            RunnerClass = runnerClass.Id,
            Started = started,
            Finished = DateTime.UtcNow,
            Ticks = ticks,
            Loaded = true,
            Manifest = new JsonObject
            {
                ["runId"] = id,
                ["kind"] = kind,
                ["profile"] = profile,
                ["runnerClass"] = runnerClass.Fields.DeepClone(),
                ["kernel"] = Environment.OSVersion.VersionString,
            }.ToJsonString(),
        };
    }

    private static int Report(CliContext context, string runId, CalibrationResult calibration)
    {
        foreach (var failure in calibration.Failures)
        {
            context.Logger.Warning("Calibration outside tolerance: {Failure}", failure);
        }

        context.Stdout.WriteLine(new JsonObject
        {
            ["runId"] = runId,
            ["profile"] = calibration.Profile,
            ["passed"] = calibration.Passed,
            ["sent"] = calibration.Sent,
            ["echoed"] = calibration.Echoed,
            ["medianRoundTripMs"] = Finite(calibration.MedianRoundTripMs),
            ["jitterMs"] = Finite(calibration.JitterMs),
            ["upLossPercent"] = calibration.UpLossPercent,
            ["downLossPercent"] = calibration.DownLossPercent,
            ["upReordered"] = calibration.UpReordered,
            ["downReordered"] = calibration.DownReordered,
            ["upInterarrivalJitterMs"] = calibration.UpInterarrivalJitterMs,
            ["downInterarrivalJitterMs"] = calibration.DownInterarrivalJitterMs,
            ["failures"] = new JsonArray([.. calibration.Failures.Select(f => (JsonNode)f)]),
        }.ToJsonString());
        return calibration.Passed ? HarnessExitCodes.Ok : HarnessExitCodes.OutsideTolerance;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    /// <summary>Waits for the duration, or until stopped (Ctrl-C), without throwing.</summary>
    private static Task Wait(double? seconds, CancellationToken cancellationToken) =>
        Task.Delay(seconds is { } s ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan, cancellationToken)
            .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private static async Task<int> WithStoreAsync(CliContext context, HarnessSettings settings, Func<IHarnessStore, Task<int>> action)
    {
        var store = context.Options.CreateStore(settings.ClickHouseConnectionString);
        try
        {
            return await action(store).ConfigureAwait(false);
        }
        catch (Exception ex) when (Commands.IsStoreFailure(ex))
        {
            context.Logger.Error("Store error: {Error}", ex.Message);
            return HarnessExitCodes.Error;
        }
        finally
        {
            (store as IDisposable)?.Dispose();
        }
    }

    internal static bool TryEndPoint(CliContext context, string text, out IPEndPoint endPoint, bool allowAnyPort = false)
    {
        if (IPEndPoint.TryParse(text, out var parsed) && (parsed.Port > 0 || (allowAnyPort && text.EndsWith(":0", StringComparison.Ordinal))))
        {
            endPoint = parsed;
            return true;
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
        {
            try
            {
                var address = Dns.GetHostAddresses(text[..colon]).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?? Dns.GetHostAddresses(text[..colon]).First();
                endPoint = new IPEndPoint(address, port);
                return true;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or InvalidOperationException)
            {
                // Falls through to the error.
            }
        }

        context.Logger.Error("Invalid endpoint {EndPoint}: expected host:port", text);
        endPoint = new IPEndPoint(IPAddress.None, 0);
        return false;
    }

    private static bool TryProfile(CliContext context, DirectoryInfo folder, string name, out NetworkProfile profile)
    {
        try
        {
            var catalog = ProfileCatalog.Load(folder.FullName);
            if (catalog.TryGetValue(name, out var found))
            {
                profile = found;
                return true;
            }

            context.Logger.Error("Unknown profile {Profile}; known: {Known}", name, string.Join(", ", catalog.Keys.Order(StringComparer.Ordinal)));
        }
        catch (ProfileException ex)
        {
            context.Logger.Error("Invalid profile: {Error}", ex.Message);
        }

        profile = null!;
        return false;
    }

    private static int WithProfiles(CliContext context, DirectoryInfo folder, Func<IReadOnlyDictionary<string, NetworkProfile>, int> action)
    {
        try
        {
            return action(ProfileCatalog.Load(folder.FullName));
        }
        catch (ProfileException ex)
        {
            context.Logger.Error("Invalid profile: {Error}", ex.Message);
            return HarnessExitCodes.Invalid;
        }
    }

    private static int WithProfile(CliContext context, DirectoryInfo folder, string name, Func<NetworkProfile, int> action) =>
        TryProfile(context, folder, name, out var profile) ? action(profile) : HarnessExitCodes.Invalid;
}
