using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Audio.Contract;

/// <summary>
/// Предоставляет список стрипов (входных и выходных) для привязки к ним
/// внешних контроллеров (MIDI, OBS-док и т.д.).
/// </summary>
public interface IStripProvider
{
    /// <summary>Входные стрипы (микрофоны, loopback, виртуальные кабели).</summary>
    IReadOnlyList<IInputStrip> Inputs { get; }

    /// <summary>Выходные стрипы (шины).</summary>
    IReadOnlyList<IOutputStrip> Buses { get; }
}
