// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

namespace TempFold.Logging;

// Monotonic time keeps the animation independent of wall-clock adjustments.
internal sealed class SensorFlash(TimeProvider timeProvider)
{
    private static readonly TimeSpan duration = TimeSpan.FromSeconds(0.75);
    private long? started;

    public void Restart() => started = timeProvider.GetTimestamp();
    public void Reset() => started = null;
    public double Strength
    {
        get
        {
            if (started is not { } timestamp)
                return 0;
            double progress = Math.Clamp(timeProvider.GetElapsedTime(timestamp).TotalSeconds / duration.TotalSeconds, 0, 1);
            return 1 - progress;
        }
    }
}
