using SoundMeeter.Services;
using SoundMeeter.Tests.Infrastructure;
using SoundMeeter.ViewModels;
using SoundMeeter.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Xunit;

namespace SoundMeeter.Tests;

/// <summary>
/// Окно настроек модуля синтеза речи (SM-E01).
///
/// Проверяется не «красиво ли выглядит», а три вещи, которые ломаются молча и
/// заметны только пользователю:
///
/// <list type="bullet">
/// <item>разметка вообще разбирается — словари стилей и ресурсы на месте; без
///       этого окно просто не открылось бы;</item>
/// <item>поля подписаны, а список целей не пуст — иначе «куда отдавать голос»
///       пришлось бы угадывать;</item>
/// <item>применение доходит до модуля: окно не «открывается вхолостую».</item>
/// </list>
///
/// Без звуковой карты и без голосов в системе: всё, что связано с SAPI, подменено
/// заглушками в <see cref="TextToSpeechServiceTests"/>.
/// </summary>
public class TextToSpeechWindowTests
{
    [Fact]
    public void TheWindowParsesAndLaysOut()
    {
        UiHost.Run(() =>
        {
            var host = new FakeTextToSpeechHost();
            var window = new TextToSpeechWindow(new TextToSpeechSettingsViewModel(host));
            try
            {
                // Окно нужно показать: у WPF содержимое Window раскладывается только
                // после Show, иначе визуальное дерево пустое.
                window.Show();
                window.UpdateLayout();

                Assert.Same(host, (window.DataContext as TextToSpeechSettingsViewModel)?.Host);
                Assert.NotEmpty(VisualTree.FindAll<Button>(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Каждое поле окна обязано быть привязано к настройке модуля. Проверяются не
    /// подписи, а сами привязки: поле, потерявшее привязку, выглядит вполне
    /// ordinarily — пользователь правит его, ничего не происходит, и он ищет
    /// причину в модуле, а не в окне.
    /// </summary>
    [Fact]
    public void EveryEditableFieldIsBoundToASetting()
    {
        UiHost.Run(() =>
        {
            var window = new TextToSpeechWindow(new TextToSpeechSettingsViewModel(new FakeTextToSpeechHost()));
            try
            {
                window.Show();
                window.UpdateLayout();

                var paths = BoundPaths(window);

                string[] required =
                [
                    nameof(TextToSpeechSettingsViewModel.Enabled),
                    nameof(TextToSpeechSettingsViewModel.SelectedTarget),
                    nameof(TextToSpeechSettingsViewModel.SelectedVoice),
                    nameof(TextToSpeechSettingsViewModel.Rate),
                    nameof(TextToSpeechSettingsViewModel.Volume),
                    nameof(TextToSpeechSettingsViewModel.SpeakCommandText),
                    nameof(TextToSpeechSettingsViewModel.VoiceCommandText),
                    nameof(TextToSpeechSettingsViewModel.AllowVoiceChange),
                    nameof(TextToSpeechSettingsViewModel.MaxQueueLength),
                    nameof(TextToSpeechSettingsViewModel.MaxMessagesPerMinute),
                    nameof(TextToSpeechSettingsViewModel.MaxMessageChars),
                    nameof(TextToSpeechSettingsViewModel.IgnoredUsersText),
                    nameof(TextToSpeechSettingsViewModel.NewUserText),
                    nameof(TextToSpeechSettingsViewModel.NewUserVoice),
                    nameof(TextToSpeechSettingsViewModel.TestUserText),
                    nameof(TextToSpeechSettingsViewModel.TestTextText),
                ];

                foreach (string path in required)
                    Assert.Contains(path, paths);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Все пути привязок в поддереве. Перебираются только те свойства элементов
    /// управления, которые в этом окне правятся, — обход по отражению здесь был бы
    /// хрупким: состав свойств у разных версий WPF разный.
    /// </summary>
    private static HashSet<string> BoundPaths(DependencyObject root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        void Collect(DependencyObject? element)
        {
            switch (element)
            {
                case TextBox box: Add(box, TextBox.TextProperty); break;
                case Slider slider: Add(slider, RangeBase.ValueProperty); break;
                case CheckBox check: Add(check, ToggleButton.IsCheckedProperty); break;
                case ComboBox combo: Add(combo, Selector.SelectedItemProperty); break;
            }
        }

        void Add(DependencyObject element, DependencyProperty property)
        {
            if (BindingOperations.GetBindingExpression(element, property) is not { } expression) return;
            if (expression.ParentBinding.Path.Path is { Length: > 0 } path) paths.Add(path);
        }

        Collect(root);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (string path in BoundPaths(VisualTreeHelper.GetChild(root, i))) paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// Пункт меню обязан быть в панели инструментов: модуль, до которого нельзя
    /// добраться, выглядит как отсутствующий. Пункт лежит внутри выпадающего меню,
    /// поэтому его приходится открыть — иначе он не в визуальном дереве.
    /// </summary>
    [Fact]
    public void TheToolbarMenuHasAnEntryForTheModule()
    {
        UiHost.Run(() =>
        {
            var toolbar = new Views.Controls.MixerToolbarView
            {
                DataContext = new TextToSpeechSettingsViewModel(new FakeTextToSpeechHost()),
            };
            VisualTree.Layout(toolbar, 700, 44);

            var popup = VisualTree.FindByName<Popup>(toolbar, "SettingsPopup");
            Assert.NotNull(popup);

            var tip = Loc.Get("Sm.Toolbar.TextToSpeechTip");
            Assert.Contains(VisualTree.FindAll<Button>(popup.Child), button =>
                button.ToolTip is string text && text == tip);
        });
    }

    /// <summary>
    /// В списке целей обязан быть и каждый стрип микшера, и пункт «отдельный канал
    /// TTS»: без второго у пользователя не было бы способа завести канал,
    /// которого ещё нет.
    /// </summary>
    [Fact]
    public void TheTargetListOffersEveryStripPlusTheOwnOne()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host);

        Assert.Contains(vm.Targets, target => target.Id == TtsTargetItemViewModel.GeneratedId);
        Assert.Contains(vm.Targets, target => target.Id == "strip-mic");
    }

    [Fact]
    public void TheVoiceListIsFilledFromTheSystem()
    {
        var vm = new TextToSpeechSettingsViewModel(new FakeTextToSpeechHost());

        Assert.NotEmpty(vm.Voices);
        Assert.NotNull(vm.SelectedVoice);
    }

    /// <summary>
    /// Без явного выбора голоса модуль должен взять русский: иначе стример услышал
    /// бы русскую фразу английским голосом.
    /// </summary>
    [Fact]
    public void TheDefaultVoicePrefersRussian()
    {
        var vm = new TextToSpeechSettingsViewModel(new FakeTextToSpeechHost());

        Assert.True(vm.SelectedVoice!.IsRussian);
    }

    /// <summary>Основная проверка: значения из полей доходят до настроек модуля.</summary>
    [Fact]
    public void ApplyingWritesTheFieldsIntoTheModuleSettings()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host)
        {
            Enabled = true,
            Rate = 3,
            Volume = 40,
            MaxQueueLength = 7,
            MaxMessagesPerMinute = 2,
            MaxMessageChars = 120,
            SpeakCommandText = "!say",
            VoiceCommandText = "!voice",
            IgnoredUsersText = "bot, @spammer",
        };

        vm.ApplyCommand.Execute(null);

        var settings = host.TtsSettings;
        Assert.True(settings.Enabled);
        Assert.Equal(3, settings.Rate);
        Assert.Equal(40, settings.Volume);
        Assert.Equal(7, settings.MaxQueueLength);
        Assert.Equal(2, settings.MaxMessagesPerMinute);
        Assert.Equal(120, settings.MaxMessageChars);
        Assert.Equal("!say", settings.SpeakCommand);
        Assert.Equal("!voice", settings.VoiceCommand);
        Assert.Equal(new[] { "bot", "spammer" }, settings.IgnoredUsers);
    }

    /// <summary>
    /// Значения вне пределов приводятся к рабочим: в файле настроек не должно быть
    /// темпа «999», который потом пошёл бы в синтезатор.
    /// </summary>
    [Fact]
    public void ValuesOutsideTheRangeAreBroughtIntoIt()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host)
        {
            Enabled = true,
            Rate = 999,
            Volume = -50,
            MaxQueueLength = 0,
        };

        vm.ApplyCommand.Execute(null);

        var settings = host.TtsSettings;
        Assert.InRange(settings.Rate, -10, 10);
        Assert.InRange(settings.Volume, 0, 100);
        Assert.InRange(settings.MaxQueueLength, 1, 100);
    }

    /// <summary>
    /// «Свой канал» превращается в настоящий стрип, и в настройках остаётся его Id,
    /// а не маркер: иначе после перезапуска приложения голос потерял бы цель.
    /// </summary>
    [Fact]
    public void ChoosingTheOwnChannelCreatesAStripInTheMixer()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host)
        {
            Enabled = true,
            SelectedTarget = new TtsTargetItemViewModel(
                TtsTargetItemViewModel.GeneratedId, "TTS", "", true, true),
        };

        vm.ApplyCommand.Execute(null);

        var created = Assert.Single(host.Engine.FakeInputs.Where(input => input.IsGenerated));
        Assert.Equal(created.Id, host.TtsSettings.TargetInputId);
        Assert.NotEqual(TtsTargetItemViewModel.GeneratedId, host.TtsSettings.TargetInputId);
    }

    [Fact]
    public void ApplyingTwiceDoesNotDuplicateTheOwnChannel()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host)
        {
            Enabled = true,
            SelectedTarget = new TtsTargetItemViewModel(
                TtsTargetItemViewModel.GeneratedId, "TTS", "", true, true),
        };

        vm.ApplyCommand.Execute(null);
        vm.ApplyCommand.Execute(null);

        Assert.Single(host.Engine.FakeInputs.Where(input => input.IsGenerated));
    }

    /// <summary>
    /// Назначение голоса пользователю добавляет строку в список и убирает прежнюю
    /// запись того же логина — иначе в списке появлялись бы дубли, а из чата
    /// пришёл бы только один из голосов.
    /// </summary>
    [Fact]
    public void AssigningAVoiceReplacesThePreviousOneForTheSameUser()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host) { Enabled = true };

        vm.NewUserText = "moderator";
        vm.NewUserVoice = vm.Voices[0];
        vm.AddUserVoiceCommand.Execute(null);

        // После добавления логин очищается — так пользователь не добавит случайно
        // того же модератора второй раз.
        Assert.Equal("", vm.NewUserText);

        vm.NewUserText = "moderator";
        vm.NewUserVoice = vm.Voices[^1];
        vm.AddUserVoiceCommand.Execute(null);

        Assert.Single(vm.UserVoices);
        Assert.Equal("moderator", vm.UserVoices[0].User);
        Assert.Equal(vm.Voices[^1].Name, vm.UserVoices[0].Voice);
    }

    [Fact]
    public void RemovingAVoiceFromTheListTakesItAway()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host)
        {
            Enabled = true,
            NewUserText = "moderator",
        };

        // Голос по умолчанию уже выбран конструктором, поэтому достаточно логина.
        vm.AddUserVoiceCommand.Execute(null);
        Assert.Single(vm.UserVoices);

        vm.UserVoices[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.UserVoices);
    }

    [Fact]
    public void AddingAVoiceWithoutAUserIsRefusedWithAMessage()
    {
        var vm = new TextToSpeechSettingsViewModel(new FakeTextToSpeechHost())
        {
            Enabled = true,
            NewUserText = "   ",
        };

        vm.AddUserVoiceCommand.Execute(null);

        Assert.Empty(vm.UserVoices);
        Assert.False(vm.IsStatusOk);
        Assert.NotEmpty(vm.Feedback);
    }

    /// <summary>
    /// Проверка разбора команд идёт через настоящий разбор, а не через отдельную
    /// логику окна: только так она готовит подключение к чату.
    /// </summary>
    [Fact]
    public void CheckingACommandLineGoesThroughTheRealParser()
    {
        var host = new FakeTextToSpeechHost();
        var vm = new TextToSpeechSettingsViewModel(host)
        {
            Enabled = true,
            TestUserText = "moderator",
            TestTextText = "!ttsvoice Microsoft Irina",
        };

        vm.TestMessageCommand.Execute(null);

        Assert.Contains(host.TtsSettings.UserVoices,
            pair => pair.User == "moderator" && pair.Voice == "Microsoft Irina");
    }

    [Fact]
    public void AnOrdinaryLineIsReportedAsNotACommand()
    {
        var vm = new TextToSpeechSettingsViewModel(new FakeTextToSpeechHost())
        {
            Enabled = true,
            TestUserText = "viewer",
            TestTextText = "привет, как дела?",
        };

        vm.TestMessageCommand.Execute(null);

        Assert.False(vm.IsStatusOk);
        Assert.Equal(Loc.Get("Sm.Tts.NotACommand"), vm.Feedback);
    }
}
