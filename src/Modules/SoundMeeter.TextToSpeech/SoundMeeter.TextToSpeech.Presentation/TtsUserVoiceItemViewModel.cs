using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.TextToSpeech.Entities;

namespace SoundMeeter.TextToSpeech.Presentation;

/// <summary>Голос, выбранный пользователю чата: имя, код и «используется сейчас».</summary>
public sealed partial class TtsUserVoiceItemViewModel : ObservableObject
{
    public TtsUserVoiceItemViewModel(TtsUserVoice model)
    {
        Model = model;
    }

    public TtsUserVoice Model { get; }

    /// <summary>Логин без «@».</summary>
    public string User => Model.User;

    /// <summary>Имя голоса SAPI.</summary>
    public string Voice => Model.Voice;

    /// <summary>Подпись строки списка.</summary>
    public string DisplayName => $"{Model.User} → {Model.Voice}";

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    /// <summary>Удаление обрабатывает окно: список — его, а модель живёт в настройках.</summary>
    public event Action<TtsUserVoiceItemViewModel>? RemoveRequested;
}