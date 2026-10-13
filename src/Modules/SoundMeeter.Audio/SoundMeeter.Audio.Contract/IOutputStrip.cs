using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Audio.Contract;

/// <summary>
/// Выходной стрип для привязки внешних контроллеров.
/// </summary>
public interface IOutputStrip
{
    string Id { get; }
    string Title { get; }
    string DeviceId { get; }
    string Name { get; }
    float PeakLevel { get; }
    bool IsAvailable { get; }
    bool IsMuted { get; set; }
    bool IsSolo { get; set; }
    bool IsMono { get; set; }
    float VolumeDb { get; set; }
}
