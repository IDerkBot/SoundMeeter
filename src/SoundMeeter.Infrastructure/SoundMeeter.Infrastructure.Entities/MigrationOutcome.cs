using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Infrastructure.Entities;

/// <summary>
/// Результат применения миграций к загруженному файлу настроек.
/// </summary>
public enum MigrationOutcome
{
    /// <summary>Файл в поддерживаемой версии (возможно, после миграций).</summary>
    Loaded,

    /// <summary>
    /// Файл записан более новой версией приложения. Данные не применены, файл
    /// не перезаписывается до явного сохранения пользователем.
    /// </summary>
    UnsupportedNewerVersion
}
