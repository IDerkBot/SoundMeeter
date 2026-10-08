namespace SoundMeeter.Models;

/// <summary>
/// Выходная шина (стрип вывода): физическое/виртуальное устройство
/// воспроизведения, куда микшируется аудио с выбранных входов.
/// </summary>
public class OutputBusModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>
    /// Пользовательское имя канала (редактируется по правому клику сверху стрипа).
    /// Пустое — отображается имя устройства (Name).
    /// </summary>
    public string ChannelName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public float VolumeDb { get; set; }
    public bool IsMuted { get; set; }
    public bool IsMono { get; set; }
    public bool IsSolo { get; set; }
    public float PeakLevel { get; set; }

    /// <summary>
    /// false, если устройство не удалось открыть (например, отключено/занято).
    /// </summary>
    public bool IsAvailable { get; set; } = true;
}