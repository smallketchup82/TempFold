using System.Text.Json;
using TempFold.Firmware;

namespace TempFold.Tests;

public sealed class FirmwareConfigurationTests
{
    private static FirmwareConfiguration Saved =>
        new(
            "Desk",
            "Network",
            "http://192.168.1.20:5147",
            5000,
            8,
            9,
            68,
            true);

    [Fact]
    public void PanelLoadsStoredBoardValuesInsteadOfDefaults()
    {
        using var response = JsonDocument.Parse(
            """
            {"chip":"ESP32-C3","protocolVersion":1,"configPatch":true,
             "boardName":"Desk","wifiSsid":"Network","apiEndpoint":"http://192.168.1.20:5147",
             "sensorIntervalMs":5000,"sdaPin":8,"sclPin":9,"sensorAddress":68,"wifiPasswordSet":true}
            """);

        Assert.Equal(Saved, FirmwareConfiguration.Read(response.RootElement));
    }

    [Fact]
    public void UnchangedPasswordAndWiringAreOmittedFromPatch()
    {
        string patch = (Saved with { BoardName = "Bedroom", SensorIntervalMs = 2000 }).Patch(Saved, null);
        using var document = JsonDocument.Parse(patch);

        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.Equal("Bedroom", document.RootElement.GetProperty("boardName").GetString());
        Assert.Equal(2000, document.RootElement.GetProperty("sensorIntervalMs").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("wifiPassword", out _));
        Assert.False(document.RootElement.TryGetProperty("sdaPin", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("new-password")]
    public void ExplicitPasswordActionsAreIncludedInPatch(string password)
    {
        using var document = JsonDocument.Parse(Saved.Patch(Saved, password));

        Assert.Equal(password, document.RootElement.GetProperty("wifiPassword").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void DisablingNetworkingPreservesCredentials()
    {
        var disabled = Saved with { ApiEndpoint = "" };
        using var document = JsonDocument.Parse(disabled.Patch(Saved, null));

        Assert.Equal("", document.RootElement.GetProperty("apiEndpoint").GetString());
        Assert.Single(document.RootElement.EnumerateObject());

        disabled.Verify(disabled, null);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(3600001)]
    public void InvalidIntervalCannotBeApplied(int interval) =>
        Assert.Throws<InvalidDataException>(() => (Saved with { SensorIntervalMs = interval }).Patch(Saved, null));

    [Fact]
    public void ReadbackChecksIntervalWiringAndPasswordPresence()
    {
        Saved.Verify(Saved, null);
        Saved.Verify(Saved with { WifiPasswordSet = false }, "");

        Assert.Throws<InvalidDataException>(() => Saved.Verify(Saved with { SensorIntervalMs = 1000 }, null));
        Assert.Throws<InvalidDataException>(() => Saved.Verify(Saved with { SdaPin = 3 }, null));
        Assert.Throws<InvalidDataException>(() => Saved.Verify(Saved with { WifiPasswordSet = false }, null));
    }

    [Fact]
    public void UnsupportedFirmwareCannotReceivePreservingUpdates()
    {
        using var status = JsonDocument.Parse("""{"chip":"ESP32-C3","protocolVersion":1}""");
        var error = Assert.Throws<InvalidDataException>(() => FirmwareConfiguration.Read(status.RootElement));

        Assert.Contains("firmware setup", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void DeviceSelectionAlwaysOffersRefreshAndCancel(int count)
    {
        var ports = Enumerable.Range(0, count)
            .Select(i => new BoardPort("port-" + i, i == 0 ? "Unidentified serial device" : "ESP32-C3"))
            .ToArray();
        var choices = FirmwareInstaller.PortChoices(ports);

        Assert.Equal(count + 2, choices.Length);
        Assert.Equal(new[] { "Refresh", "Cancel" }, choices[^2..]);

        for (int i = 0; i < count; ++i)
            Assert.Contains(ports[i].Address, choices[i]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureRequiresExplicitConfirmation(bool confirmed)
    {
        bool erased = false;
        bool result = await FirmwareInstaller.EraseConfirmedAsync(
            () => confirmed,
            () =>
            {
                erased = true;
                return Task.FromResult(true);
            });

        Assert.Equal(confirmed, erased);
        Assert.Equal(confirmed, result);
    }
}