using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;
using SoundMeeter.Services.TextToSpeech;

namespace SoundMeeter.ViewModels;

// Модуль синтеза речи (SM-E01). Сам модуль живёт в TextToSpeechService и ничего
// не знает про интерфейс; здесь только мост между ним и главной VM: подписки,
// жизненный цикл и то, что окно настроек видит как «настройки TTS».
//
// Отдельный partial по той же причине, что MainViewModel.ObsDock и
// MainViewModel.Midi: файл главной VM и так уже держит семь тем, и ещё одна
// задача в его конструкторе ничего не добавит.
public partial class MainViewModel : ITextToSpeechHost
{
    private readonly TextToSpeechService _tts;
    private readonly ILogger _ttsLogger = AppLog.For<MainViewModel>();

    /// <summary>Модуль синтеза речи.</summary>
    public TextToSpeechService Tts => _tts;

    /// <summary>
    /// Полосы микшера для окна настроек. Реализация явная, потому что у главной VM
    /// <see cref="Inputs"/> — это <c>ObservableCollection</c>: класс, а интерфейс
    /// требует <c>IReadOnlyList</c>, и общего типа у них нет.
    /// </summary>
    IReadOnlyList<InputChannelViewModel> ITextToSpeechHost.Inputs => Inputs;

    /// <summary>Живые настройки модуля (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public TextToSpeechSettings TtsSettings => _settings.Settings.TextToSpeech;

    /// <summary>
    /// Применяет настройки модуля из окна.
    ///
    /// Отдельно от <see cref="ApplyDockSettings"/> потому, что здесь есть
    /// побочный эффект, которого нет у дока: модуль умеет создавать свой
    /// входной стрип, если пользователь выбрал «отдельный канал TTS». Создание
    /// идёт через движок и поднимает ChannelsChanged — то есть пересобирает
    /// полосы микшера, и делать это надо до сохранения, иначе на диск уедет снимок
    /// без только что созданного канала.
    /// </summary>
    public void ApplyTtsSettings(TextToSpeechSettings updated, bool saveNow = true)
    {
        if (!string.IsNullOrWhiteSpace(updated.TargetInputId) && updated.TargetInputId == GeneratedInputSentinel)
        {
            // «Отдельный канал» — это не выбор существующего стрипа, а просьба
            // создать свой. Движок вернёт Id; он же переживает перезапуск,
            // поэтому в настройках остаётся именно он, а не маркер.
            string stripId = _engine.EnsureGeneratedInput(TextToSpeechGeneratedStripName);
            updated.TargetInputId = stripId;

            var created = _engine.Inputs.FirstOrDefault(i => i.Id == stripId);
            updated.TargetInputDeviceId = created?.DeviceId ?? "";
        }

        _tts.Apply(updated, saveNow);
    }

    /// <summary>
    /// Поднять модуль после восстановления пресета. Порядок тот же, что у дока:
    /// стрипы уже на месте, поэтому «куда говорить» разрешается сразу.
    /// </summary>
    public void RestoreTts() => _tts.Restore();

    private void SubscribeTts() => _tts.StatusChanged += OnTtsStatusChanged;

    /// <summary>
    /// Статус модуля изменился. Окно настроек подписывается само; здесь только
    /// отметка в журнале на уровне отладки — иначе каждая реплика из чата
    /// засоряла бы общий журнал приложения.
    /// </summary>
    private void OnTtsStatusChanged() => _ttsLogger.LogDebug("TTS: {Status}", _tts.Status);

    /// <summary>
    /// Имя стрипа, который создаёт модуль. Одно на всё приложение: по нему
    /// движок находит уже созданный канал вместо того, чтобы плодить новые.
    /// </summary>
    public const string TextToSpeechGeneratedStripName = "TTS";

    /// <summary>
    /// Псевдо-Id для пункта «отдельный канал TTS» в списке выбора. Такой Id не
    /// может совпасть с настоящим: настоящие — <c>Guid.NewGuid().ToString("N")</c>,
    /// то есть 32 шестнадцатеричных символа.
    /// </summary>
    public const string GeneratedInputSentinel = "@tts";
}
