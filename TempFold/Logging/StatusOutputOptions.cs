// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

namespace TempFold.Logging;

internal sealed record StatusOutputOptions(
    bool Interactive = true,
    bool Colors = true,
    bool Effects = false,
    bool Json = false,
    double IntervalSeconds = 1);
