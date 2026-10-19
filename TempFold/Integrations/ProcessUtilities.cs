// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;

namespace TempFold.Integrations;

internal static class ProcessUtilities
{
    public static bool WaitForExitOrKill(Process process, TimeSpan timeout)
    {
        if (process.WaitForExit(timeout))
            return true;

        try
        {
            process.Kill(true);
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout and the kill attempt.
        }

        return false;
    }
}