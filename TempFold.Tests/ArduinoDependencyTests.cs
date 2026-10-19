using TempFold.Firmware;

namespace TempFold.Tests;

public sealed class ArduinoDependencyTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task InstallerWorkspaceCleansTemporaryDependenciesOnEveryExit(string outcome)
    {
        string? directory = null;
        Task<int> operation = FirmwareInstaller.WithWorkspaceAsync(async workspace =>
        {
            directory = workspace;
            Directory.CreateDirectory(Path.Combine(workspace, "arduino-data", "packages"));
            await File.WriteAllTextAsync(
                Path.Combine(
                    workspace,
                    "arduino-data",
                    "packages",
                    "fixture"),
                "temporary tools");
            if (outcome == "failure")
                throw new IOException("fixture installation failed");
            if (outcome == "cancel")
                throw new OperationCanceledException();
            return 0;
        });
        if (outcome == "failure")
            await Assert.ThrowsAsync<IOException>(() => operation);
        else if (outcome == "cancel")
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        else
            Assert.Equal(0, await operation);

        Assert.NotNull(directory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task PersistentCoreIsReusedAndTemporaryInstallIsIsolated()
    {
        if (OperatingSystem.IsWindows())
            return;
        string workspace = Directory.CreateTempSubdirectory("tempfold-tools-test-").FullName;
        try
        {
            string cli = Path.Combine(workspace, "cli");

            await File.WriteAllTextAsync(
                cli,
                """
                    #!/bin/sh
                    scope='ROOT/persistent'
                    for arg in "$@"; do
                        if [ "$arg" = '--config-file' ]; then scope='ROOT/temporary'; fi
                    done
                    case "$1 $2" in
                      'core list')
                        if [ -f "$scope" ]; then printf '%s' '{"platforms":[{"id":"esp32:esp32","installed_version":"3.3.12"}]}'; else printf '%s' '{"platforms":[]}'; fi ;;
                      'core install') touch "$scope" ;;
                      'config get') printf '%s' '"ROOT/data"' ;;
                    esac
                    """.Replace("ROOT", workspace));
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tools = new ArduinoTools(cli);

            Assert.False(await tools.HasCoreAsync(default));
            Assert.Equal(Path.Combine(workspace, "data"), await tools.DataDirectoryAsync(default));

            await tools.InstallCoreAsync(default);

            Assert.True(await new ArduinoTools(cli).HasCoreAsync(default));

            tools.UseTemporaryConfig(workspace);

            Assert.False(await tools.HasCoreAsync(default));

            await tools.InstallCoreAsync(default);

            Assert.True(await tools.HasCoreAsync(default));

            using var config = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(workspace, "arduino-cli.yaml")));
            string data = config.RootElement.GetProperty("directories").GetProperty("data").GetString()!;

            Assert.StartsWith(workspace, data);
            Assert.True(await new ArduinoTools(cli).HasCoreAsync(default));
        }
        finally
        {
            Directory.Delete(workspace, true);
        }

        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public async Task SourceBuildCanReuseCoreWithoutReplacingUserLibraries()
    {
        string workspace = Directory.CreateTempSubdirectory("tempfold-tools-test-").FullName;
        try
        {
            string coreData = Path.Combine(workspace, "reusable-core");

            new ArduinoTools("unused-cli").UseTemporaryConfig(workspace, coreData);
            using var config = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(workspace, "arduino-cli.yaml")));
            var directories = config.RootElement.GetProperty("directories");

            Assert.Equal(coreData, directories.GetProperty("data").GetString());
            Assert.Equal(Path.Combine(workspace, "arduino-user"), directories.GetProperty("user").GetString());
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task CoreDiscoveryFailureRetainsDiagnostic()
    {
        if (OperatingSystem.IsWindows())
            return;
        string workspace = Directory.CreateTempSubdirectory("tempfold-tools-test-").FullName;
        try
        {
            string cli = Path.Combine(workspace, "cli");

            await File.WriteAllTextAsync(
                cli,
                "#!/bin/sh\nprintf 'fixture: data directory permission denied' >&2\nexit 1\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var error = await Assert.ThrowsAsync<IOException>(() => new ArduinoTools(cli).HasCoreAsync(default));

            Assert.Contains("data directory permission denied", error.Message);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }
}