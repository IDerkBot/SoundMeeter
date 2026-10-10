namespace SoundMeeter.Docking.Entities;

/// <summary>
/// Итог попытки добавить или убрать док в конфигурации OBS.
/// </summary>
/// <remarks>
/// Живёт в сущностях, а не рядом с установщиком: это данные, которые одинаково
/// нужны реализации (формирует результат), сценариям (передают в окно) и самому
/// окну (показывает). Установщик живёт в Contract.Implementation, а сценарий —
/// в UseCases, и общее у них было бы только это.
/// </remarks>
/// <param name="Success">Получилось ли.</param>
/// <param name="Message">Текст для пользователя: причина отказа или что сделано.</param>
/// <param name="Path">Правленный файл, если он был (для показа в статусе окна).</param>
public sealed record ObsDockInstallResult(bool Success, string Message, string? Path = null)
{
    public static ObsDockInstallResult Ok(string message, string? path = null) => new(true, message, path);

    public static ObsDockInstallResult Fail(string message) => new(false, message);
}