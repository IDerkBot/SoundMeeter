using Microsoft.Extensions.Logging;
using SoundMeeter.Audio.Contract;
using SoundMeeter.Core.Contract;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.TextToSpeech.Entities;
using SoundMeeter.TextToSpeech.Contract;

namespace SoundMeeter.TextToSpeech.UseCases;

/// <summary>
/// VM модуля синтеза речи (SM-E01): мост между <see cref="TextToSpeechService"/>
/// и интерфейсом. Сам модуль ничего не знает про главную VM; здесь только
/// подписки, жизненный цикл и то, что окно настроек видит как «настройки TTS».
/// </summary>
public sealed class TtsViewModel : ITextToSpeechHost
{
    private readonly TextToSpeechService _tts;
    private readonly IAudioEngine _engine;
    private readonly IStripProvider _strips;
    private readonly ILogger _logger = AppLog.For<TtsViewModel>();

    /// <summary>Модуль синтеза речи (контракт для окна настроек).</summary>
    public ITtsModule Tts => _tts;

    /// <summary>Модуль синтеза речи целиком — тем, кому нужна его жизненная часть.</summary>
    public TextToSpeechService Service => _tts;

    public TtsViewModel(
        TextToSpeechService tts,
        IAudioEngine engine,
        IStripProvider strips)
    {
        _tts = tts;
        _engine = engine;
        _strips = strips;

        _tts.StatusChanged += OnTtsStatusChanged;
    }

    /// <summary>Входные стрипы микшера — из них строится список целей.</summary>
    public IReadOnlyList<IInputStrip> Inputs => _strips.Inputs;

    /// <summary>Живые настройки модуля (лежат в SettingsService, оттуда уходят в settings.json).</summary>
    public TextToSpeechSettings TtsSettings => _tts.Settings;

    /// <summary>
    /// Применяет настройки модуля из окна.
    ///
    /// Отдельно от применения настроек дока потому, что здесь есть побочный
    /// эффект, которого нет у дока: модуль умеет создавать свой входной стрип,
    /// если пользователь выбрал «отдельный канал TTS». Создание идёт через движок
    /// и поднимает ChannelsChanged — то есть пересобирает полосы микшера, и
    /// делать это надо до сохранения, иначе на диск уедет снимок без только что
    /// созданного канала.
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

    /// <summary>
    /// Статус модуля изменился. Окно настроек подписывается само; здесь только
    /// отметка в журнале на уровне отладки — иначе каждая реплика из чата
    /// засоряла бы общий журнал приложения.
    /// </summary>
    private void OnTtsStatusChanged() => _logger.LogDebug("TTS: {Status}", _tts.Status);

    /// <summary>
    /// Имя стрипа, который создаёт модуль. Само значение живёт в
    /// <see cref="TtsStripNames"/> — там, где от него могут взять и другие слои,
    /// а не только представление.
    /// </summary>
    public const string TextToSpeechGeneratedStripName = TtsStripNames.Generated;

    /// <summary>
    /// Псевдо-Id для пункта «отдельный канал TTS» в списке выбора. Такой Id не
    /// может совпасть с настоящим: настоящие — <c>Guid.NewGuid().ToString("N")</c>,
    /// то есть 32 шестнадцатеричных символа.
    /// </summary>
    public const string GeneratedInputSentinel = TtsStripNames.GeneratedSentinel;

    /// <summary>Освобождение подписки на статус модуля.</summary>
    public void Dispose() => _tts.StatusChanged -= OnTtsStatusChanged;
}
