// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using System.Text.Json.Serialization;
using TempFold.Models;

namespace TempFold.Api;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SensorRequest))]
[JsonSerializable(typeof(SensorError))]
[JsonSerializable(typeof(StatusReport))]
internal partial class SensorJsonContext : JsonSerializerContext;
