using SoundMeeter.Models;

namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Источник сообщений чата для модуля синтеза речи (SM-E01).
///
/// Это то место, где модуль получает реплики. Само подключение к Twitch здесь
/// намеренно отсутствует: модуль не должен зависеть от одного канала, а
/// <see cref="TextToSpeechService"/> работает с этим интерфейсом, а не с Twitch.
/// Появление коннектора — это ещё одна реализация плюс её регистрация в
/// контейнере; разбор команд, очередь, дроссель и синтез не меняются.
///
/// Реализация сейчас одна — <see cref="ManualTtsMessageSource"/>: сообщения
/// вводятся руками из окна настроек. Это не заглушка, а проверяемый вручную
/// способ убедиться, что команды и голоса работают, до того как появится
/// подключение к Twitch.
/// </summary>
public interface ITtsMessageSource : IDisposable
{
    /// <summary>Имя источника — попадает в журнал при старте и остановке.</summary>
    string Name { get; }

    /// <summary>Источник сейчас подключён и шлёт сообщения.</summary>
    bool IsRunning { get; }

    /// <summary>Новая реплика. Прилетает из потока источника, а не UI.</summary>
    event Action<TtsChatMessage>? MessageReceived;

    /// <summary>Подключиться. Настройки передаются источнику, а не разбираются им.</summary>
    void Start(TextToSpeechSettings settings);

    /// <summary>Отключиться. События после этого приходить не должны.</summary>
    void Stop();
}

/// <summary>
/// Источник, в который сообщения публикует сам пользователь — из окна настроек
/// модуля. Он же служит ручным стендом для разбора команд: напечатать
/// «<c>!ttsvoice Лилия</c>», затем «<c>!tts привет</c>» и услышать ответ можно
/// без подключения к чату.
/// </summary>
public sealed class ManualTtsMessageSource : ITtsMessageSource
{
    public string Name => "manual";

    public bool IsRunning { get; private set; }

#pragma warning disable CS0067   // событие поднимает внешний код (окно настроек)
    public event Action<TtsChatMessage>? MessageReceived;
#pragma warning restore CS0067

    public void Start(TextToSpeechSettings settings) => IsRunning = true;

    public void Stop() => IsRunning = false;

    /// <summary>
    /// Отправить реплику. false, если источник остановлен — вызывающий покажет
    /// это в статусной строке, а не будет гадать, почему модуль молчит.
    /// </summary>
    public bool Publish(string user, string text)
    {
        if (!IsRunning) return false;

        MessageReceived?.Invoke(new TtsChatMessage(user, text));
        return true;
    }

    public void Dispose() => Stop();
}
