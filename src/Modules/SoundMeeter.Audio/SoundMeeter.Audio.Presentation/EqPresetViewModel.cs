using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Audio.Dsp;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;

namespace SoundMeeter.Audio.Presentation;

/// <summary>
/// Пресет кривой эквалайзера в списке выбора.
///
/// Отдельный класс, а не строка, потому что элемент списка должен и название на
/// текущем языке, и действие «применить», а список элементов ComboBox не
/// умеет ни того, ни другого сам.
/// </summary>
public sealed partial class EqPresetViewModel : LocalizedViewModel
{
    private readonly EqualizerPreset _preset;
    private readonly Action _apply;

    public EqualizerPreset Preset => _preset;

    public string Name => Loc.Get(_preset.Key);

    [RelayCommand]
    private void Apply() => _apply();

    public EqPresetViewModel(EqualizerPreset preset, Action apply)
    {
        _preset = preset;
        _apply = apply;
    }

    protected override void OnLanguageChangedCore() => OnPropertyChanged(nameof(Name));

    public override string ToString() => Name;
}