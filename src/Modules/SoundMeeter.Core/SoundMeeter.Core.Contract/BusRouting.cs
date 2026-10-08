namespace SoundMeeter.Models;

/// <summary>
/// Настройка связи одного входа с одним выходом (шиной).
/// Ключом в словаре роутинга является DeviceId выходного устройства.
/// </summary>
public class BusRouting
{
    public bool Enabled { get; set; }
    public float GainDb { get; set; }
}