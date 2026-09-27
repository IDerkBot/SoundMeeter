using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.Models;

/// <summary>
/// Результат применения миграций к загруженному файлу настроек.
/// </summary>
public enum MigrationOutcome
{
    /// <summary>Файл в поддерживаемой версии (возможно, после миграций).</summary>
    Loaded,

    /// <summary>
    /// Файл записан более новой версией приложения. Данные не применены, файл
    /// не перезаписывается до явного сохранения пользователем.
    /// </summary>
    UnsupportedNewerVersion
}

/// <summary>
/// Цепочка миграций схемы настроек (SM-A05).
///
/// Правило простое: файл всегда несёт номер версии, а переход между соседними
/// версиями — отдельная маленькая функция. Добавить следующее изменение формата
/// значит дописать один шаг <c>n → n+1</c>, а не разбирать файл вручную в
/// <see cref="Services.SettingsService"/>: иначе каждое новое поле обрастает
/// собственной точечной проверкой, и через год их будет пять в разных местах.
///
/// Версия 0 — это формат до введения версионирования, то есть все settings.json,
/// написанные сборками 1.0.x. Он поддерживается и мигрируется в
/// <see cref="CurrentSchemaVersion"/>.
/// </summary>
public static class SettingsMigrator
{
    /// <summary>Версия схемы, которую понимает и пишет эта сборка.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Приводит загруженный снимок к <see cref="CurrentSchemaVersion"/>.
    /// Возвращает <see cref="MigrationOutcome.UnsupportedNewerVersion"/>, если файл
    /// записан более новой версией: тогда данные применять нельзя (мы не знаем
    /// смысла новых полей), но и затирать файл нельзя — пользователь его не потеряет.
    /// </summary>
    public static MigrationOutcome Migrate(AppSettings settings, out string message)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.SchemaVersion > CurrentSchemaVersion)
        {
            message = $"settings.json записан версией схемы {settings.SchemaVersion}, " +
                      $"а сборка понимает только {CurrentSchemaVersion} — настройки не применены";
            return MigrationOutcome.UnsupportedNewerVersion;
        }

        var logger = AppLog.For("SettingsMigrator");
        var from = settings.SchemaVersion;
        while (settings.SchemaVersion < CurrentSchemaVersion)
            MigrateStep(settings);

        settings.SchemaVersion = CurrentSchemaVersion;

        if (from != CurrentSchemaVersion)
        {
            message = $"схема настроек мигрирована {from} → {CurrentSchemaVersion}";
            logger.LogInformation("{Message}", message);
        }
        else
        {
            message = "";
        }

        return MigrationOutcome.Loaded;
    }

    /// <summary>Один шаг цепочки: n → n+1. Следующие шаги добавляются здесь.</summary>
    private static void MigrateStep(AppSettings settings)
    {
        switch (settings.SchemaVersion)
        {
            case 0:
                Migrate0To1(settings);
                break;
            default:
                // Сюда попасть нельзя: Migrate крутится только пока версия < Current.
                settings.SchemaVersion = CurrentSchemaVersion;
                break;
        }
    }

    /// <summary>
    /// 0 → 1. Формат не менялся, миграция приводит файл к инвариантам, на которых
    /// дальше построен весь остальной код: списки не null, а значения громкости и
    /// посылок — конечные числа в рабочем диапазоне.
    ///
    /// Отдельно про legacy-поле MIDI <c>IsMomentary</c>: оно читается
    /// сеттером <see cref="MidiBinding.LegacyMomentary"/> и переносится в
    /// <see cref="MidiBinding.Mode"/> прямо при десериализации, отдельный шаг для
    /// этого не нужен — но потерять его нельзя.
    /// </summary>
    private static void Migrate0To1(AppSettings settings)
    {
        settings.PersistentRoutes ??= new List<DeviceRouteRule>();
        settings.RemovedDeviceIds ??= new List<string>();
        settings.HiddenDeviceIds ??= new List<string>();
        settings.Inputs ??= new List<InputChannelModel>();
        settings.Outputs ??= new List<OutputBusModel>();
        settings.Midi ??= new MidiSettings();
        settings.LogLevel = AppLog.ParseLevel(settings.LogLevel).ToString();

        foreach (var input in settings.Inputs)
        {
            input.BusRouting ??= new Dictionary<string, BusRouting>();
            input.VolumeDb = Sanitize(input.VolumeDb, -60f, 12f);
            foreach (var route in input.BusRouting.Values)
                route.GainDb = Sanitize(route.GainDb, -60f, 12f);
        }

        foreach (var bus in settings.Outputs)
            bus.VolumeDb = Sanitize(bus.VolumeDb, -60f, 12f);

        settings.SchemaVersion = 1;
    }

    /// <summary>
    /// Значение вне диапазона или NaN (битый JSON, правка руками) заменяется
    /// безопасным значением: иначе один такой файл даёт NaN в аудиобуфере.
    /// </summary>
    private static float Sanitize(float value, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : 0f;
}
