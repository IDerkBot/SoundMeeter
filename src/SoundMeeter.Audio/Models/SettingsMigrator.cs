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
    public const int CurrentSchemaVersion = 6;

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
            message = Services.Loc.Get("Sm.Settings.NewerSchema", settings.SchemaVersion, CurrentSchemaVersion);
            return MigrationOutcome.UnsupportedNewerVersion;
        }

        var logger = AppLog.For("SettingsMigrator");
        var from = settings.SchemaVersion;
        while (settings.SchemaVersion < CurrentSchemaVersion)
            MigrateStep(settings);

        settings.SchemaVersion = CurrentSchemaVersion;

        if (from != CurrentSchemaVersion)
        {
            message = Services.Loc.Get("Sm.Settings.Migrated", from, CurrentSchemaVersion);
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
            case 1:
                Migrate1To2(settings);
                break;
            case 2:
                Migrate2To3(settings);
                break;
            case 3:
                Migrate3To4(settings);
                break;
            case 4:
                Migrate4To5(settings);
                break;
            case 5:
                Migrate5To6(settings);
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
    /// 1 → 2. Появились назначения кнопок FUNC у входных стрипов: список из
    /// ровно <see cref="InputChannelModel.FuncButtonSlotCount"/> записей, у каждой —
    /// непустой список Id шин и непустая подпись.
    ///
    /// Плоский список, а не словарь по номеру слота, выбран потому, что разметка
    /// стрипа обращается к первому и второму назначению напрямую, а файл,
    /// написанный вручную, может содержать сколько угодно записей — лишние
    /// отбрасываем, недостающие добираем пустыми (кнопка без назначения просто
    /// не делает ничего).
    /// </summary>
    private static void Migrate1To2(AppSettings settings)
    {
        foreach (var input in settings.Inputs)
            input.FuncButtons = NormalizeFuncButtons(input.FuncButtons);

        settings.SchemaVersion = 2;
    }

    /// <summary>
    /// 2 → 3. Кнопки FUNC стали переключателями: у стрипа появились номер
    /// нажатой кнопки (<see cref="InputChannelModel.EngagedFunc"/>) и снимок
    /// роутинга, который она заменила
    /// (<see cref="InputChannelModel.FuncBaseRouting"/>), — без него снятие
    /// кнопки после перезапуска приложения возвращало бы некуда.
    ///
    /// Приводим к допустимому состоянию: номера вне диапазона, «нажатая кнопка
    /// без назначения» (например, выход отключили, а пресет остался) и «нажатая
    /// кнопка без базового роутинга» (возвращать ей нечего) — всё это означало бы
    /// включённую кнопку, которая не может ни включить, ни вернуть.
    /// </summary>
    private static void Migrate2To3(AppSettings settings)
    {
        foreach (var input in settings.Inputs)
        {
            input.FuncButtons = NormalizeFuncButtons(input.FuncButtons);
            input.FuncBaseRouting ??= new Dictionary<string, bool>();
            input.FuncBaseRouting = input.FuncBaseRouting
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

            bool engagedIsUsable = input.EngagedFunc >= 0
                                   && input.EngagedFunc < InputChannelModel.FuncButtonSlotCount
                                   && input.FuncButtons[input.EngagedFunc].BusIds.Count > 0
                                   && input.FuncBaseRouting.Count > 0;
            if (!engagedIsUsable) input.EngagedFunc = InputChannelModel.NoFuncEngaged;
        }

        settings.SchemaVersion = 3;
    }

    /// <summary>
    /// 3 → 4. У входного стрипа появились эффекты: компрессор, trim, задержка и
    /// реверберация (SM-B05). У старых файлов полей нет, десериализация подставит
    /// значения по умолчанию, а вручную испорченный файл мог оставить вне
    /// диапазона или NaN — приводим всё к рабочим пределам, как и громкость в
    /// <see cref="Migrate0To1"/>: одна такая запись в аудиобуфере глушит канал.
    ///
    /// Нулевые Wet (микширование) у обоих временных эффектов — не совпадение:
    /// так они выключены по умолчанию, и включённый, но неслышный блок
    /// вводил бы в заблуждение.
    /// </summary>
    private static void Migrate3To4(AppSettings settings)
    {
        foreach (var input in settings.Inputs)
        {
            input.CompressorThresholdDb = Sanitize(input.CompressorThresholdDb, -60f, 0f);
            input.CompressorRatio = Sanitize(input.CompressorRatio, 1f, 20f);
            input.CompressorAttackMs = Sanitize(input.CompressorAttackMs, 0.1f, 100f);
            input.CompressorReleaseMs = Sanitize(input.CompressorReleaseMs, 10f, 1000f);
            input.CompressorMakeupDb = Sanitize(input.CompressorMakeupDb, -12f, 24f);

            input.FxGainDb = Sanitize(input.FxGainDb, -60f, 24f);

            input.DelayTimeMs = Sanitize(input.DelayTimeMs, 1f, 2000f);
            input.DelayFeedback = Sanitize(input.DelayFeedback, 0f, 90f);
            input.DelayDampingHz = Sanitize(input.DelayDampingHz, 200f, 18000f);
            input.DelayMix = Sanitize(input.DelayMix, 0f, 100f);

            input.ReverbSize = Sanitize(input.ReverbSize, 0f, 100f);
            input.ReverbDamping = Sanitize(input.ReverbDamping, 0f, 100f);
            input.ReverbMix = Sanitize(input.ReverbMix, 0f, 100f);
        }

        settings.SchemaVersion = 4;
    }

    /// <summary>
    /// 4 → 5. Появились настройки поведения приложения: значок в трее
    /// (SM-D02) и автозапуск вместе с Windows (SM-D01).
    ///
    /// У старых файлов полей нет, и десериализация подставит значения
    /// свойств — здесь важно лишь не дать файлу, правленному руками, выключить
    /// трей: без иконки и без правила «закрыть = свернуть» микшер стал бы
    /// выглядеть зависшим (окно исчезло, процесса нет в списке задач).
    /// Поэтому трей приводится к включённому, а автозапуск — к выключенному:
    /// трогать состояние системы без решения пользователя нельзя.
    ///
    /// Фактическое состояние автозапуска всё равно читается из реестра при
    /// старте, так что расхождение файла и системы не приводит к включённому
    /// автозапуску против воли пользователя.
    /// </summary>
    private static void Migrate4To5(AppSettings settings)
    {
        settings.TrayEnabled = true;
        settings.RunAtStartup = false;

        settings.SchemaVersion = 5;
    }

    /// <summary>
    /// 5 → 6. У входного стрипа появился графический эквалайзер (SM-B05):
    /// включение, десять полос, общий makeup-gain и два среза.
    ///
    /// У старых файлов полей нет, и десериализация подставит значения свойств, а
    /// вот руками правленный файл мог оставить массив полос короче
    /// <see cref="InputChannelModel.EqBandCount"/>, длиннее или с NaN. Полосы
    /// приводим к рабочей длине: DSP и окно настроек читают их по индексу, и
    /// лишняя или недостающая запись — это либо мусор в обработке, либо полоса,
    /// которой не на чем рисоваться.
    ///
    /// Значение по умолчанию у полос — 0 дБ, а у срезов — «выключено» (крайние
    /// положения шкалы). Именно поэтому здесь <see cref="Sanitize(float, float, float, float)"/>
    /// с явной заменой: ноль герц для частоты среза недопустим, в отличие от
    /// нуля дБ у остальных параметров.
    /// </summary>
    private static void Migrate5To6(AppSettings settings)
    {
        foreach (var input in settings.Inputs)
        {
            input.EqBandGains = NormalizeEqBands(input.EqBandGains);

            float limit = InputChannelModel.EqBandGainLimitDb;
            input.EqPreampDb = Sanitize(input.EqPreampDb, 0f, -limit, limit);
            input.EqLowCutHz = Sanitize(input.EqLowCutHz,
                InputChannelModel.EffectDefaults.EqLowCutHz,
                InputChannelModel.EqLowCutMinHz, InputChannelModel.EqLowCutMaxHz);
            input.EqHighCutHz = Sanitize(input.EqHighCutHz,
                InputChannelModel.EffectDefaults.EqHighCutHz,
                InputChannelModel.EqHighCutMinHz, InputChannelModel.EqHighCutMaxHz);
        }

        settings.SchemaVersion = 6;
    }

    /// <summary>
    /// Полосы эквалайзера, приведённые к рабочей длине: недостающие добавляются
    /// нулевыми, лишние отбрасываются, значения вне диапазона и NaN заменяются
    /// нулём дБ.
    /// </summary>
    private static float[] NormalizeEqBands(float[]? source)
    {
        var bands = new float[InputChannelModel.EqBandCount];
        if (source is null) return bands;

        float limit = InputChannelModel.EqBandGainLimitDb;
        for (int i = 0; i < bands.Length; i++)
            bands[i] = i < source.Length ? Sanitize(source[i], -limit, limit) : 0f;

        return bands;
    }

    /// <summary>
    /// Приводит список назначений к ровно <see cref="InputChannelModel.FuncButtonSlotCount"/>
    /// записей: лишние отбрасываются, недостающие добираются пустыми, Id шин
    /// чистятся от пустых строк и дублей. Разметка стрипа обращается к первому
    /// и второму назначению напрямую, поэтому список короче неё быть не может.
    /// </summary>
    private static List<FuncButtonModel> NormalizeFuncButtons(List<FuncButtonModel>? source)
    {
        var normalized = new List<FuncButtonModel>(InputChannelModel.FuncButtonSlotCount);
        for (int i = 0; i < InputChannelModel.FuncButtonSlotCount; i++)
        {
            var func = source is not null && i < source.Count ? source[i] : null;
            normalized.Add(new FuncButtonModel
            {
                Label = func?.Label ?? "",
                Exclusive = func?.Exclusive ?? true,
                BusIds = (func?.BusIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            });
        }

        return normalized;
    }

    /// <summary>
    /// Значение вне диапазона или NaN (битый JSON, правка руками) заменяется
    /// безопасным значением: иначе один такой файл даёт NaN в аудиобуфере.
    /// </summary>
    private static float Sanitize(float value, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : 0f;

    /// <summary>
    /// То же, но с явной заменой вместо нуля: у частот среза ноль герц — не
    /// рабочее значение, а «выключено» — это крайнее положение шкалы.
    /// </summary>
    private static float Sanitize(float value, float fallback, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}