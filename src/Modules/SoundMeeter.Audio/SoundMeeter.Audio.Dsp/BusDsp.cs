using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundMeeter.Models;

namespace SoundMeeter.Audio;

/// <summary>
/// Обёртка микшера одной выходной шины. Применяет громкость/мут/моно/Solo шины,
/// измеряет пик для VU-метра и подаёт результат в WasapiOut.
/// </summary>
public sealed class BusDsp : ISampleProvider
{
    private readonly MixingSampleProvider _mixer;
    private readonly OutputBusModel _bus;
    private readonly SoloState _solo;

    public BusDsp(OutputBusModel bus, SoloState solo)
    {
        _bus = bus;
        _solo = solo;
        _mixer = new MixingSampleProvider(InputSource.OutputFormat)
        {
            ReadFully = true
        };
    }

    public WaveFormat WaveFormat => _mixer.WaveFormat;

    public void AddInput(ISampleProvider input) => _mixer.AddMixerInput(input);
    public void RemoveInput(ISampleProvider input) => _mixer.RemoveMixerInput(input);

    public int Read(Span<float> buffer)
    {
        int read = _mixer.Read(buffer);

        bool blockedBySolo = _solo.AnyOutputSolo && !_bus.IsSolo;
        bool muted = _bus.IsMuted || blockedBySolo;
        float volume = muted ? 0f : DbToLinear(_bus.VolumeDb);

        float peak = 0f;
        if (volume == 0f)
        {
            buffer.Slice(0, read).Clear();
            peak = 0f;
        }
        else if (_bus.IsMono)
        {
            for (int i = 0; i < read; i += 2)
            {
                float s = (buffer[i] + buffer[i + 1]) * 0.5f * volume;
                buffer[i] = s;
                buffer[i + 1] = s;
                float abs = MathF.Abs(s);
                if (abs > peak) peak = abs;
            }
        }
        else
        {
            for (int i = 0; i < read; i++)
            {
                float s = buffer[i] * volume;
                buffer[i] = s;
                float abs = MathF.Abs(s);
                if (abs > peak) peak = abs;
            }
        }

        _bus.PeakLevel = peak;
        return read;
    }

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}