// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.CommandLine;
using Spectre.Console;
using TempFold.Configuration;
using TempFold.Firmware;

namespace TempFold.CommandLine;

internal static class FirmwareCommand
{
    internal static Task<int> InvokeAsync(string[] args, string? defaultConfigPath = null)
    {
        var root = new RootCommand("Install and configure TFirm on an ESP32-C3.");
        var setup = new Command("setup", "Interactive ESP32-C3 firmware installer.");
        var repository = new Option<string>("--repository") { Description = "GitHub firmware release repository.", DefaultValueFactory = _ => "smallketchup82/TempFold" };
        var config = new Option<string?>("--config") { Description = "TempFold config file to use for the server listener port." };
        var listener = new Option<string?>("--api-listen-url") { Description = "Override the TempFold server listener URL used to derive its port." };
        setup.Options.Add(repository); setup.Options.Add(config); setup.Options.Add(listener);
        foreach (var command in new[] { setup, new Command("clear", "Interactively confirm and erase all ESP32-C3 flash."), new Command("update", "Edit saved TFirm settings; Flash persists configuration over USB.") })
        {
            if (command != setup) { command.Options.Add(config); command.Options.Add(listener); }
            command.SetAction(async (result, token) =>
            {
                try
                {
                    if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
                        throw new InvalidOperationException($"firmware {command.Name} requires an interactive terminal.");
                    var (settings, _) = await ConfigFile.LoadOptionalAsync(result.GetValue(config), token, defaultConfigPath);
                    string? listen = result.GetValue(listener) ?? settings.ApiListenUrl ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
                    return command == setup
                        ? await FirmwareInstaller.RunAsync(result.GetValue(repository)!, listen, token)
                        : await FirmwareInstaller.ManageAsync(command.Name, listen, token);
                }
                catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]Firmware operation cancelled.[/]"); return 130; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException or HttpRequestException or System.ComponentModel.Win32Exception or KeyNotFoundException)
                {
                    AnsiConsole.MarkupLine("[red]Firmware operation failed:[/] " + Markup.Escape(ex.Message));
                    return 1;
                }
            });
            root.Subcommands.Add(command);
        }
        return root.Parse(args).InvokeAsync();
    }
}
