namespace SoundMeeter.Framework.Entities;

/// <summary>
/// Представление файла журнала для отображения в UI.
/// Неизменяемый (immutable) для безопасной передачи между потоками.
/// </summary>
public sealed record LogFile
{
    public LogFile(string fullname)
    {
        Fullname = fullname;
        // Path.GetFileName намного быстрее и не создает объект FileInfo в куче
        Name = Path.GetFileName(fullname);
    }

    public string Fullname { get; init; }
    public string Name { get; init; }
}
