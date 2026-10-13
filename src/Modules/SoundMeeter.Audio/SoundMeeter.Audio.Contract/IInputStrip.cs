using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Audio.Contract;

/// <summary>
/// Входной стрип для привязки внешних контроллеров.
/// Содержит только те свойства, которые нужны для MIDI, OBS-дока и т.д.
/// </summary>
public interface IInputStrip
{
    string Id { get; }
    string Title { get; }
    string DeviceId { get; }
    string Name { get; }
    float PeakLevel { get; }

    /// <summary>Устройство на месте (открылось, не занято другим приложением).</summary>
    bool IsAvailable { get; }

    /// <summary>Стрип наполняется кодом приложения, а не устройством (модуль TTS).</summary>
    bool IsGenerated { get; }

    bool IsMuted { get; set; }
    bool IsSolo { get; set; }
    bool IsMono { get; set; }
    float VolumeDb { get; set; }
    float GainDb { get; set; }
    bool DenoiserEnabled { get; set; }
    float DenoiserNoiseRemover { get; set; }
    float DenoiserDryWet { get; set; }
    float DenoiserFormantLowDb { get; set; }
    float DenoiserFormantMidDb { get; set; }
    float DenoiserFormantHighDb { get; set; }
    float DenoiserFormantGroupDb { get; set; }
    bool CompressorEnabled { get; set; }
    bool FxGainEnabled { get; set; }
    bool DelayEnabled { get; set; }
    bool ReverbEnabled { get; set; }
    bool EqEnabled { get; set; }
    bool Func1Engaged { get; set; }
    bool Func2Engaged { get; set; }
}
