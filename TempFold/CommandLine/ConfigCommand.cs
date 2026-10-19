// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.CommandLine;
using System.Text.Json;
using TempFold.Configuration;

namespace TempFold.CommandLine;

internal static class ConfigCommand
{
    internal static Task<int> InvokeAsync(string[] args, string? defaultConfigPath = null)
    {
        var root = new RootCommand("Create or validate a TempFold configuration file.");
        foreach (string name in new[] { "create", "validate" })
        {
            var command = new Command(name, name == "create"
                ? "Create a configuration template without overwriting an existing file."
                : "Validate supported settings and their values.");
            var pathOption = new Option<string?>("--config") { Description = "Configuration path; create defaults beside the executable, validate also searches ~/.config/tempfold/config.json." };
            var pathArgument = new Argument<string?>("path") { Arity = ArgumentArity.ZeroOrOne, Description = "Configuration file path." };
            command.Options.Add(pathOption);
            command.Arguments.Add(pathArgument);
            command.SetAction(async (result, token) =>
            {
                try
                {
                    string? option = result.GetValue(pathOption);
                    string? argument = result.GetValue(pathArgument);
                    if (option is not null && argument is not null)
                        throw new InvalidDataException("Specify the config path once, either as a positional path or with --config.");
                    string path = Path.GetFullPath(option ?? argument ?? defaultConfigPath ?? ConfigFile.DefaultPath);
                    if (name == "create")
                        await ConfigFile.CreateAsync(path, token);
                    else
                    {
                        var (_, loadedPath) = await ConfigFile.LoadOptionalAsync(option ?? argument, token, defaultConfigPath);
                        path = loadedPath ?? throw new FileNotFoundException("No configuration file found.", path);
                    }
                    Console.WriteLine($"{(name == "create" ? "Created" : "Valid")} configuration: {path}");
                    return 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
                {
                    Console.Error.WriteLine($"Configuration error: {ex.Message}");
                    return 1;
                }
            });
            root.Subcommands.Add(command);
        }
        return root.Parse(args).InvokeAsync();
    }
}
