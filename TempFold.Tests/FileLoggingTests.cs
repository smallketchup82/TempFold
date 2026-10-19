// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Serilog;
using System.Text.Json;
using TempFold.Logging;

namespace TempFold.Tests;

public sealed class FileLoggingTests
{
    [Fact]
    public void ConfiguredFileReceivesJsonLinesAndAppendsAcrossRuns()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tempfold-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "tempfold.log");
        try
        {
            foreach (string message in new[] { "First run", "Second run" })
            {
                var configuration = new LoggerConfiguration();

                FileLogging.Configure(configuration, path);
                using var logger = configuration.CreateLogger();
                logger.Information("{Message}", message);
            }

            string text = File.ReadAllText(path);
            var events = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();

            Assert.Equal(2, events.Length);
            Assert.All(
                events,
                entry => Assert.Equal("application", entry.RootElement.GetProperty("logType").GetString()));

            foreach (var entry in events)
                entry.Dispose();

            Assert.Contains("First run", text);
            Assert.Contains("Second run", text);
            Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RotationRetainsBoundedFilesAndEveryLineIsJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tempfold-roll-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new LoggerConfiguration().MinimumLevel.Verbose();

            FileLogging.Configure(
                configuration,
                null,
                directory,
                sizeLimit: 300,
                retainedFiles: 3);
            using (var logger = configuration.CreateLogger())
                for (int i = 0; i < 30; i++)
                    logger.Verbose(
                        "{logType}: {readingId} {payload}",
                        "sensorUpdate",
                        i,
                        new string('x', 100));
            string[] files = Directory.GetFiles(directory);

            Assert.InRange(files.Length, 2, 3);

            foreach (var line in files.SelectMany(File.ReadAllLines))
            {
                using var json = JsonDocument.Parse(line);

                Assert.Equal("sensorUpdate", json.RootElement.GetProperty("logType").GetString());
                Assert.Equal("verbose", json.RootElement.GetProperty("level").GetString());
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FileLevelFiltersAndExceptionsStayOnOneJsonLine()
    {
        string path = Path.Combine(Path.GetTempPath(), "tempfold-level-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var configuration = new LoggerConfiguration().MinimumLevel.Verbose();

            FileLogging.Configure(configuration, path, level: "debug");
            using (var logger = configuration.CreateLogger())
            {
                logger.Verbose("Excluded");
                logger.Debug("{logType}: {value}", "userInput", "quote \" and newline\n");
                logger.Error(new InvalidOperationException("multiline\nexception"), "Failure");
            }

            var lines = File.ReadAllLines(path);

            Assert.Equal(2, lines.Length);

            using var first = JsonDocument.Parse(lines[0]);

            Assert.Equal("quote \" and newline\n", first.RootElement.GetProperty("value").GetString());

            using var second = JsonDocument.Parse(lines[1]);

            Assert.Contains("multiline\nexception", second.RootElement.GetProperty("exception").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExistingPlainTextIsRejectedWithoutChangingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            File.WriteAllText(path, "old plain text\n");

            Assert.Throws<InvalidDataException>(() => FileLogging.Configure(new LoggerConfiguration(), path));
            Assert.Equal("old plain text\n", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InaccessibleDestinationFailsVisibly()
    {
        Assert.Throws<UnauthorizedAccessException>(() => FileLogging.Configure(
            new LoggerConfiguration(),
            Path.GetTempPath()));
    }

    [Fact]
    public void OmittedPathDoesNotAddAFileSink()
    {
        var configuration = new LoggerConfiguration();

        FileLogging.Configure(configuration, null);
        using var logger = configuration.CreateLogger();
        logger.Information("Console-only configuration");
    }
}