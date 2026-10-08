using System.Text.RegularExpressions;

namespace SoundMeeter.Services;

/// <summary>
/// Виртуальный кабель (VB-Cable, VAC, StreamerCable) — это всегда два
/// endpoint'а: один вход (захват) и один выход (воспроизведение). Звук приложений,
/// отправленный в выход, приходит во вход, поэтому половинки связываются по имени.
/// Имена задаёт пользователь при установке кабеля, поэтому общая форма разная:
/// «CABLE-A Input (VB-Audio Virtual Cable)», «L1In.Discord», «L1Out.VoiceChat»,
/// «B1.Mic»/«B1.Microphone». Надёжная часть имени — префикс до точки, из которого
/// убирается признак стороны (In/Out/Input/Output).
/// </summary>
public static class CablePairing
{
    /// <summary>
    /// Слово, различающее половинки кабеля. Встречается и в конце имени
    /// («CABLE-x Input»), и перед названием драйвера в скобках
    /// («CABLE-x Input (VB-Audio Virtual Cable)»), и слитно с префиксом
    /// («L1In.Discord»).
    /// </summary>
    private static readonly Regex FlowToken =
        new(@"\s+(?<token>Input|Output)\b(?=\s|\(|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Признак стороны, слитый с префиксом кабеля: «L1In.», «B1Out.».</summary>
    private static readonly Regex AttachedFlowToken =
        new(@"(?<token>Input|Output|In|Out)\.|(?<token2>Input|Output|In|Out)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Parenthetical = new(@"\([^()]*\)", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Имя устройства — виртуальный кабель (драйверы VB-Cable/VAC/StreamerCable).</summary>
    public static bool IsVirtualCableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        return name.Contains("cable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("virtual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Внутренние endpoint'ы VoiceMeeter (VAIO и панель A/B-шины) подпадают под
    /// «виртуальный кабель» по слову vb-audio, но кабелями не являются: это
    /// записи собственных шин VoiceMeeter. Приложения в них не уходят, поэтому
    /// половинками кабеля мы их не считаем.
    /// </summary>
    public static bool IsVoiceMeeterInternal(string? name) =>
        name != null && name.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ключ, по которому ищется вторая половинка кабеля: имя без «Input»/«Output».
    /// null — имя не похоже на половинку кабеля.
    /// </summary>
    public static string? GetPairKey(string? name, bool isMicrophone)
    {
        if (!IsVirtualCableName(name) || IsVoiceMeeterInternal(name)) return null;

        var match = FlowToken.Match(name!);
        if (match.Success)
        {
            // У входа кабеля слово Input, у выхода — Output. Перепутанные имена
            // (у входа вдруг Output) парой не считаем.
            bool tokenIsInput = match.Groups["token"].Value.Equals("Input", StringComparison.OrdinalIgnoreCase);
            if (tokenIsInput != isMicrophone) return null;

            return Clean(FlowToken.Replace(name!, " "));
        }

        return GetSlotKey(name!, isMicrophone);
    }

    /// <summary>
    /// Ключ по «слоту» кабеля: имя без скобок, обрезанное до первой точки, без
    /// признака стороны. «L1In.Discord», «L1Out.VoiceChat» и «B1.Mic» дают
    /// соответственно «l1», «l1» и «b1» — по этому ключу половинки находятся
    /// даже тогда, когда подписи различаются («L3In.Game» ↔ «L3Out.Games»).
    /// null — в имени нет ни точки, ни признака стороны: кабель так не назван.
    /// </summary>
    public static string? GetSlotKey(string? name, bool isMicrophone)
    {
        if (!IsVirtualCableName(name) || IsVoiceMeeterInternal(name)) return null;

        var head = Parenthetical.Replace(name!, " ").Trim();
        int dot = head.IndexOf('.');
        if (dot < 0) return null;

        head = head[..dot].Trim();
        var match = AttachedFlowToken.Match(head);
        if (match.Success) head = head[..match.Index].Trim();

        // «L1In» без точки в конце: снимаем признак стороны с конца.
        if (head.EndsWith("Input", StringComparison.OrdinalIgnoreCase)) head = head[..^5];
        else if (head.EndsWith("Output", StringComparison.OrdinalIgnoreCase)) head = head[..^6];
        else if (head.EndsWith("In", StringComparison.OrdinalIgnoreCase)) head = head[..^2];
        else if (head.EndsWith("Out", StringComparison.OrdinalIgnoreCase)) head = head[..^3];

        var key = Clean(head);
        return key is null || key.Length < 2 ? null : key;
    }

    /// <summary>
    /// Тот же ключ, но без скобок: спасает, когда драйвер дописал своё название
    /// только с одной стороны («CABLE-A Input» против «CABLE-A Output (VB-Cable)»).
    /// </summary>
    private static string? GetLoosePairKey(string? name, bool isMicrophone)
    {
        var key = GetPairKey(name, isMicrophone);
        return key == null ? null : Clean(Parenthetical.Replace(key, " "));
    }

    /// <summary>
    /// Вход виртуального кабеля: устройство захвата, в которое уходят приложения —
    /// через связанный выход кабеля или через явно выбранный. Внутренние
    /// endpoint'ы VoiceMeeter кабелями не считаются, поэтому с ними это не так.
    /// </summary>
    public static bool IsCableCapture(DeviceInfo? device) =>
        device is { IsVirtualCable: true, IsMicrophone: true } && !IsVoiceMeeterInternal(device.Name);

    /// <summary>
    /// Render-endpoint, в который уходят приложения стрипа, снявшего это устройство.
    /// Явный выбор пользователя важнее всего; иначе для входа виртуального кабеля
    /// берётся связанный выход (звук приходит во вход, а играть надо в выход), для
    /// render-устройства, снятого loopback'ом, — оно само, для микрофона — null.
    /// </summary>
    public static string? GetAppRenderDeviceId(
        DeviceInfo? device,
        bool stripIsMicrophone,
        string? explicitTargetId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitTargetId)) return explicitTargetId;
        if (device == null) return null;
        if (IsCableCapture(device) && device!.CablePeerId is { } peer) return peer;
        return stripIsMicrophone ? null : device.DeviceId;
    }

    /// <summary>
    /// Проставляет каталогу признак «виртуальный кабель» и DeviceId второй
    /// половинки. У половинки, пары которой нет, CablePeerId остаётся null:
    /// кабель бывает установлен только со стороны входа или только выхода, а имя
    /// может быть задано так, что пару не угадать (тогда цель выбирается вручную).
    /// </summary>
    public static List<DeviceInfo> Link(IEnumerable<DeviceInfo> devices)
    {
        var linked = devices
            .Select(d => d with { IsVirtualCable = IsVirtualCableName(d.Name), CablePeerId = null })
            .ToList();

        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < linked.Count; i++) indexById[linked[i].DeviceId] = i;

        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Сначала строгое совпадение: у разных кабелей в скобках различается буква
        // (CABLE-A / CABLE-B), и терять её нельзя. Потом — по имени без скобок.
        Pair(linked, indexById, paired, GetPairKey);
        Pair(linked, indexById, paired, GetLoosePairKey);

        return linked;
    }

    private static void Pair(
        List<DeviceInfo> linked,
        Dictionary<string, int> indexById,
        HashSet<string> paired,
        Func<string?, bool, string?> keyOf)
    {
        var groups = linked
            .Where(d => d.IsVirtualCable && d.CablePeerId == null && !paired.Contains(d.DeviceId))
            .GroupBy(d => keyOf(d.Name, d.IsMicrophone) ?? "", StringComparer.Ordinal);

        foreach (var group in groups)
        {
            if (group.Key.Length == 0) continue;

            var capture = group.Where(d => d.IsMicrophone).ToList();
            var render = group.Where(d => !d.IsMicrophone).ToList();

            // Связываем только однозначные пары. Если в слоте оказалось несколько
            // полов с одной стороны (один кабель назван как «B1Out.Other», другой —
            // как «B1» и так далее), угадывать нельзя: ошибка в связке молча
            // разводит звук не туда. Такие стрипы получают цель вручную.
            if (capture.Count != 1 || render.Count != 1) continue;

            if (!indexById.TryGetValue(capture[0].DeviceId, out int ci)) continue;
            if (!indexById.TryGetValue(render[0].DeviceId, out int ri)) continue;

            linked[ci] = linked[ci] with { CablePeerId = render[0].DeviceId };
            linked[ri] = linked[ri] with { CablePeerId = capture[0].DeviceId };
            paired.Add(capture[0].DeviceId);
            paired.Add(render[0].DeviceId);
        }
    }

    private static string? Clean(string value)
    {
        var key = Spaces.Replace(value, " ").Trim().ToLowerInvariant();
        return key.Length == 0 ? null : key;
    }
}
