using SoundMeeter.Models;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using SoundMeeter.Views.Controls;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Пустой микшер — штатное состояние после первого запуска: каналов нет, и
/// добавляет их пользователь. Лента стрипов без единой полосы выглядит как
/// сломанная, поэтому на месте пустых групп стоит подсказка, и она обязана
/// исчезать, как только появился хоть один стрип.
///
/// DataContext — лёгкая заглушка вместо <see cref="MainViewModel"/>: та собирает
/// одиннадцать сервисов, а лента интересуется только списками и командами.
/// Настоящий движок проверяется отдельно (<c>AudioEnginePresetTests</c>).
/// </summary>
public class MixerViewTests
{
    private sealed class EmptyMixer
    {
        public ObservableCollection<InputChannelViewModel> Inputs { get; } = new();
        public ObservableCollection<OutputBusViewModel> Buses { get; } = new();

        public ICommand AddInputCommand { get; } = new NoopCommand();
        public ICommand AddBusCommand { get; } = new NoopCommand();
        public ICommand RemoveInputCommand { get; } = new NoopCommand();
        public ICommand RemoveBusCommand { get; } = new NoopCommand();
    }

    private sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) { }
    }

    [Fact]
    public void AnEmptyMixerShowsTheHintInBothGroups()
    {
        UiHost.Run(() =>
        {
            var view = new MixerView { DataContext = new EmptyMixer() };
            VisualTree.Layout(view, 900, 500);

            Assert.Equal(Visibility.Visible, VisualTree.FindByName<TextBlock>(view, "InputsHint")!.Visibility);
            Assert.Equal(Visibility.Visible, VisualTree.FindByName<TextBlock>(view, "OutputsHint")!.Visibility);
        });
    }

    [Fact]
    public void TheHintGivesWayToTheFirstStrip()
    {
        UiHost.Run(() =>
        {
            var mixer = new EmptyMixer();
            var view = new MixerView { DataContext = mixer };
            VisualTree.Layout(view, 900, 500);

            mixer.Inputs.Add(Strips.Microphone(new FakeAudioEngine()));
            mixer.Buses.Add(new OutputBusViewModel(
                new FakeAudioEngine(), new OutputBusModel { Name = "Speakers" }, () => { }));
            VisualTree.Layout(view, 900, 500);

            Assert.Equal(Visibility.Collapsed, VisualTree.FindByName<TextBlock>(view, "InputsHint")!.Visibility);
            Assert.Equal(Visibility.Collapsed, VisualTree.FindByName<TextBlock>(view, "OutputsHint")!.Visibility);
        });
    }
}