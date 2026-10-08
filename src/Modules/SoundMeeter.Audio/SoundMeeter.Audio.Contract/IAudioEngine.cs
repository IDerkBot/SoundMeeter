using SoundMeeter.Models;

namespace SoundMeeter.Services;

public interface IAudioEngine : IDisposable
{
    IReadOnlyList<InputChannelModel> Inputs { get; }
    IReadOnlyList<OutputBusModel> Buses { get; }
    bool IsRunning { get; }

    /// <summary>Настройки MIDI-микшера (устройство + привязки контроллеров).</summary>
    MidiSettings Midi { get; set; }

    /// <summary>Каталог доступных устройств (для назначения источника стрипам).</summary>
    IReadOnlyList<DeviceInfo> Catalog { get; }

    /// <summary>Список устройств/каналов изменился (после RefreshDevices/назначения источника).</summary>
    event Action? ChannelsChanged;

    /// <summary>Состояние движка изменилось (запущен/остановлен).</summary>
    event Action? StateChanged;

    /// <summary>Перечитывает каталог WASAPI-устройств и проверяет доступность стрипов.</summary>
    void RefreshDevices();

    void Start();
    void Stop();

    /// <summary>Включает/выключает маршрут вход → шина (применяется на лету).</summary>
    void SetRoute(string inputId, string busId, bool enabled);

    /// <summary>
    /// Индивидуальная посылка входа в шину, дБ (применяется на лету, без
    /// пересоздания аудиопотока). Диапазон ограничен движком.
    /// </summary>
    void SetRouteGain(string inputId, string busId, float gainDb);

    /// <summary>Solo для входного стрипа: при активном solo несоло-входы замолкают.</summary>
    void SetInputSolo(string inputId, bool value);

    /// <summary>Solo для выходной шины: при активном solo несоло-шины замолкают.</summary>
    void SetOutputSolo(string busId, bool value);

    /// <summary>Добавляет новый входной стрип (без назначенного источника).</summary>
    void AddInput();

    void RemoveInput(string inputId);

    /// <summary>Добавляет новый выходной стрип (без назначенного устройства).</summary>
    void AddBus();

    void RemoveBus(string busId);

    /// <summary>
    /// Меняет порядок входных стрипов (перетаскивание в UI). Сами стрипы и их
    /// привязки не трогаются: MIDI-биндинки и панель OBS ссылаются на Id канала,
    /// поэтому перестановка не должна ничего пересоздавать.
    /// </summary>
    void MoveInput(int fromIndex, int toIndex);

    /// <summary>Меняет порядок выходных стрипов (перетаскивание в UI).</summary>
    void MoveBus(int fromIndex, int toIndex);

    /// <summary>Назначает источнику входного стрипа устройство (null — отключить).</summary>
    void SetInputSource(string inputId, string? deviceId);

    /// <summary>
    /// Задаёт вручную, в какое устройство уходят приложения стрипа (null — вернуть
    /// автоматическое определение). Нужно там, где связку кабеля угадать нельзя.
    /// </summary>
    void SetInputAppTarget(string inputId, string? deviceId);

    /// <summary>Назначает выходной шине устройство воспроизведения (null — отключить).</summary>
    void SetBusSource(string busId, string? deviceId);

    /// <summary>Снимок текущих настроек для сохранения.</summary>
    AppSettings CreateSnapshot();

    /// <summary>Накладывает сохранённый пресет (заменяет списки стрипов).</summary>
    void ApplyPreset(AppSettings preset);

    /// <summary>Инициализация стартового состояния (наводит записи роутинга).</summary>
    void EnsureRoutingTable();

    /// <summary>
    /// Возвращает входной стрип, наполняемый кодом приложения, а не устройством,
    /// создав его при необходимости, и возвращает его Id.
    ///
    /// Существует ради модуля синтеза речи (SM-E01): голосу нужен собственный
    /// канал, где его можно отдельно приглушить, обработать и положить в микс.
    /// Такой стрип не занимает устройство, поэтому проверка «одно устройство —
    /// один стрип» к нему не относится и его можно создать сколько угодно.
    ///
    /// Повторный вызов с тем же именем возвращает тот же стрип — иначе каждый
    /// клик по «применить» в окне настроек плодил бы каналы.
    /// </summary>
    string EnsureGeneratedInput(string name);

    /// <summary>
    /// Подмешивает готовые сэмплы (48 кГц, стерео) в кольцо источника стрипа.
    /// Именно так синтезированная речь попадает в микшер (SM-E01).
    ///
    /// Темп выдачи задаёт источник, а не вызывающий: сэмплы кладутся в очередь и
    /// уходят в кольцо со скоростью воспроизведения, иначе кольцо переполнилось
    /// бы и в середине фразы были бы щелчки.
    ///
    /// false — стрипа нет или у него нет источника (например, движок остановлен
    /// либо снят весь маршрут); данные в этом случае возвращаются вызывающему
    /// жестом «озвучивать некуда».
    /// </summary>
    bool PushAudio(string inputId, ReadOnlySpan<float> samples);

    /// <summary>
    /// Сколько секунд подмешанного к стрипу звука ещё не прозвучало.
    ///
    /// Нужно модулю синтеза речи (SM-E01), чтобы знать, когда фраза действительно
    /// дозвучала: синтезатор отдаёт весь текст за доли секунды, поэтому момент
    /// возврата из синтеза о слушабе ничего не говорит.
    ///
    /// Всегда 0, если стрипа нет, движок остановлен или канал никуда не послан.
    /// </summary>
    double GetBufferedSeconds(string inputId);

    /// <summary>
    /// Выбросить из стрипа звук, который ещё не прозвучал. true, если что-то было
    /// выброшено.
    ///
    /// Кнопке «стоп» без этого нечего глушить: фраза уже лежит в буфере источника
    /// и доиграет до конца сама. Уже ушедшее в кольцо не трогается — это звук
    /// микшера, а не буфер модуля.
    /// </summary>
    bool ClearBufferedAudio(string inputId);
}

/// <summary>Устройство в каталоге. IsMicrophone=true — устройство захвата (микрофон);
/// false — устройство воспроизведения (для входов как loopback, для шин как вывод).</summary>
public sealed record DeviceInfo(string DeviceId, string Name, bool IsMicrophone)
{
    /// <summary>Устройство — половина виртуального кабеля (VB-Cable, VAC, StreamerCable).</summary>
    public bool IsVirtualCable { get; init; }

    /// <summary>Вторая половина того же кабеля: для входа кабеля — его выход, и наоборот.
    /// null — парная половинка не найдена (кабель установлен только с одной стороны).</summary>
    public string? CablePeerId { get; init; }
}
