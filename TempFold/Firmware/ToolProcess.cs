// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using System.ComponentModel;

namespace TempFold.Firmware;

internal static class ToolProcess
{
    internal static async Task<string> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken token,
        TimeSpan? timeout = null, bool secretOutput = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { throw new IOException($"{Path.GetFileName(tool)} was not found or could not be started."); }
        using var registration = deadline.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        Task<string> output = ReadBoundedAsync(process.StandardOutput, deadline);
        Task<string> error = ReadBoundedAsync(process.StandardError, deadline);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string stdout = await output;
            string stderr = await error;
            if (process.ExitCode != 0)
                throw new IOException(secretOutput ? "The operating system could not supply saved Wi-Fi settings."
                    : $"{Path.GetFileName(tool)} failed: {(stderr + stdout)[..Math.Min(3000, stderr.Length + stdout.Length)]}");
            return stdout;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException($"{Path.GetFileName(tool)} timed out."); }
        finally
        {
            try { await Task.WhenAll(output, error); } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var result = new System.Text.StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
        {
            if (result.Length + count > 2 * 1024 * 1024)
            { deadline.Cancel(); throw new IOException("Tool output exceeded the size limit."); }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
