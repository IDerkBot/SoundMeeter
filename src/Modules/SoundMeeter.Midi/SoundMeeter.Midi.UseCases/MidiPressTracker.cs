using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Память о том, что сейчас нажато на контроллере.
///
/// Существует ради двух вещей, которые нельзя вывести из стрипов.
///
/// Фронт нажатия. Контроллер шлёт значение каждые ~10 мс, пока кнопка удерживается,
/// а не одно событие. Без памяти Toggle переключался бы двадцать раз в секунду.
/// Отсюда правило: действие выполняется только на смене состояния, а не на
/// каждом сообщении.
///
/// Откат PTT. Удержание запоминает состояние параметра ДО нажатия, чтобы
/// вернуть его на отпускание. Эту память больше нигде не хранить нельзя: у
/// стрипа есть только текущее значение, а нам нужно прошлое.
///
/// Память живёт здесь, а не внутри <see cref="RouteMidiMessageUseCase"/>, потому
/// что нужна двум сценариям: маршрутизации и сброса отслеживания при смене
/// устройства или привязки. Общее состояние — общий владелец, иначе сброс
/// пришлось бы лезть в другой сценарий по ссылке.
/// </summary>
public sealed class MidiPressTracker
{
    private readonly Dictionary<string, bool> _pressed = new();
    private readonly Dictionary<string, bool> _pttRestore = new();

    /// <summary>
    /// Нажата ли кнопка прямо сейчас.
    /// </summary>
    /// <returns>
    /// true, если состояние изменилось и действие надо выполнить; false, если
    /// это повтор того же состояния (кнопку держат) или отпускание без нажатия
    /// (контроллер шлёт мусор).
    /// </returns>
    public bool IsRisingEdge(string bindingKey, bool isPressed)
    {
        bool wasPressed = _pressed.TryGetValue(bindingKey, out var previous) && previous;
        _pressed[bindingKey] = isPressed;
        return isPressed != wasPressed;
    }

    /// <summary>
    /// Запомнить состояние параметра перед нажатием PTT и включить его.
    /// </summary>
    public void RememberAndPress(string bindingKey, bool valueBeforePress)
    {
        _pttRestore[bindingKey] = valueBeforePress;
    }

    /// <summary>
    /// Отпустить PTT.
    /// </summary>
    /// <returns>
    /// true, если было что откатывать. false означает, что кнопку отпустили без
    /// нажатия в этой сессии — восстанавливать нечего, и присваивать «false»
    /// наугад значило бы выключить параметр, который пользователь и не трогал.
    /// </returns>
    public bool ReleaseAndTakeRestore(string bindingKey, out bool valueToRestore)
    {
        if (_pttRestore.Remove(bindingKey, out var restore))
        {
            valueToRestore = restore;
            return true;
        }

        valueToRestore = false;
        return false;
    }

    /// <summary>
    /// Всё, что нажато по PTT, с откатом: значение ДО нажатия, если оно
    /// было запомнено. Сброс привязок и смена устройства обязаны это
    /// довести до конца, иначе микрофон, включённый удержанием, останется
    /// открытым навсегда — до перезапуска приложения.
    /// </summary>
    public IEnumerable<(string BindingKey, bool Value)> DrainHeldPtt() =>
        _pttRestore.Select(pair => (pair.Key, pair.Value)).ToList();

    /// <summary>Забыть всё: и фронты нажатий, и откаты PTT.</summary>
    public void Clear()
    {
        _pttRestore.Clear();
        _pressed.Clear();
    }
}