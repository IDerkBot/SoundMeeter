namespace SoundMeeter.Models;

public enum ChannelType
{
    HardwareInput,   // Физический микрофон/линейный вход
    VirtualInput,    // Виртуальный вход (VB-Cable)
    HardwareOutput,  // Физический выход (колонки)
    VirtualOutput    // Виртуальный выход (для OBS/Discord)
}
