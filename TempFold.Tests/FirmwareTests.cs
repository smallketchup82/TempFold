using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TempFold.CommandLine;
using TempFold.Firmware;

namespace TempFold.Tests;

public sealed class FirmwareTests
{
    [Theory]
    [InlineData("http://192.168.1.20:5147", true)]
    [InlineData("http://tempfold.local:5147", true)]
    [InlineData("http://localhost:5147", false)]
    [InlineData("http://127.0.0.1:5147", false)]
    [InlineData("http://0.0.0.0:5147", false)]
    [InlineData("https://192.168.1.20:5147", false)]
    [InlineData("http://192.168.1.20:5147/", false)]
    [InlineData("http://192.168.1.20:5147/sensor", false)]
    [InlineData("http://user:secret@192.168.1.20:5147", false)]
    [InlineData("http://[::1]:5147", false)]
    public void EndpointsMatchFirmwareSupport(string endpoint, bool expected) =>
        Assert.Equal(expected, FirmwareValidation.ValidEndpoint(endpoint));

    [Fact]
    public void PasswordsUseUtf8ByteLimits()
    {
        Assert.True(FirmwareValidation.ValidPassword(""));
        Assert.False(FirmwareValidation.ValidPassword("short"));
        Assert.True(FirmwareValidation.ValidPassword(new string('a', 63)));
        Assert.True(FirmwareValidation.ValidPassword(new string('a', 64)));
        Assert.False(FirmwareValidation.ValidPassword(new string('z', 64)));
        Assert.False(FirmwareValidation.ValidPassword(new string('é', 32)));
        Assert.False(FirmwareValidation.ValidPassword("password\n"));
        Assert.False(FirmwareValidation.ValidText("secret\0", 64));
    }

    [Fact]
    public void ProtocolIgnoresTelemetryAndOtherCommandResponses()
    {
        Assert.Null(FirmwareValidation.ParseResponse("TEMP=22.5", "GET"));
        Assert.Null(FirmwareValidation.ParseResponse("TF={\"command\":\"SET\",\"ok\":true}", "GET"));

        using var result = FirmwareValidation.ParseResponse("TF={\"command\":\"GET\",\"ok\":true}", "GET");

        Assert.NotNull(result);
        Assert.Throws<InvalidDataException>(() => FirmwareValidation.ParseResponse(
            "TF={\"command\":\"SET\",\"ok\":false,\"error\":\"STORAGE_FAILED\"}",
            "SET"));
    }

    [Fact]
    public void ConfigurationRequiresSuccessfulPersistenceAndCompatibleFirmware()
    {
        var settings = new DeviceSettings(
            "Desk",
            "WiFi",
            "password",
            "http://192.168.1.20:5147");
        using var document = JsonDocument.Parse(
            """
            {"boardName":"Desk","wifiSsid":"WiFi","apiEndpoint":"http://192.168.1.20:5147",
             "wifiPasswordSet":true,"restartRequired":false,"chip":"ESP32-C3","protocolVersion":1,"firmwareVersion":"2026.10.0"}
            """);

        FirmwareInstaller.ValidateSettings(document.RootElement, settings);
        FirmwareInstaller.ValidateDevice(document.RootElement, "2026.10.0");

        Assert.Throws<InvalidDataException>(() =>
            FirmwareInstaller.ValidateSettings(document.RootElement, settings with { BoardName = "Other" }));
        Assert.Throws<InvalidDataException>(() => FirmwareInstaller.ValidateDevice(document.RootElement, "2026.10.1"));

        string serialized = JsonSerializer.Serialize(settings, FirmwareJsonContext.Default.DeviceSettings);
        using var payload = JsonDocument.Parse(serialized);

        Assert.Equal("Desk", payload.RootElement.GetProperty("boardName").GetString());
    }

    [Fact]
    public void MergedImageRequiresC3BootloaderPartitionTableAndApplication()
    {
        byte[] image = Image();

        FirmwareImage.ValidateMerged(new MemoryStream(image));
        image[0x10000 + 12] = 0;

        Assert.Throws<InvalidDataException>(() => FirmwareImage.ValidateMerged(new MemoryStream(image)));
        Assert.Throws<InvalidDataException>(() => FirmwareImage.ValidateMerged(new MemoryStream(new byte[1000])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseArchiveChecksChecksumWithoutExtractingOtherFiles(bool corrupt)
    {
        string folder = Directory.CreateTempSubdirectory("tempfold-test-").FullName;
        try
        {
            byte[] image = Image();
            string hash = corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(image));
            string manifest = $$"""
                                {"chip":"esp32c3","protocolVersion":1,"firmwareVersion":"2026.10.0",
                                 "factoryImage":{"file":"TFirm.ino.merged.bin","offset":"0x0"},
                                 "files":[{"file":"TFirm.ino.merged.bin","bytes":{{image.Length}},"sha256":"{{hash}}"}]}
                                """;
            string archive = Path.Combine(folder, "release.tar.gz");
            using (var file = File.Create(archive))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
            {
                foreach (var (name, bytes) in new[]
                         {
                             ("TFirm.ino.merged.bin", image),
                             ("manifest.json", Encoding.UTF8.GetBytes(manifest)),
                             ("../escaped-secret", new byte[] { 1 })
                         })
                {
                    using var data = new MemoryStream(bytes);
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
                }
            }

            if (corrupt)
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    FirmwareImage.LoadAsync(archive, folder, CancellationToken.None));
            else
            {
                FirmwareImage loaded = await FirmwareImage.LoadAsync(archive, folder, CancellationToken.None);

                Assert.Equal("2026.10.0", loaded.Version);
                Assert.Equal(image, await File.ReadAllBytesAsync(loaded.Path));
                Assert.False(File.Exists(Path.Combine(folder, "escaped-secret")));
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("clear")]
    [InlineData("update")]
    public async Task FirmwareCommandHelpDoesNotStartController(string action)
    {
        bool started = false;
        int result = await ControllerCommand.InvokeAsync(
            ["firmware", action, "--help"],
            (_, _) =>
            {
                started = true;
                return Task.CompletedTask;
            });

        Assert.Equal(0, result);
        Assert.False(started);
    }

    [Fact]
    public async Task SerialProvisioningHandlesSplitResponsesAndRestart()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
            return;
        const string emulator = """
                                import os,pty,tty,json,time
                                master,slave=pty.openpty();tty.setraw(slave)
                                print(os.ttyname(slave),flush=True)
                                config={"boardName":"TempFold sensor","wifiSsid":"","apiEndpoint":"","wifiPassword":""}
                                pending=False
                                buffer=b""
                                while True:
                                    buffer+=os.read(master,4096)
                                    while b"\n" in buffer:
                                        line,buffer=buffer.split(b"\n",1)
                                        command=line.decode().rstrip("\r").split(" ",2)
                                        if len(command)<2:continue
                                        name=command[1]
                                        if name=="SET":config=json.loads(command[2]);pending=True
                                        if name=="RESTART":pending=False
                                        response={"command":name,"ok":True,"restartRequired":pending}
                                        if name=="GET":response.update({k:v for k,v in config.items() if k!="wifiPassword"});response["wifiPasswordSet"]=bool(config["wifiPassword"])
                                        os.write(master,b"TEMP=21.25\nTF=")
                                        time.sleep(0.3)
                                        os.write(master,json.dumps(response).encode()+b"\n")
                                """;
        var start = new ProcessStartInfo("/usr/bin/python3")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };

        start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(emulator);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            string address = (await process.StandardOutput.ReadLineAsync(timeout.Token))!;
            using var serial = new FirmwareSerial(address);
            var settings = new DeviceSettings(
                "Desk é",
                "Fixture WiFi",
                "fixture-secret",
                "http://192.168.1.20:5147");
            using var saved = await serial.RequestAsync(
                "SET",
                " " + JsonSerializer.Serialize(settings, FirmwareJsonContext.Default.DeviceSettings),
                timeout.Token);

            Assert.True(saved.RootElement.GetProperty("restartRequired").GetBoolean());

            using var restarted = await serial.RequestAsync("RESTART", "", timeout.Token);
            using var status = await serial.RequestAsync("GET", "", timeout.Token);

            FirmwareInstaller.ValidateSettings(status.RootElement, settings);

            Assert.False(status.RootElement.TryGetProperty("wifiPassword", out _));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task ToolFailureRedactsSecretOutputAndTimeoutTerminatesProcess()
    {
        if (OperatingSystem.IsWindows())
            return;
        var error = await Assert.ThrowsAsync<IOException>(() => ToolProcess.RunAsync(
            "/bin/sh",
            ["-c", "printf fixture-secret; exit 1"],
            CancellationToken.None,
            secretOutput: true));

        Assert.DoesNotContain("fixture-secret", error.Message);
        await Assert.ThrowsAsync<IOException>(() => ToolProcess.RunAsync(
            "/bin/sh",
            ["-c", "sleep 30"],
            CancellationToken.None,
            TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ArduinoCliIntegrationDiscoversCoreAndDelegatesFlashing()
    {
        if (OperatingSystem.IsWindows())
            return;
        string workspace = Directory.CreateTempSubdirectory("tempfold-cli-test-").FullName;
        try
        {
            string flasher = Path.Combine(workspace, "flasher");
            string record = Path.Combine(workspace, "arguments");

            await File.WriteAllTextAsync(flasher, "#!/bin/sh\nprintf '%s\\n' \"$@\" > '" + record + "'\n");
            File.SetUnixFileMode(flasher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string cli = Path.Combine(workspace, "arduino-cli");

            await File.WriteAllTextAsync(
                cli,
                """
                    #!/bin/sh
                    case "$1" in
                    core) printf '%s' '{"platforms":[{"id":"esp32:esp32","installed_version":"3.3.12"}]}' ;;
                    board) printf '%s' '{"detected_ports":[{"port":{"protocol":"serial","address":"fixture-port","label":"C3"},"matching_boards":[{"name":"ESP32-C3"}]}]}' ;;
                    compile) printf '%s\n' 'tools.esptool_py.path=DIR' 'tools.esptool_py.cmd=flasher' ;;
                    esac
                    """.Replace("DIR", workspace));
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tools = new ArduinoTools(cli);

            Assert.True(await tools.HasCoreAsync(CancellationToken.None));
            Assert.Contains(await tools.BoardsAsync(CancellationToken.None), p => p.Address == "fixture-port");

            await tools.FlashAsync(
                new FirmwareImage("fixture.merged.bin", null),
                "fixture-port",
                workspace,
                CancellationToken.None);
            string[] arguments = await File.ReadAllLinesAsync(record);

            Assert.Contains("esp32c3", arguments);
            Assert.Contains("write-flash", arguments);
            Assert.Equal(new[] { "0x0", "fixture.merged.bin" }, arguments[^2..]);

            await tools.EraseAsync("fixture-port", workspace, CancellationToken.None);
            string[] eraseArguments = await File.ReadAllLinesAsync(record);

            Assert.Contains("esp32c3", eraseArguments);
            Assert.Contains("fixture-port", eraseArguments);
            Assert.Equal("erase-flash", eraseArguments[^1]);
            Assert.DoesNotContain("write-flash", eraseArguments);
            Assert.DoesNotContain("--force", eraseArguments);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    private static byte[] Image()
    {
        byte[] image = new byte[0x10000 + 256];
        foreach (int offset in new[] { 0, 0x10000 })
        {
            image[offset] = 0xE9;
            image[offset + 12] = 5;
        }

        image[0x8000] = 0xAA;
        image[0x8001] = 0x50;
        return image;
    }
}