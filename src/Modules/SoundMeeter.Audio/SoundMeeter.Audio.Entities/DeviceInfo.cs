using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Audio.Entities;

/// <summary>Устройство в каталоге. IsMicrophone=true — устройство захвата (микрофон);
/// false — устройство воспроизведения (для входов как loopback, для шин как вывод).</summary>
public sealed record DeviceInfo(string DeviceId, string Name, bool IsMicrophone)
{
    /// <summary>Устройство — половина виртуального кабеля (VB-Cable, VAC, StreamerCable).</summary>
    public bool IsVirtualCable { get; init; }

    /// <summary>Вторая половина того же кабеля: для входа кабеля — его выход, и наоборот.
    /// null — парная половинка не найдена (кабель установлен только с одной стороны).</summary>
    public string? CablePeerId { get; init; }
}
