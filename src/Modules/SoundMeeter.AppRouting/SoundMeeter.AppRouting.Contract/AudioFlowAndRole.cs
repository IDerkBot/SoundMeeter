namespace SoundMeeter.AppRouting.Contract;

/// <summary>
/// Направление аудиопотока процесса.
/// </summary>
public enum AudioFlow
{
    /// <summary>Воспроизведение: то, что приложение выводит в колонки.</summary>
    Render = 0,

    /// <summary>Захват: то, что приложение берёт с микрофона.</summary>
    Capture = 1,
}

/// <summary>
/// Роль Windows для смены endpoint по умолчанию.
/// </summary>
public enum ERole
{
    Console,
    Multimedia,
    Communications,
}