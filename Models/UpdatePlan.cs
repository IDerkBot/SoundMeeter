namespace SoundMeeter.Models;

/// <summary>
/// План автообновления: проверенный распакованный payload, точный список файлов,
/// которые будут удалены из каталога установки, и сводка для показа пользователю.
///
/// Существует отдельным шагом от применения именно потому, что удаление чужих
/// файлов необратимо: решение должен принимать человек, видя список, а не код,
/// получивший архив из сети.
/// </summary>
public sealed class UpdatePlan
{
    /// <summary>Каталог с распакованными файлами новой сборки.</summary>
    public required string PayloadDirectory { get; init; }

    /// <summary>Каталог установки, файлы которого заменяются.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>Путь к запускаемому после обновления файлу.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Абсолютные пути файлов каталога установки, которых нет в новой сборке.</summary>
    public required IReadOnlyList<string> FilesToDelete { get; init; }

    /// <summary>Число файлов в новой сборке (для сводки).</summary>
    public required int NewFileCount { get; init; }

    /// <summary>Суммарный размер новой сборки, байт.</summary>
    public required long NewTotalBytes { get; init; }

    /// <summary>Человекочитаемый отчёт о проверке целостности.</summary>
    public required IReadOnlyList<string> ValidationNotes { get; init; }

    /// <summary>Тег релиза, из которого получен payload.</summary>
    public required string TagName { get; init; }
}
