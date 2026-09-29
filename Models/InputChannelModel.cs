namespace SoundMeeter.Models;

/// <summary>
/// Входной канал (стрип): физический микрофон либо loopback-захват
/// устройства воспроизведения (системный звук, VB-Cable и т.п.).
/// </summary>
public class InputChannelModel
{
    /// <summary>Сколько кнопок FUNC на стрипе (их число зашито в разметку).</summary>
    public const int FuncButtonSlotCount = 2;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>
    /// Пользовательское имя канала (редактируется по правому клику сверху стрипа).
    /// Пустое — отображается имя устройства (Name).
    /// </summary>
    public string ChannelName { get; set; } = "";
    public bool IsMicrophone { get; set; }
    public string DeviceId { get; set; } = "";
    public float VolumeDb { get; set; }

    /// <summary>
    /// Входное усиление, дБ (0 = без усиления, только подъём). Применяется один раз
    /// до разветвления на шины, поэтому не зависит от числа посылок. Нужен микрофонам:
    /// их сигнал на десятки дБ тише линейного, и без подъёма канал не вытянуть.
    /// </summary>
    public float GainDb { get; set; }
    public bool IsMuted { get; set; }
    public bool IsMono { get; set; }
    public bool IsSolo { get; set; }
    public bool IsAvailable { get; set; }
    public float PeakLevel { get; set; }

    /// <summary>
    /// Render-устройство, в которое уходят приложения этого стрипа, если связку
    /// кабеля угадать не удалось (имя задаёт пользователь, а Windows признака пары
    /// не отдаёт). Пусто — цель выводится автоматически: связанный выход кабеля
    /// либо собственное устройство loopback-стрипа.
    /// </summary>
    public string AppTargetDeviceId { get; set; } = "";

    /// <summary>
    /// Денойзер (RNNoise, CPU). Включение = "Noise Remover".
    /// </summary>
    public bool DenoiserEnabled { get; set; }

    /// <summary>Noise Remover, 0..100% — доля денойзерного сигнала в миксе с исходным.</summary>
    public float DenoiserNoiseRemover { get; set; } = 100f;

    /// <summary>Dry / Wet Balance, 0..100% — финальный кросфейд между исходным и обработанным сигналом.</summary>
    public float DenoiserDryWet { get; set; } = 100f;

    /// <summary>Formant Low, дБ (−24..+24) — EQ-пик ~500 Гц.</summary>
    public float DenoiserFormantLowDb { get; set; }

    /// <summary>Formant Medium, дБ (−24..+24) — EQ-пик ~1.5 кГц.</summary>
    public float DenoiserFormantMidDb { get; set; }

    /// <summary>Formant High, дБ (−24..+24) — EQ-пик ~3.5 кГц.</summary>
    public float DenoiserFormantHighDb { get; set; }

    /// <summary>Formant Group, дБ (−12..+12) — общий makeup-gain EQ-секции.</summary>
    public float DenoiserFormantGroupDb { get; set; }

    /// <summary>
    /// Матрица роутинга: ключ = DeviceId выходной шины, значение = настройка (вкл + гейн).
    /// </summary>
    public Dictionary<string, BusRouting> BusRouting { get; set; } = new();

    /// <summary>
    /// Назначения двух кнопок FUNC (см. <see cref="FuncButtonModel"/>): список
    /// всегда длиной <see cref="FuncButtonSlotCount"/>, лишние записи из файла
    /// настроек отбрасываются, недостающие — добираются пустыми (миграция
    /// 1 → 2 в SettingsMigrator).
    /// </summary>
    public List<FuncButtonModel> FuncButtons { get; set; } = new();

    /// <summary>Номер включённой кнопки FUNC (индекс в <see cref="FuncButtons"/>).
    /// <see cref="NoFuncEngaged"/> — ни одна не нажата. Активна может быть только
    /// одна: обе кнопки описывают один и тот же роутинг стрипа, поэтому «нажаты
    /// обе» — невозможное состояние, а не просто неудобное.</summary>
    public int EngagedFunc { get; set; } = NoFuncEngaged;

    /// <summary>Значение <see cref="EngagedFunc"/> для стрипа, у которого ни одна
    /// кнопка не нажата.</summary>
    public const int NoFuncEngaged = -1;

    /// <summary>
    /// Роутинг стрипа до включения кнопки FUNC: снимок флагов Enabled по Id шин.
    /// Без него снятие кнопки некуда возвращать — «вернуть как было» не из чего.
    ///
    /// Хранится в пресете, а не только в памяти: иначе после перезапуска
    /// приложения нажатая кнопка осталась бы включённой, а вернуть прежний
    /// роутинг было бы уже нечем.
    /// </summary>
    public Dictionary<string, bool> FuncBaseRouting { get; set; } = new();
}