using SoundMeeter.Models;

namespace SoundMeeter.Services.TextToSpeech;

/// <summary>
/// Как произносить фразу: голос, темп, громкость.
///
/// Отдельный тип вместо того, чтобы читать <see cref="TextToSpeechSettings"/>
/// прямо в синтезаторе: у настроек модуля есть ещё команды чата и очередь, а
/// синтезатору нужны ровно три числа и имя голоса. Заодно это делает синтезатор
/// проверяемым без подмены настроек целиком.
/// </summary>
public sealed record TtsSpeechOptions(string VoiceName, int Rate, int Volume)
{
    /// <summary>Громкость синтеза в шкале SAPI, 0…100.</summary>
    public int SapiVolume => Math.Clamp(Volume, TextToSpeechSettings.Limits.VolumeMin,
        TextToSpeechSettings.Limits.VolumeMax);

    /// <summary>
    /// Темп в шкале SAPI, −10…+10. У SAPI это целые «шаги» примерно по 12 %:
    /// значения не кратны шагу, движок округляет, поэтому и мы округляем —
    /// иначе ползунок в окне и то, что реально говорит синтезатор, разошлись бы.
    /// </summary>
    public int SapiRate => Math.Clamp(Rate, TextToSpeechSettings.Limits.RateMin,
        TextToSpeechSettings.Limits.RateMax);
}

/// <summary>
/// Синтез речи. За фразой отдаёт готовые сэмплы 48 кГц/стерео порциями, как
/// только они появляются, — потому что выдача в кольцо идёт в реальном времени
/// и ждать конца фразы значило бы добавить к озвучке её же длительность.
///
/// Контракт не включает выбор голоса из списка (это каталог) и не включает темп
/// воспроизведения (это <c>SyntheticInputSource</c>).
/// </summary>
public interface ITtsSpeechEngine : IDisposable
{
    /// <summary>Может ли синтезатор работать (есть ли SAPI в системе).</summary>
    bool IsAvailable { get; }

    /// <summary>Почему не работает (пусто — работает).</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// Синтезирует <paramref name="text"/> и вызывает <paramref name="onAudio"/>
    /// для каждой порции готовых сэмплов (48 кГц, стерео, L/R попарно).
    ///
    /// Вызывается из своего потока и синхронно: метод возвращается, когда фраза
    /// договорена или отменена. Частичный результат (фраза обрезалась отменой) —
    /// норма, ошибкой он не считается.
    /// </summary>
    void Speak(string text, TtsSpeechOptions options, Action<float[]> onAudio, CancellationToken cancellationToken);
}
