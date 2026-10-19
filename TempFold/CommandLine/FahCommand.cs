// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.CommandLine;
using System.Net.WebSockets;
using System.Text.Json;
using TempFold.Configuration;
using TempFold.Integrations;

namespace TempFold.CommandLine;

internal static class FahCommand
{
    internal static Task<int> InvokeAsync(string[] args, string? defaultConfigPath = null,
        Func<string, Uri, CancellationToken, Task>? run = null, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        run ??= (action, endpoint, token) => RunAsync(action, endpoint, output, token);
        var root = new RootCommand("Control Folding@home directly, then exit.");
        var configOption = new Option<string?>("--config")
        {
            Recursive = true,
            Description = "Configuration path; otherwise search beside the executable, then ~/.config/tempfold/config.json."
        };
        var endpointOption = new Option<string?>("--fah-endpoint")
        {
            Recursive = true,
            Description = "FAH WebSocket endpoint (ws:// or wss://); overrides the configuration file."
        };
        root.Options.Add(configOption);
        root.Options.Add(endpointOption);
        foreach (string action in new[] { "start", "stop", "status" })
        {
            var command = new Command(action, action switch
            {
                "start" => "Request folding to start, then exit.",
                "stop" => "Request folding to stop, then exit.",
                _ => "Print the client-reported folding state, then exit."
            });
            command.SetAction(async (result, token) =>
            {
                try
                {
                    (AppConfig config, _) = await ConfigFile.LoadOptionalAsync(result.GetValue(configOption), token, defaultConfigPath);
                    string endpoint = result.GetValue(endpointOption) ?? config.FahEndpoint ?? AppConfig.DefaultFahEndpoint;
                    if (!AppConfig.IsFahEndpoint(endpoint))
                        throw new InvalidDataException("--fah-endpoint must be an absolute ws:// or wss:// URL without credentials or a fragment.");
                    await run(action, new Uri(endpoint), token);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    await error.WriteLineAsync(token.IsCancellationRequested ? "FAH command canceled." : "FAH command timed out.");
                    return 1;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                    or ArgumentException or NotSupportedException or InvalidOperationException or WebSocketException or TimeoutException)
                {
                    await error.WriteLineAsync($"FAH command failed: {ex.Message}");
                    return 1;
                }
            });
            root.Subcommands.Add(command);
        }
        return root.Parse(args).InvokeAsync();
    }

    private static async Task RunAsync(string action, Uri endpoint, TextWriter output, CancellationToken token)
    {
        using var connection = new FahConnection();
        var state = new FahState();
        state.Apply(await connection.ConnectAsync(endpoint, token), initial: true);
        if (action == "status")
        {
            Models.FoldingStatus status = state.Status!;
            await output.WriteLineAsync($"FAH: online | FOLDING: {status.IsWorking.ToString().ToLowerInvariant()} | Configured to fold: {status.IsConfiguredToFold.ToString().ToLowerInvariant()}");
        }
        else
        {
            await connection.SetFoldingAsync(action == "start", token);
            await output.WriteLineAsync($"Requested Folding@home {(action == "start" ? "start" : "stop")}.");
        }
        await connection.CloseAsync(token);
    }
}
