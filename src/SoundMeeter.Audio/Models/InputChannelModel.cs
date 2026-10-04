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

    #region Эффекты стрипа (SM-B05)

    // Поля эффектов читаются DSP на каждом пакете, поэтому любая правка слышна
    // сразу. Диапазоны заданы здесь же как константы в Audio/*Dsp: миграция
    // схемы приводит к ним значения из файла, иначе битый JSON (или правка
    // руками) дал бы в аудиобуфер NaN или усиление в сотни раз.

    /// <summary>
    /// Значения эффектов по умолчанию — то, к чему двойной щелчок по крутилке
    /// возвращает параметр. Вынесены отдельно от полей, чтобы «дефолт» нельзя
    /// было случайно переписать, и чтобы одно место отвечало и за инициализацию,
    /// и за сброс.
    /// </summary>
    public static class EffectDefaults
    {
        public const float CompressorThresholdDb = -18f;
        public const float CompressorRatio = 3f;
        public const float CompressorAttackMs = 10f;
        public const float CompressorReleaseMs = 150f;
        public const float CompressorMakeupDb = 0f;
        public const float FxGainDb = 0f;
        public const float DelayTimeMs = 250f;
        public const float DelayFeedback = 35f;
        public const float DelayDampingHz = 4000f;
        public const float DelayMix = 0f;
        public const float ReverbSize = 60f;
        public const float ReverbDamping = 40f;
        public const float ReverbMix = 0f;

        public const float EqPreampDb = 0f;

        /// <summary>Частота среза снизу по умолчанию: 20 Гц, то есть «не резать».
        /// См. <see cref="InputChannelModel.EqLowCutMinHz"/>.</summary>
        public const float EqLowCutHz = InputChannelModel.EqLowCutMinHz;

        /// <summary>Частота среза сверху по умолчанию: 20 кГц, то есть «не резать».</summary>
        public const float EqHighCutHz = InputChannelModel.EqHighCutMaxHz;
    }

    /// <summary>Компрессор включён.</summary>
    public bool CompressorEnabled { get; set; }

    /// <summary>Порог компрессора, дБ (−60..0).</summary>
    public float CompressorThresholdDb { get; set; } = EffectDefaults.CompressorThresholdDb;

    /// <summary>Коэффициент сжатия, :1 (1..20).</summary>
    public float CompressorRatio { get; set; } = EffectDefaults.CompressorRatio;

    /// <summary>Время атаки компрессора, мс (0.1..100).</summary>
    public float CompressorAttackMs { get; set; } = EffectDefaults.CompressorAttackMs;

    /// <summary>Время отпускания компрессора, мс (10..1000).</summary>
    public float CompressorReleaseMs { get; set; } = EffectDefaults.CompressorReleaseMs;

    /// <summary>Makeup-gain компрессора, дБ (−12..+24): компенсирует потерю
    /// уровня на сжатии.</summary>
    public float CompressorMakeupDb { get; set; }

    /// <summary>Trim после компрессора включён.</summary>
    public bool FxGainEnabled { get; set; }

    /// <summary>Уровень trim, дБ (−60..+24). Двусторонний, в отличие от входного
    /// Gain микрофона (0..+60): им выравнивают громкость перед задержкой и
    /// реверберацией, не трогая посылки на шины.</summary>
    public float FxGainDb { get; set; }

    /// <summary>Задержка включена.</summary>
    public bool DelayEnabled { get; set; }

    /// <summary>Время задержки, мс (1..2000).</summary>
    public float DelayTimeMs { get; set; } = EffectDefaults.DelayTimeMs;

    /// <summary>Обратная связь задержки, % повторов (0..90).</summary>
    public float DelayFeedback { get; set; } = EffectDefaults.DelayFeedback;

    /// <summary>Гашение верхов в петле повторов, Гц (200..18000).</summary>
    public float DelayDampingHz { get; set; } = EffectDefaults.DelayDampingHz;

    /// <summary>Доля мокрого сигнала задержки, % (0..100).</summary>
    public float DelayMix { get; set; }

    /// <summary>Реверберация включена.</summary>
    public bool ReverbEnabled { get; set; }

    /// <summary>Размер реверберации, % (0..100): время хвоста и плотность.</summary>
    public float ReverbSize { get; set; } = EffectDefaults.ReverbSize;

    /// <summary>Затухание верхов реверберации, % (0..100).</summary>
    public float ReverbDamping { get; set; } = EffectDefaults.ReverbDamping;

    /// <summary>Доля мокрого сигнала реверберации, % (0..100).</summary>
    public float ReverbMix { get; set; }

    #endregion

    #region Эквалайзер стрипа (SM-B05)

    /// <summary>
    /// Число полос графического эквалайзера. Полосы идут с шагом примерно в
    /// октаву (стандартные центры 31/62/125 Гц округлены до целых), поэтому
    /// число и центральные частоты — одно целое: полосы не равны, а соседние
    /// отличаются примерно вдвое, иначе соседние пики налезали бы друг на друга.
    /// </summary>
    public const int EqBandCount = 10;

    /// <summary>Предел усиления/ослабления полосы, дБ (±12).</summary>
    public const float EqBandGainLimitDb = 12f;

    /// <summary>Предел общего makeup-gain эквалайзера, дБ (±12).</summary>
    public const float EqPreampLimitDb = 12f;

    /// <summary>Нижняя граница ползунка среза снизу, Гц. Значение = «срез выключен».</summary>
    public const float EqLowCutMinHz = 20f;

    /// <summary>Верхняя граница ползунка среза снизу, Гц.</summary>
    public const float EqLowCutMaxHz = 300f;

    /// <summary>Нижняя граница ползунка среза сверху, Гц.</summary>
    public const float EqHighCutMinHz = 3000f;

    /// <summary>
    /// Верхняя граница ползунка среза сверху, Гц. Значение = «срез выключен».
    /// Ставить выше нельзя: при 48 кГц частота Найквиста 24 кГц, и ФНЧ на 20 кГц
    /// уже снимает верхний октав — «выключено» обязано быть насквозь прозрачным.
    /// </summary>
    public const float EqHighCutMaxHz = 20000f;

    private static readonly float[] EqFrequencies =
        [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    /// <summary>
    /// Центральные частоты полос, Гц. <see cref="ReadOnlySpan{T}"/> вместо
    /// массива, чтобы вызывающий не мог бы его переписать, а копия не
    /// выделялась на каждом кадре DSP.
    /// </summary>
    public static ReadOnlySpan<float> EqBandFrequencies => EqFrequencies;

    /// <summary>Эквалайзер включён.</summary>
    public bool EqEnabled { get; set; }

    /// <summary>
    /// Усиление полос, дБ, ровно <see cref="EqBandCount"/> штук. Массив, а не
    /// словарь: полосы адресуются по индексу и из DSP, и из разметки, а имена
    /// у них есть только в окне настроек.
    /// </summary>
    public float[] EqBandGains { get; set; } = new float[EqBandCount];

    /// <summary>Общий makeup-gain эквалайзера, дБ (−12..+12): компенсирует суммарный подъём полос.</summary>
    public float EqPreampDb { get; set; } = EffectDefaults.EqPreampDb;

    /// <summary>Срез снизу, Гц (20 = выключен).</summary>
    public float EqLowCutHz { get; set; } = EffectDefaults.EqLowCutHz;

    /// <summary>Срез сверху, Гц (20000 = выключен).</summary>
    public float EqHighCutHz { get; set; } = EffectDefaults.EqHighCutHz;

    /// <summary>
    /// Усиление полосы, дБ. Индекс вне диапазона и мусор в массиве (файл,
    /// правленный руками) читаются как 0 дБ: лучше ровная полоса, чем разрыв в
    /// обработке. Рабочий диапазон приводит <see cref="SettingsMigrator"/>.
    /// </summary>
    public float GetEqBand(int index) =>
        EqBandGains is { } bands && index >= 0 && index < bands.Length && float.IsFinite(bands[index])
            ? bands[index]
            : 0f;

    /// <summary>Записать усиление полосы. Индекс вне диапазона игнорируется.</summary>
    public void SetEqBand(int index, float gainDb)
    {
        if (EqBandGains is null || index < 0 || index >= EqBandGains.Length) return;
        EqBandGains[index] = gainDb;
    }

    #endregion

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