// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.ComponentModel;
using System.Diagnostics;
using TempFold.Models;

namespace TempFold.Integrations;

internal sealed class GameModeClient
{
    public GameModeStatus GetStatus()
    {
        try
        {
            using Process? process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "gamemoded",
                    ArgumentList = { "--status" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

            if (process is null)
                return new GameModeStatus(null, "could not start gamemoded");

            if (!ProcessUtilities.WaitForExitOrKill(process, TimeSpan.FromSeconds(3)))
                return new GameModeStatus(null, "gamemoded timed out");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            string response = $"{output}\n{error}";

            if (process.ExitCode != 0)
            {
                string detail = response.Trim();
                return new GameModeStatus(
                    null,
                    detail.Length == 0 ? $"gamemoded exited with code {process.ExitCode}" : detail);
            }

            if (response.Contains("gamemode is inactive", StringComparison.OrdinalIgnoreCase))
                return new GameModeStatus(false, null);

            if (response.Contains("gamemode is active", StringComparison.OrdinalIgnoreCase))
                return new GameModeStatus(true, null);

            return new GameModeStatus(null, "unrecognized response from gamemoded");
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            return new GameModeStatus(null, exception.Message);
        }
    }
}