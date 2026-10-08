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

        // Не «5», а текущая версия: файл версии 4 обязан доехать до последней
        // схемы, иначе на нём по дороге потерялись бы поля следующих шагов.
        Assert.Equal(SettingsMigrator.CurrentSchemaVersion, settings.SchemaVersion);
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
        var settings = new AppSettings
        {
            SchemaVersion = SettingsMigrator.CurrentSchemaVersion,
            TrayEnabled = false,
            RunAtStartup = true
        };

        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        SettingsMigrator.Migrate(reloaded, out _);

        Assert.False(reloaded.TrayEnabled);
        Assert.True(reloaded.RunAtStartup);
    }

    /// <summary>
    /// Номер схемы закреплён намеренно: смена формата файла обязана быть
    /// осознанным шагом (добавить <c>n → n+1</c> в <see cref="SettingsMigrator"/>),
    /// а не побочным эффектом правки модели. Тест ломается ровно тогда, когда
    /// версия поехала без нового шага миграции.
    /// </summary>
    [Fact]
    public void CurrentSchemaVersionIsEight()
    {
        // 6 → 7: модуль синтеза речи (SM-E01) и генерируемые входные стрипы.
        // 7 → 8: интеграция с Twitch (SM-F01) — вход, чат, статус трансляции.
        Assert.Equal(8, SettingsMigrator.CurrentSchemaVersion);
    }
}
