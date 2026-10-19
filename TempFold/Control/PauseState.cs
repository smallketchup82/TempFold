// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Models;

namespace TempFold.Control;

// Owned by TemperatureController under its state lock. Automatic sources cannot
// change a manual pause; their current state is checked when the user releases it.
internal sealed class PauseState
{
    internal PauseReason? Reason { get; private set; }
    internal bool IsPaused => Reason is not null;

    internal void SetPaused(bool paused, bool manual = false)
    {
        if (!manual && Reason == PauseReason.Manual) return;
        Reason = paused ? manual ? PauseReason.Manual : PauseReason.GameMode : null;
    }
}
