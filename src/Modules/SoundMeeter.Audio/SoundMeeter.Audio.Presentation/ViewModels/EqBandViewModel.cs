using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Audio.Entities;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;

namespace SoundMeeter.Audio.Presentation.ViewModels;

/// <summary>
/// Полоса графического эквалайзера стрипа: частота задана разметкой шага октавы,
/// а пользователь правит только усиление.
///
/// Значение живёт в модели пресета, а полоса работает с ним через
/// <see cref="InputChannelModel.GetEqBand"/>/<see cref="InputChannelModel.SetEqBand"/>:
/// кривую в окне рисует один и тот же список, который читает DSP, поэтому
/// «написано» и «слышно» не могут разойтись.
/// </summary>
public sealed partial class EqBandViewModel : LocalizedViewModel
{
    private readonly InputChannelModel _model;
    private readonly Action _markDirty;

    /// <summary>Центральная частота полосы, Гц. Задана моделью, не пользователем.</summary>
    public float Frequency { get; }

    /// <summary>
    /// Номер полосы в модели. Кривая рисует набор полос как усиления по номерам,
    /// а не по частотам: частота — это только подпись, и искать по ней индекс
    /// означало бы лишний поиск на каждый кадр отрисовки.
    /// </summary>
    public int Index { get; }

    /// <summary>Подпись частоты под узлом кривой: «125», «1k», «16k».</summary>
    public string Label { get; }

    /// <summary>
    /// Усиление полосы, дБ. Диапазон совпадает с тем, что проверяет DSP и миграция
    /// схемы: иначе полоса позволяла бы записать в пресет значение, которое
    /// обработка потом срежет, а UI врал бы.
    /// </summary>
    public float Gain
    {
        get => _model.GetEqBand(Index);
        set
        {
            float limit = InputChannelModel.EqBandGainLimitDb;
            float clamped = float.IsFinite(value) ? Math.Clamp(value, -limit, limit) : 0f;

            // Повторная запись того же значения не нужна: иначе двойной щелчок по
            // уже ровной полосе зря помечал бы пресет изменённым.
            if (_model.GetEqBand(Index) == clamped)
            {
                OnPropertyChanged(nameof(Gain));
                OnPropertyChanged(nameof(Display));
                return;
            }

            _model.SetEqBand(Index, clamped);
            _markDirty();
            OnPropertyChanged(nameof(Gain));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Значение с единицей измерения: «3.0 dB», «−4.5 dB». Формат тот же, что у
/// крутилок эффектов: подпись полосы читается на кривой, а не в таблице.</summary>
    public string Display => $"{Gain.ToString("0.0", Loc.Culture)} dB";

    /// <summary>Подсказка полосы: частота, значение и напоминание про сброс.</summary>
    public string ToolTip =>
        $"{Label} Hz: {Display}  ·  {Loc.Get("Sm.Param.DoubleClickReset")}";

    /// <summary>Двойной щелчок по узлу кривой — вернуть 0 дБ.</summary>
    [RelayCommand]
    private void Reset() => Gain = 0f;

    public EqBandViewModel(int index, float frequency, InputChannelModel model, Action markDirty)
    {
        Index = index;
        _model = model;
        _markDirty = markDirty;
        Frequency = frequency;
        Label = FormatFrequency(frequency);
    }

    /// <summary>
    /// Частота подписью: килогерцы без дробной части («1k»), остальное — как
    /// есть («125»). Подписи короче подписи полной частоты, а в узле кривой
    /// места мало.
    /// </summary>
    private static string FormatFrequency(float hz) =>
        hz >= 1000f
            ? (hz / 1000f).ToString("0.#", Loc.Culture) + "k"
            : hz.ToString("0", Loc.Culture);

    /// <summary>Перечитывает подписи после смены языка (кто-то мог поменять язык).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ToolTip));
    }
}