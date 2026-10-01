using SoundMeeter.Audio;
using SoundMeeter.Models;
using SoundMeeter.Services;
using Xunit;

/// <summary>
/// Настройки поведения приложения (SM-D01/SM-D02) в файле settings.json.
/// Требует отдельной миграции 4 → 5: старые файлы полей не содержат, а
/// расхождение «в настройках включено, в системе выключено» для автозапуска —
/// источник вечного заблуждения пользователя.
/// </summary>
public class AppBehaviourSettingsTests
{
    [Fact]
    public void VersionFourGetsTrayOnAndStartupOff()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 4,
            // Руками выставленные значения не должны пережить миграцию: трей
            // без правила «закрыть = свернуть» выглядит как зависшее приложение.
            TrayEnabled = false,
            RunAtStartup = true
        };

        SettingsMigrator.Migrate(settings, out _);

        Assert.Equal(5, settings.SchemaVersion);
        Assert.True(settings.TrayEnabled);
        Assert.False(settings.RunAtStartup);
    }

    [Fact]
    public void FreshInstallDefaultsToTrayOnAndStartupOff()
    {
        // Дефолты заданы свойствами, а не миграцией: новый файл сразу получает
        // нужные значения, и первый запуск не зависит от номера схемы.
        var settings = new AppSettings();

        Assert.True(settings.TrayEnabled);
        Assert.False(settings.RunAtStartup);
    }

    [Fact]
    public void BehaviourFlagsSurviveJsonRoundTrip()
    {
        var settings = new AppSettings { SchemaVersion = 5, TrayEnabled = false, RunAtStartup = true };

        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        SettingsMigrator.Migrate(reloaded, out _);

        Assert.False(reloaded.TrayEnabled);
        Assert.True(reloaded.RunAtStartup);
    }

    [Fact]
    public void CurrentSchemaVersionIsFive()
    {
        Assert.Equal(5, SettingsMigrator.CurrentSchemaVersion);
    }
}
