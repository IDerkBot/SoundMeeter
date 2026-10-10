namespace SoundMeeter.Services.TextToSpeech;
using SoundMeeter.TextToSpeech.Entities;

/// <summary>
/// Модуль синтеза речи глазами окна настроек: всё, чем окно может управлять и
/// что оно показывает.
///
/// Отдельный контракт вместо <c>TextToSpeechService</c> — по той же причине,
/// что у <c>ITtsSpeechEngine</c> и <c>ITtsVoiceCatalog</c>: окно не должно
/// знать, что модуль устроен очередью на своём потоке и дросселем по
/// пользователям. Ему нужны голоса, отправка реплики и строка состояния.
///
/// Намеренно узкий: сюда нет ни <c>Restore</c>, ни <c>Apply</c> — это жизненный
/// цикл модуля, а не работа с ним из окна.
/// </summary>
public interface ITtsModule : IDisposable
{
    /// <summary>Список доступных голосов.</summary>
    IReadOnlyList<TtsVoice> Voices { get; }

    /// <summary>Сколько реплик ждёт очереди.</summary>
    int QueueLength { get; }

    /// <summary>Статусная строка модуля. Пустая — пока нечего сообщать.</summary>
    string Status { get; }

    /// <summary>Статус — это ошибка (красным), а не подтверждение.</summary>
    bool IsStatusOk { get; }

    /// <summary>Состояние изменилось — окно настроек обновит надписи.</summary>
    event Action? StatusChanged;

    /// <summary>
    /// Прогнать реплику через разбор команд и поставить в очередь. Возвращает
    /// разобранную команду, а не «да/нет»: окну нужно знать, <em>почему</em>
    /// реплика не озвучена.
    /// </summary>
    TtsChatCommand Submit(string? user, string? text);

    /// <summary>Остановить текущую фразу. Очередь при этом сохраняется.</summary>
    void StopSpeaking();

    /// <summary>Опустошить очередь озвучки.</summary>
    void ClearQueue();

    /// <summary>Есть ли куда озвучивать: микшер запущен и стрип куда-то послан.</summary>
    bool CanSpeakNow();
}
