using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoundMeeter.Views.Controls;

/// <summary>
/// Сброс параметра к значению по умолчанию двойным щелчком по регулятору
/// (фейдер, ползунок, крутилка).
///
/// Почему attached property, а не обработчик в стиле: регуляторов на стрипе
/// десяток, и у каждого СВОЙ дефолт (у посылки — 0 дБ, у Noise Remover — 100 %,
/// у компрессора — −18 дБ). Значение по умолчанию знает ViewModel параметра,
/// поэтому в разметку передаётся команда сброса, а не число в <c>Tag</c>:
/// иначе число в разметке рано или поздно разошлось бы с моделью.
///
/// Работает с любым элементом (Slider, GainKnob, ComboBox), потому что вешает
/// обработчик на сам элемент, а не на конкретный шаблон.
/// </summary>
public static class ParamReset
{
    /// <summary>Команда сброса параметра к значению по умолчанию.</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(ParamReset),
            new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    /// <summary>
    /// Событие именно класса <see cref="Control"/> (а не <see cref="Mouse"/>):
    /// оно всплывает от Thumb/Slider и даёт handledEventsToo — у ползунка
    /// двойной щелчок к самому себе уже обработан, а сброс всё равно нужен.
    /// </summary>
    private static readonly MouseButtonEventHandler DoubleClickHandler = OnDoubleClick;

    private static void OnCommandChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Control control) return;
        control.RemoveHandler(Control.MouseDoubleClickEvent, DoubleClickHandler);
        if (e.NewValue is ICommand) control.AddHandler(Control.MouseDoubleClickEvent, DoubleClickHandler);
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetCommand((DependencyObject)sender) is not { } command) return;
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }
}
