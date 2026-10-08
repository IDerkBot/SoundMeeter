using SoundMeeter.Models;
using SoundMeeter.Services.TextToSpeech;

namespace SoundMeeter.Services.Twitch;

/// <summary>
/// Источник сообщений для модуля синтеза речи, питаемый чатом Twitch (SM-F01).
///
/// Это тонкий мост, и он тонкий намеренно. Модуль озвучки ничего не знает про
/// Twitch: он подписан на <see cref="MessageReceived"/> и получает реплики. Разбор
/// команд, список игнора, дроссель и очередь остаются в модуле и не меняются оттого,
/// что реплики пришли из сети, а не введены руками.
///
/// ЗАЧЕМ ЕЩЁ ОДИН КЛАСС, ЕСЛИ ЕСТЬ <see cref="ManualTtsMessageSource"/>. Ручной
/// источник остаётся нужным: он позволяет проверить «!tts» и «!ttsvoice» без
/// подключения к чату вообще. Этот — для случая, когда канал читается, а ввести
/// реплику руками негде (стрим уже идёт).
///
/// ЧТО ЗДЕСЬ ВАЖНО. Реплики прилетают на потоке чтения сокета, а модуль бьёт по
/// SAPI синхронно. Поэтому обработчик обязан вернуться немедленно и только передать
/// реплику — всё остальное модуль делает сам, в своём потоке. Иначе чтение чата
/// встало бы на время синтеза фразы.
/// </summary>
public sealed class TwitchTtsMessageSource : ITtsMessageSource, IDisposable
{
    private readonly TwitchService _twitch;

    public TwitchTtsMessageSource(TwitchService twitch)
    {
        _twitch = twitch ?? throw new ArgumentNullException(nameof(twitch));

        // Подписка здесь, а не в Start: модуль озвучки подписывается на источник в
        // своём конструкторе, и если бы источник ждал Start, реплики в первую секунду
        // после включения модуля прошли бы мимо.
        _twitch.MessageReceived += OnMessage;
    }

    public string Name => "twitch";

    /// <summary>
    /// Источник считается работающим, когда подключён к чату.
    ///
    /// Не «когда модуль вообще что-то умеет»: без соединения реплик не будет, и
    /// показывать «источник работает» значило бы вводить в заблуждение при разборе
    /// причин, почему модуль молчит.
    /// </summary>
    public bool IsRunning => _twitch.IsChatConnected;

    public event Action<TtsChatMessage>? MessageReceived;

    public void Start(TextToSpeechSettings settings) => _twitch.ApplySettings();

    /// <summary>
    /// Останавливать чат здесь нельзя: статус трансляции нужен и при выключенном
    /// модуле озвучки, а подключение к чату — это настройка интеграции, а не
    /// модуля. Отключает его пользователь, сняв галочку в окне.
    /// </summary>
    public void Stop() { }

    private void OnMessage(TtsChatMessage message) => MessageReceived?.Invoke(message);

    public void Dispose()
    {
        _twitch.MessageReceived -= OnMessage;
        MessageReceived = null;
    }
}