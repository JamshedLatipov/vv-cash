using System.IO;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

/// <summary>Миграция ролей печати. Настройка появляется у парка, который её
/// никогда не видел, поэтому «поля нет в файле» — основной случай, а не крайний.</summary>
public class PrinterRolesSettingsTest
{
    private static string WriteSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vv-settings-{Path.GetRandomFileName()}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void PrinterWithoutRolesInFile_PrintsReceiptsAsBefore()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100", "IsEnabled": true }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
    }

    [Fact]
    public void RolesAreReadAsNames_BecauseThisFileIsEditedByHand()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "Ticket, KitchenOrder" }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Ticket | PrintRole.KitchenOrder, settings.Printers[0].Roles);
    }

    [Fact]
    public void RolesAreWrittenBackAsNames()
    {
        var path = WriteSettings("{}");
        var settings = new SettingsService(path);
        settings.Printers = new()
        {
            new PrinterConfig { Name = "a", Roles = PrintRole.Receipt | PrintRole.Ticket }
        };

        settings.Save();

        Assert.Contains("\"Receipt, Ticket\"", File.ReadAllText(path));
    }

    [Fact]
    public void MistypedRole_FallsBackToReceipt_WithoutWipingTheRestOfTheFile()
    {
        var path = WriteSettings("""
        {
          "BackendUrl": "https://example.com",
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "Bogus" }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
        // The point of this assertion: without the fix, the JsonException from the
        // bad "Roles" token is caught by Load()'s catch-all, which resets the
        // entire SettingsData — BackendUrl included, not just the offending role.
        Assert.Equal("https://example.com", settings.BackendUrl);
    }

    [Fact]
    public void PartiallyValidRoleList_FallsBackToReceiptEntirely()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "Ticket, Bogus" }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
    }

    [Fact]
    public void NumberInsteadOfString_DoesNotThrow()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": 3 }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
    }

    [Fact]
    public void NoneIsARealSetting_DistinctFromTheFieldBeingAbsent()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "None" }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.None, settings.Printers[0].Roles);
    }

    [Fact]
    public void RoleNamesAreMatchedCaseInsensitively()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "ticket, KITCHENorder" }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Ticket | PrintRole.KitchenOrder, settings.Printers[0].Roles);
    }

    [Fact]
    public void RoleAsJsonArray_FallsBackToReceipt_WithoutWipingTheRestOfTheFile()
    {
        var path = WriteSettings("""
        {
          "BackendUrl": "https://example.com",
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": ["Receipt", "Ticket"] }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
        // Same point as the "Bogus" test above: an array is a plausible hand-edit
        // from someone listing roles without knowing the comma-string convention,
        // and it must not take the rest of the file down with it.
        Assert.Equal("https://example.com", settings.BackendUrl);
    }

    [Fact]
    public void RoleAsJsonObject_FallsBackToReceipt_WithoutWipingTheRestOfTheFile()
    {
        var path = WriteSettings("""
        {
          "BackendUrl": "https://example.com",
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": {"x": 1} }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
        Assert.Equal("https://example.com", settings.BackendUrl);
    }

    [Fact]
    public void TrailingComma_IsTolerated_LikeTheOldConverterAccepted()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "Ticket, KitchenOrder," }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Ticket | PrintRole.KitchenOrder, settings.Printers[0].Roles);
    }

    [Fact]
    public void OnlyCommas_HaveNoRecognisedName_AndFallBackToReceipt()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "," }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
    }

    [Fact]
    public void RoleAsJsonNull_DoesNotThrow()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": null }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
    }
}
