using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.TextToSpeech;
using System.Collections.ObjectModel;
using Limits = SoundMeeter.Models.TextToSpeechSettings.Limits;

namespace SoundMeeter.ViewModels;

/// <summary>Входной стрип в списке выбора цели для голоса.</summary>
public sealed class TtsTargetItemViewModel
{
    /// <summary>
    /// Пункт «отдельный канал TTS». Его Id — маркер, а не настоящий Id стрипа;
    /// настоящий появляется после создания (см. MainViewModel.ApplyTtsSettings).
    /// </summary>
    public const string GeneratedId = MainViewModel.GeneratedInputSentinel;

    public TtsTargetItemViewModel(string id, string title, string deviceId, bool isAvailable, bool isGenerated)
    {
        Id = id;
        Title = title;
        DeviceId = deviceId;
        IsAvailable = isAvailable;
        IsGenerated = isGenerated;
    }

    /// <summary>Id стрипа либо <see cref="GeneratedId"/>.</summary>
    public string Id { get; }

    /// <summary>Имя канала: своё, если задано, иначе имя устройства.</summary>
    public string Title { get; }

    /// <summary>DeviceId устройства стрипа (пусто у генерируемого).</summary>
    public string DeviceId { get; }

    /// <summary>Устройство на месте.</summary>
    public bool IsAvailable { get; }

    /// <summary>Канал без устройства, наполняемый модулем синтеза.</summary>
    public bool IsGenerated { get; }
}

/// <summary>Голос, выбранный пользователю чата: имя, код и «используется сейчас».</summary>
public sealed partial class TtsUserVoiceItemViewModel : ObservableObject
{
    public TtsUserVoiceItemViewModel(TtsUserVoice model)
    {
        Model = model;
    }

    public TtsUserVoice Model { get; }

    /// <summary>Логин без «@».</summary>
    public string User => Model.User;

    /// <summary>Имя голоса SAPI.</summary>
    public string Voice => Model.Voice;

    /// <summary>Подпись строки списка.</summary>
    public string DisplayName => $"{Model.User} → {Model.Voice}";

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    /// <summary>Удаление обрабатывает окно: список — его, а модель живёт в настройках.</summary>
    public event Action<TtsUserVoiceItemViewModel>? RemoveRequested;
}

/// <summary>
/// Окно настроек модуля синтеза речи (SM-E01): включение, целевой канал, голос
/// по умолчанию, голоса отдельных пользователей и пределы чата.
///
/// Отдельно от остального окна — две вещи, которые не помещаются в попап стрипа:
/// список голосов пользователей растёт вместе с модераторами, а проверка разбора
/// команд на живой реплике — единственный способ убедиться, что всё настроено
/// верно, не дожидаясь подключения чата.
/// </summary>
public sealed partial class TextToSpeechSettingsViewModel : LocalizedViewModel
{
    private readonly ITextToSpeechHost _host;
    private readonly TextToSpeechService _tts;

    public TextToSpeechSettingsViewModel(ITextToSpeechHost host)
    {
        _host = host;
        _tts = host.Tts;

        var settings = host.TtsSettings;
        Enabled = settings.Enabled;
        Rate = settings.Rate;
        Volume = settings.Volume;
        SpeakCommandText = settings.SpeakCommand;
        VoiceCommandText = settings.VoiceCommand;
        AllowVoiceChange = settings.AllowVoiceChange;
        MaxQueueLength = settings.MaxQueueLength;
        MaxMessagesPerMinute = settings.MaxMessagesPerMinute;
        MaxMessageChars = settings.MaxMessageChars;

        foreach (var pair in settings.UserVoices)
            AddUserVoiceItem(new TtsUserVoiceItemViewModel(pair.Clone()));

        BuildVoiceList();
        BuildTargetList(settings);

        _tts.StatusChanged += RefreshStatus;
        RefreshStatus();
    }

    /// <summary>Приложение, из которого окно берёт модуль, настройки и полосы.</summary>
    public ITextToSpeechHost Host => _host;

    #region Включение и канал

    /// <summary>Модуль включён.</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Куда отдавать голос: существующий стрип или отдельный канал TTS.</summary>
    private TtsTargetItemViewModel? _selectedTarget;

    /// <summary>Выбранный целевой канал.</summary>
    public TtsTargetItemViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value)) OnPropertyChanged(nameof(TargetHint));
        }
    }

    /// <summary>Куда уходит голос — одной строкой для подсказки под списком.</summary>
    public string TargetHint => SelectedTarget is null
        ? Loc.Get("Sm.Tts.NoTarget")
        : SelectedTarget.IsGenerated
            ? Loc.Get("Sm.Tts.TargetHintGenerated", MainViewModel.TextToSpeechGeneratedStripName)
            : Loc.Get("Sm.Tts.TargetHintStrip", SelectedTarget.Title);

    /// <summary>Входные стрипы, доступные цели, плюс пункт «отдельный канал».</summary>
    public ObservableCollection<TtsTargetItemViewModel> Targets { get; } = new();

    #endregion

    #region Голос и параметры речи

    /// <summary>Голос по умолчанию для всех, у кого нет своего.</summary>
    private TtsVoice? _selectedVoice;

    /// <summary>Выбранный голос по умолчанию.</summary>
    public TtsVoice? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (SetProperty(ref _selectedVoice, value)) OnPropertyChanged(nameof(VoiceHint));
        }
    }

    /// <summary>Голоса, установленные в системе.</summary>
    public ObservableCollection<TtsVoice> Voices { get; } = new();

    /// <summary>Темп речи, −10…+10.</summary>
    [ObservableProperty]
    private int _rate;

    /// <summary>Громкость синтеза, 0…100.</summary>
    [ObservableProperty]
    private int _volume;

    #endregion

    #region Команды чата и пределы

    /// <summary>Команда озвучки, текстом — чтобы не мешать вводу.</summary>
    [ObservableProperty]
    private string _speakCommandText;

    /// <summary>Команда выбора голоса.</summary>
    [ObservableProperty]
    private string _voiceCommandText;

    /// <summary>Разрешить смену голоса командой из чата.</summary>
    [ObservableProperty]
    private bool _allowVoiceChange;

    /// <summary>Предел очереди озвучки.</summary>
    [ObservableProperty]
    private int _maxQueueLength;

    /// <summary>Предел реплик в минуту от одного пользователя.</summary>
    [ObservableProperty]
    private int _maxMessagesPerMinute;

    /// <summary>Предел длины реплики.</summary>
    [ObservableProperty]
    private int _maxMessageChars;

    /// <summary>Подсказка под списком голосов.</summary>
    public string VoiceHint => Voices.Count == 0
        ? Loc.Get("Sm.Tts.NoVoicesHint")
        : Voices.Any(voice => voice.IsRussian)
            ? Loc.Get("Sm.Tts.RussianVoiceFound", Voices.Count(voice => voice.IsRussian), Voices.Count)
            : Loc.Get("Sm.Tts.NoRussianVoice", Voices.Count);

    #endregion

    #region Голоса пользователей

    /// <summary>Кто и каким голосом говорит — то, что набирается командами !ttsvoice.</summary>
    public ObservableCollection<TtsUserVoiceItemViewModel> UserVoices { get; } = new();

    /// <summary>Логин, которому назначают голос.</summary>
    [ObservableProperty]
    private string _newUserText = "";

    /// <summary>Голос для нового пользователя.</summary>
    private TtsVoice? _newUserVoice;

    /// <summary>Выбранный голос в поле назначения.</summary>
    public TtsVoice? NewUserVoice
    {
        get => _newUserVoice;
        set => SetProperty(ref _newUserVoice, value);
    }

    /// <summary>Пользователи, чьи сообщения модуль не читает никогда.</summary>
    [ObservableProperty]
    private string _ignoredUsersText = "";

    /// <summary>Результат последнего действия (зелёным/красным — по IsStatusOk).</summary>
    [ObservableProperty]
    private string _feedback = "";

    [ObservableProperty]
    private bool _isStatusOk;

    /// <summary>Строка состояния модуля (очередь, текущая фраза, ошибки).</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>Можно ли говорить прямо сейчас (модуль включён и канал есть).</summary>
    private bool _canSpeakNow;

    /// <summary>Разрешена ли проверка на живой реплике.</summary>
    public bool CanSpeakNow => _canSpeakNow;

    /// <summary>Логин для проверки разбора команд.</summary>
    [ObservableProperty]
    private string _testUserText = "tester";

    /// <summary>Реплика для проверки разбора команд.</summary>
    [ObservableProperty]
    private string _testTextText = "";

    #endregion

    #region Команды

    [RelayCommand]
    private void AddUserVoice()
    {
        string login = TtsChatCommandParser.NormalizeUser(NewUserText);
        if (login.Length == 0 || NewUserVoice is null)
        {
            SetFeedback(Loc.Get("Sm.Tts.NeedUserAndVoice"), false);
            return;
        }

        // Один логин — один голос: иначе в списке появлялись бы дубли, а из чата
        // пришёл бы только тот, что записан последним.
        foreach (var existing in UserVoices.Where(item =>
                     string.Equals(item.User, login, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            existing.RemoveRequested -= OnRemoveUserVoice;
            UserVoices.Remove(existing);
        }

        AddUserVoiceItem(new TtsUserVoiceItemViewModel(new TtsUserVoice(login, NewUserVoice.Name)));

        NewUserText = "";
        SetFeedback(Loc.Get("Sm.Tts.UserVoiceAdded", login, NewUserVoice.Name), true);
    }

    private void AddUserVoiceItem(TtsUserVoiceItemViewModel item)
    {
        item.RemoveRequested += OnRemoveUserVoice;
        UserVoices.Add(item);
    }

    private void OnRemoveUserVoice(TtsUserVoiceItemViewModel item)
    {
        item.RemoveRequested -= OnRemoveUserVoice;
        UserVoices.Remove(item);
        SetFeedback(Loc.Get("Sm.Tts.UserVoiceRemoved", item.User), true);
    }

    [RelayCommand]
    private void Apply()
    {
        var updated = BuildSettings();
        _host.ApplyTtsSettings(updated);
        RefreshStatus();

        SetFeedback(_tts.Status.Length > 0 && !_tts.IsStatusOk
            ? _tts.Status
            : Loc.Get("Sm.Tts.Saved"), _tts.IsStatusOk);
    }

    [RelayCommand]
    private void StopSpeaking() => _tts.StopSpeaking();

    [RelayCommand]
    private void ClearQueue() => _tts.ClearQueue();

    /// <summary>
    /// Прогоняет реплику через настоящий разбор команд — так же, как это сделает
    /// подключение к чату. Голос при этом берётся тот, что назначен этому
    /// пользователю, поэтому проверка заодно показывает и его.
    /// </summary>
    [RelayCommand]
    private void TestMessage()
    {
        string text = TestTextText;
        if (text.Trim().Length == 0)
        {
            SetFeedback(Loc.Get("Sm.Tts.NeedTestText"), false);
            return;
        }

        // Настройки применяются перед проверкой: иначе голос и команды в тесте
        // были бы старыми, а пользователь счёл бы модуль сломанным.
        _host.ApplyTtsSettings(BuildSettings(), saveNow: false);

        var result = _tts.Submit(TestUserText, text);
        RefreshStatus();

        SetFeedback(
            result.IsAccepted ? Loc.Get("Sm.Tts.Queued", _tts.QueueLength) : DescribeRejection(result),
            result.IsAccepted);
    }

    /// <summary>
    /// Почему реплика не озвучена. Собственный статус модуля здесь не годится:
    /// после неудачи он может остаться прежним («работает, ждёт сообщений»), и
    /// пользователь увидел бы надпись, противоречащую тому, что он сделал.
    /// </summary>
    private string DescribeRejection(TtsChatCommand command) => command.Reason switch
    {
        TtsChatReject.NotACommand => Loc.Get("Sm.Tts.NotACommand"),
        TtsChatReject.EmptyText => Loc.Get("Sm.Tts.Status.EmptyText", command.User),
        TtsChatReject.UnknownVoice => Loc.Get("Sm.Tts.Status.UnknownVoice", command.Text),
        TtsChatReject.VoiceChangeDisabled => Loc.Get("Sm.Tts.Status.VoiceChangeOff"),
        TtsChatReject.RateLimited => Loc.Get("Sm.Tts.Status.RateLimited", command.User,
            _host.TtsSettings.MaxMessagesPerMinute),
        TtsChatReject.IgnoredUser => Loc.Get("Sm.Tts.Status.Ignored", command.User),
        TtsChatReject.EngineUnavailable => Loc.Get("Sm.Tts.NoVoices"),
        _ => _tts.Status.Length > 0 ? _tts.Status : Loc.Get("Sm.Tts.NotACommand"),
    };

    #endregion

    #region Сборка настроек

    /// <summary>
    /// Собирает настройки из полей окна. Значения приводятся к рабочим пределам
    /// здесь же: в файле настроек не должно быть значений, которые потом пришлось
    /// бы чинить миграцией, — а крутилка в интерфейсе и ввод с клавиатуры расходятся
    /// (кнопки прокрутки у ползунка перескакивают через 10).
    /// </summary>
    private TextToSpeechSettings BuildSettings()
    {
        var settings = _host.TtsSettings;

        settings.Enabled = Enabled;
        settings.TargetInputId = SelectedTarget?.Id ?? "";
        settings.TargetInputDeviceId = SelectedTarget?.DeviceId ?? "";
        settings.DefaultVoice = SelectedVoice?.Name ?? "";
        settings.Rate = Math.Clamp(Rate, Limits.RateMin, Limits.RateMax);
        settings.Volume = Math.Clamp(Volume, Limits.VolumeMin, Limits.VolumeMax);
        settings.SpeakCommand = SpeakCommandText.Trim();
        settings.VoiceCommand = VoiceCommandText.Trim();
        settings.AllowVoiceChange = AllowVoiceChange;
        settings.MaxQueueLength = Math.Clamp(MaxQueueLength, Limits.QueueMin, Limits.QueueMax);
        settings.MaxMessagesPerMinute = Math.Clamp(MaxMessagesPerMinute,
            Limits.MessagesPerMinuteMin, Limits.MessagesPerMinuteMax);
        settings.MaxMessageChars = Math.Clamp(MaxMessageChars, Limits.MessageCharsMin, Limits.MessageCharsMax);
        settings.UserVoices = UserVoices.Select(item => item.Model.Clone()).ToList();
        settings.IgnoredUsers = IgnoredUsersText
            .Split([',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TtsChatCommandParser.NormalizeUser)
            .Where(login => login.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return settings;
    }

    private void BuildVoiceList()
    {
        Voices.Clear();
        foreach (var voice in _tts.Voices) Voices.Add(voice);

        SelectedVoice = Voices.FirstOrDefault(voice =>
                       string.Equals(voice.Name, _host.TtsSettings.DefaultVoice, StringComparison.OrdinalIgnoreCase))
                   ?? Voices.FirstOrDefault(voice => voice.IsRussian)
                   ?? Voices.FirstOrDefault();

        NewUserVoice = SelectedVoice ?? Voices.FirstOrDefault();
    }

    private void BuildTargetList(TextToSpeechSettings settings)
    {
        foreach (var vm in _host.Inputs)
        {
            Targets.Add(new TtsTargetItemViewModel(vm.Id, vm.Title, vm.Model.DeviceId,
                vm.Model.IsAvailable || vm.Model.IsGenerated, vm.Model.IsGenerated));
        }

        Targets.Add(new TtsTargetItemViewModel(TtsTargetItemViewModel.GeneratedId,
            Loc.Get("Sm.Tts.GeneratedTarget"), "", true, true));

        // Сохранённый выбор — по Id: он переживает перезапуск приложения. Если
        // канала с таким Id больше нет (импорт настроек с другой машины), берём
        // тот, у которого DeviceId совпал, иначе — любой сгенерированный, чтобы
        // поле не осталось пустым молча.
        string? deviceId = settings.TargetInputDeviceId;
        SelectedTarget = Targets.FirstOrDefault(t => string.Equals(t.Id, settings.TargetInputId, StringComparison.Ordinal))
                         ?? Targets.FirstOrDefault(t => !string.IsNullOrEmpty(deviceId) &&
                                                       string.Equals(t.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                         ?? Targets.FirstOrDefault(t => t.Id == TtsTargetItemViewModel.GeneratedId)
                         ?? Targets.FirstOrDefault();
    }

    #endregion

    #region Статус

    private void RefreshStatus()
    {
        _canSpeakNow = _tts.CanSpeakNow() && Enabled && SelectedTarget is not null;
        Status = _tts.Status;
        IsStatusOk = _tts.IsStatusOk;
        OnPropertyChanged(nameof(CanSpeakNow));
        OnPropertyChanged(nameof(VoiceHint));
        OnPropertyChanged(nameof(TargetHint));
    }

    private void SetFeedback(string text, bool ok)
    {
        Feedback = text;
        IsStatusOk = ok;
    }

    protected override void OnLanguageChangedCore()
    {
        // Название пункта «отдельный канал» и подсказки считаются в VM, поэтому
        // после смены языка их надо пересчитать: список сам не обновится.
        var generated = Targets.FirstOrDefault(t => t.Id == TtsTargetItemViewModel.GeneratedId);
        if (generated is not null)
        {
            var refreshed = new TtsTargetItemViewModel(generated.Id, Loc.Get("Sm.Tts.GeneratedTarget"),
                generated.DeviceId, generated.IsAvailable, true);
            int index = Targets.IndexOf(generated);
            Targets[index] = refreshed;
            if (SelectedTarget?.Id == generated.Id) SelectedTarget = refreshed;
        }

        OnPropertyChanged(nameof(VoiceHint));
        OnPropertyChanged(nameof(TargetHint));
        RefreshStatus();
    }

    protected override void DisposeCore() => _tts.StatusChanged -= RefreshStatus;

    #endregion
}
