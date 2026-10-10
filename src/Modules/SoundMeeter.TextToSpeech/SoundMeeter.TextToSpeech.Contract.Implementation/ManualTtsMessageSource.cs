using SoundMeeter.TextToSpeech.Entities;

namespace SoundMeeter.TextToSpeech.Contract.Implementation;

/// <summary>
/// Источник, в который сообщения публикует сам пользователь — из окна настроек
/// модуля. Он же служит ручным стендом для разбора команд: напечатать
/// «<c>!ttsvoice Лилия</c>», затем «<c>!tts привет</c>» и услышать ответ можно
/// без подключения к чату.
///
/// Лёгкий и без внешних зависимостей, но всё же реализация контракта, а не он
/// сам: интерфейс лежит в Contract, чтобы модуль Twitch мог реализовать свой
/// источник, не ссылаясь на синтез речи.
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
