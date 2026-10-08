using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using SoundMeeter.Services.TextToSpeech;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Источник сообщений, объединяющий ручной ввод и чат Twitch (SM-F01).
///
/// Зачем объединение, а не выбор одного. Модулю всё равно, откуда пришла реплика:
/// разбор команд, дроссель и очередь устроены одинаково. А пользователю нужны оба
/// источника сразу, и это не удобство, а необходимость:
/// <list type="bullet">
/// <item>ручной ввод нужен, чтобы проверить «<c>!tts</c>» и «<c>!ttsvoice</c>» без
///       подключения к Twitch вообще — на новой установке, до регистрации;</item>
/// <item>чат нужен для работы на стриме, где вводить реплики руками нечем.</item>
/// </list>
/// Если бы регистрация выбирала один источник, ручной ввод пришлось бы выбирать
/// вручную, и на эфире легко осталось бы без озвучки.
///
/// Отдельный класс вместо наследования от одного из источников: оба держат своё
/// состояние (у ручного — включён ли, у Twitch — подключение), и склеить их
/// наследованием значило бы оставить одно из двух состояний неиспользуемым.
/// </summary>
public sealed class CompositeTtsMessageSource : ITtsMessageSource
{
    private readonly IReadOnlyList<ITtsMessageSource> _sources;
    private readonly ILogger _logger = AppLog.For<CompositeTtsMessageSource>();

    /// <summary>Логическое имя источника для журнала.</summary>
    public string Name => "chat+manual";

    public CompositeTtsMessageSource(params ITtsMessageSource[] sources) =>
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));

    /// <summary>
    /// Источник работает, если работает хотя бы один из составляющих.
    ///
    /// Иначе модуль показывал бы «источник не работает» при включённом чате и
    /// отключённом ручном вводе, и наоборот.
    /// </summary>
    public bool IsRunning => _sources.Any(source => source.IsRunning);

    public event Action<TtsChatMessage>? MessageReceived;

    public void Start(TextToSpeechSettings settings)
    {
        foreach (var source in _sources)
        {
            // Ошибка одного источника не должна лишать остальные работы: чат может
            // подняться, даже если ручной ввод почему-то не смог.
            try
            {
                source.Start(settings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Источник «{Source}» не поднялся: {Message}", source.Name, ex.Message);
            }
        }
    }

    public void Stop()
    {
        foreach (var source in _sources)
        {
            try
            {
                source.Stop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Источник «{Source}» не остановился: {Message}", source.Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// Подписаться на реплики всех источников.
    ///
    /// Подписка снимается при освобождении, а не в Stop: подписка принадлежит
    /// модулю, а не состоянию подключения, иначе реплики перестали бы приходить
    /// после первого же выключения и включения модуля.
    /// </summary>
    public void Attach()
    {
        foreach (var source in _sources) source.MessageReceived += OnMessage;
    }

    /// <summary>Отписаться от всех источников.</summary>
    public void Detach()
    {
        foreach (var source in _sources) source.MessageReceived -= OnMessage;
    }

    private void OnMessage(TtsChatMessage message) => MessageReceived?.Invoke(message);

    public void Dispose()
    {
        Detach();

        foreach (var source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Источник «{Source}» не освободился: {Message}", source.Name, ex.Message);
            }
        }
    }
}