using System.IO;
using System.Reflection;
using Xunit;

/// <summary>
/// Р“СЂР°РЅРёС†С‹ СЃР±РѕСЂРѕРє РјРѕРґСѓР»СЊРЅРѕРіРѕ РјРѕРЅРѕР»РёС‚Р°. РџСЂРѕРІРµСЂСЏРµРј С‚Рѕ, С‡С‚Рѕ РєРѕРјРїРёР»СЏС‚РѕСЂ РЅРµ РїСЂРѕРІРµСЂСЏРµС‚ Рё
/// С‡С‚Рѕ Р»РѕРјР°РµС‚СЃСЏ С‚РёС…Рѕ: РµСЃР»Рё РјРѕРґСѓР»СЊ РЅР°С‡РЅС‘С‚ СЃСЃС‹Р»Р°С‚СЊСЃСЏ РЅР° РїСЂРёР»РѕР¶РµРЅРёРµ РёР»Рё UI-СЃР±РѕСЂРєСѓ,
/// СЃР±РѕСЂРєР° РѕСЃС‚Р°РЅРµС‚СЃСЏ Р·РµР»С‘РЅРѕР№, Р° СЂР°Р·РґРµР»РµРЅРёРµ СЃР»РѕС‘РІ РїРµСЂРµСЃС‚Р°РЅРµС‚ СЃСѓС‰РµСЃС‚РІРѕРІР°С‚СЊ С„Р°РєС‚РёС‡РµСЃРєРё.
///
/// РЎС‚СЂСѓРєС‚СѓСЂР° РїРѕСЃР»Рµ РїРµСЂРµРµР·РґР° РЅР° РјРѕРґСѓР»СЊРЅС‹Р№ РјРѕРЅРѕР»РёС‚:
///   SoundMeeter.App (WinExe) вЂ” Composition Root, РµРґРёРЅСЃС‚РІРµРЅРЅР°СЏ СЃР±РѕСЂРєР° СЃ UI-С…РѕСЃС‚РѕРј;
///   SoundMeeter.Core.UseCases вЂ” СЏРґСЂРѕ СЃ ViewModels, СЃРѕР±РёСЂР°РµС‚ РјРѕРґСѓР»Рё;
///   SoundMeeter.*.{Contract,UseCases,Entities} вЂ” РјРѕРґСѓР»Рё РїСЂРµРґРјРµС‚РЅС‹С… РѕР±Р»Р°СЃС‚РµР№;
///   SoundMeeter.Infrastructure.* вЂ” СЃРµСЂРІРёСЃС‹ Рё СЂРµР°Р»РёР·Р°С†РёРё (Logger, Settings, Clipboard...);
///   SoundMeeter.Framework.* вЂ” СѓС‚РёР»РёС‚С‹.
///
/// РўРµСЃС‚ Р¶РёРІС‘С‚ Р·РґРµСЃСЊ, Р° РЅРµ РІ Core.Tests, РїРѕС‚РѕРјСѓ С‡С‚Рѕ РІРёРґРёС‚ РІСЃРµ СЃР±РѕСЂРєРё СЂРµС€РµРЅРёСЏ:
/// СЃСЃС‹Р»РєР° С‚РѕР»СЊРєРѕ РЅР° СЏРґСЂРѕ вЂ” Рё Assembly.Load РЅРµ РЅР°Р№РґС‘С‚ СЂСЏРґРѕРј РЅРё TrayIcon, РЅРё
/// РїСЂРёР»РѕР¶РµРЅРёРµ, Р° РёРјРµРЅРЅРѕ РёС… РіСЂР°РЅРёС†С‹ РјС‹ Рё РїСЂРѕРІРµСЂСЏРµРј.
/// </summary>
public class ModuleBoundaryTests
{
    /// <summary>Р’СЃРµ РјРѕРґСѓР»Рё Рё РёРЅС„СЂР°СЃС‚СЂСѓРєС‚СѓСЂР° СЂРµС€РµРЅРёСЏ (РёРјРµРЅР° СЃР±РѕСЂРѕРє).</summary>
    private static readonly string[] AllModules =
    [
        // РњРѕРґСѓР»Рё РїСЂРµРґРјРµС‚РЅС‹С… РѕР±Р»Р°СЃС‚РµР№
        "SoundMeeter.Audio.Contract",
        "SoundMeeter.Audio.Contract.Implementation",
        "SoundMeeter.Audio.Dsp",
        "SoundMeeter.Audio.UseCases",
        "SoundMeeter.Midi.Contract",
        "SoundMeeter.Midi.Contract.Implementation",
        "SoundMeeter.Midi.UseCases",
        "SoundMeeter.TextToSpeech.Contract",
        "SoundMeeter.TextToSpeech.Contract.Implementation",
        "SoundMeeter.TextToSpeech.UseCases",
        "SoundMeeter.Twitch.Contract",
        "SoundMeeter.Twitch.Contract.Implementation",
        "SoundMeeter.Twitch.UseCases",
        "SoundMeeter.Twitch.Entities",
        "SoundMeeter.Docking.Contract",
        "SoundMeeter.Docking.Contract.Implementation",
        "SoundMeeter.Docking.UseCases",
        "SoundMeeter.Docking.Entities",
        "SoundMeeter.AppRouting.Contract",
        "SoundMeeter.AppRouting.Contract.Implementation",
        "SoundMeeter.AppRouting.UseCases",
        "SoundMeeter.Update.Contract",
        "SoundMeeter.Update.Contract.Implementation",
        "SoundMeeter.Update.UseCases",
        "SoundMeeter.Update.Entities",
        "SoundMeeter.StartUp.Contract",
        "SoundMeeter.StartUp.Contract.Implementation",
        "SoundMeeter.StartUp.UseCases",
        "SoundMeeter.TrayIcon.Contract",
        "SoundMeeter.TrayIcon.UseCases",
        "SoundMeeter.ChangeLanguage.Contract",
        "SoundMeeter.ChangeLanguage.UseCases",
        // РРЅС„СЂР°СЃС‚СЂСѓРєС‚СѓСЂР°
        "SoundMeeter.Infrastructure.Logger",
        "SoundMeeter.Infrastructure.Settings",
        "SoundMeeter.Infrastructure.Clipboard",
        "SoundMeeter.Infrastructure.Dispatcher",
        "SoundMeeter.Infrastructure.UiTimer",
        // Framework
        "SoundMeeter.Framework.Utils",
        "SoundMeeter.Framework.Base",
    ];

    /// <summary>РЎР»РѕР№ СЃ ViewModels: РІСЃС‘, С‡С‚Рѕ Composition Root СЃРѕР±РёСЂР°РµС‚ РІ РїСЂРёР»РѕР¶РµРЅРёРµ.</summary>
    private const string CoreAssembly = "SoundMeeter.Core.UseCases";

    /// <summary>РљРѕРЅС‚СЂР°РєС‚ СЏРґСЂР°: РјРѕРґРµР»Рё РґР°РЅРЅС‹С… Рё РёРЅС‚РµСЂС„РµР№СЃС‹, РѕР±С‰РёРµ РґР»СЏ РІСЃРµС… РјРѕРґСѓР»РµР№.</summary>
    private const string CoreContract = "SoundMeeter.Core.Contract";

    /// <summary>
    /// РЎР±РѕСЂРєР° РїСЂРёР»РѕР¶РµРЅРёСЏ. РРјСЏ вЂ” <c>SoundMeeter</c>, Р° РЅРµ SoundMeeter.App:
    /// РёРјСЏ exe РІС…РѕРґРёС‚ РІ РєРѕРЅС‚СЂР°РєС‚ portable-СЂРµР»РёР·Р° (UpdateApplier РёС‰РµС‚
    /// SoundMeeter.exe), РїРѕСЌС‚РѕРјСѓ РїРµСЂРµРёРјРµРЅРѕРІР°РЅРёРµ Р·Р°РґР°С‡Рё РЅРµ Р±С‹Р»Рѕ.
    /// </summary>
    private const string AppAssembly = "SoundMeeter";

    /// <summary>
    /// UI-only СЃР±РѕСЂРєРё: Р·РЅР°С‡РѕРє РІ С‚СЂРµРµ (WinForms NotifyIcon) Рё РѕР±С‰Р°СЏ Р±РёР±Р»РёРѕС‚РµРєР°
    /// СЃС‚РёР»РµР№. РЇРґСЂРѕ Рё РјРѕРґСѓР»Рё Рѕ РЅРёС… РЅРёС‡РµРіРѕ РЅРµ Р·РЅР°СЋС‚ вЂ” СЌС‚Рѕ СѓСЂРѕРІРµРЅСЊ РїСЂРёР»РѕР¶РµРЅРёСЏ.
    /// </summary>
    private static readonly string[] UiOnlyAssemblies =
    [
        "SoundMeeter.TrayIcon.UseCases",
        "StreamerTools.Style",
    ];

    private static string[] References(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

    /// <summary>
    /// РЎСЃС‹Р»РєРё СЃР±РѕСЂРєРё, РµСЃР»Рё РѕРЅР° РµСЃС‚СЊ СЂСЏРґРѕРј СЃ С‚РµСЃС‚Р°РјРё. РџСѓСЃС‚С‹Рµ РїСЂРѕРµРєС‚С‹-Р·Р°РіР»СѓС€РєРё
    /// (Contract.Implementation Р±РµР· РєРѕРґР°, Framework Р±РµР· С„Р°Р№Р»РѕРІ) РІ РІС‹С…РѕРґ РЅРµ
    /// РїРѕРїР°РґР°СЋС‚ вЂ” РёС… РїСЂРѕРїСѓСЃРєР°РµРј, Р° РЅРµ РїР°РґР°РµРј.
    /// </summary>
    private static string[] ReferencesIfPresent(string assemblyName)
    {
        try
        {
            return References(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Р’СЃРµ СЃР±РѕСЂРєРё SoundMeeter.*, РґРѕСЃС‚РёР¶РёРјС‹Рµ РёР· РєРѕСЂРЅСЏ РїРѕ С†РµРїРѕС‡РєРµ СЃСЃС‹Р»РѕРє: РїСЂСЏРјР°СЏ
    /// СЃСЃС‹Р»РєР° РЅР° UI-РјРѕРґСѓР»СЊ РІРёРґРЅР° РІ GetReferencedAssemblies, Р° СЃРїСЂСЏС‚Р°РЅРЅР°СЏ РЅР° РґРІР°
    /// СѓСЂРѕРІРЅСЏ РіР»СѓР±Р¶Рµ вЂ” РЅРµС‚.
    /// </summary>
    private static string[] GraphOf(string rootAssembly)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal) { rootAssembly };
        var queue = new Queue<Assembly>();
        queue.Enqueue(Assembly.Load(rootAssembly));

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                var name = reference.Name ?? string.Empty;
                if (!name.StartsWith("SoundMeeter", StringComparison.Ordinal)) continue;
                if (!reachable.Add(name)) continue;

                queue.Enqueue(Assembly.Load(reference));
            }
        }

        return reachable.ToArray();
    }

    [Fact]
    public void NoModuleDependsOnTheApplication()
    {
        // РћР±СЂР°С‚РЅР°СЏ СЃСЃС‹Р»РєР° РЅР° РїСЂРёР»РѕР¶РµРЅРёРµ РґРµР»Р°РµС‚ РјРѕРґСѓР»СЊ РЅРµРїРµСЂРµРЅРѕСЃРёРјС‹Рј: РІС‹РЅРµСЃС‚Рё РµРіРѕ
        // РѕР±СЂР°С‚РЅРѕ, РЅРµ Р·Р°С‚Р°С‰РёРІ Р·Р° СЃРѕР±РѕР№ UI, СѓР¶Рµ РЅРµР»СЊР·СЏ.
        foreach (var module in AllModules)
            Assert.DoesNotContain(AppAssembly, ReferencesIfPresent(module));
    }

    [Fact]
    public void CoreDoesNotDependOnTheApplication()
    {
        // Р РµР±СЂРѕ App в†’ Core РѕР±СЏР·Р°РЅРѕ Р±С‹С‚СЊ РµРґРёРЅСЃС‚РІРµРЅРЅС‹Рј. РћР±СЂР°С‚РЅРѕРµ СЃРґРµР»Р°Р»Рѕ Р±С‹ СЏРґСЂРѕ
        // С‡Р°СЃС‚СЊСЋ РїСЂРёР»РѕР¶РµРЅРёСЏ: РїРµСЂРµРЅРµСЃС‚Рё Р»РѕРіРёРєСѓ РІ РґСЂСѓРіРѕР№ С…РѕСЃС‚ РЅРµРІРѕР·РјРѕР¶РЅРѕ.
        Assert.DoesNotContain(AppAssembly, References(CoreAssembly));
    }

    [Fact]
    public void BaseDependsOnNothingOfItsOwn()
    {
        // Logger Р»РµР¶РёС‚ РІ РѕСЃРЅРѕРІР°РЅРёРё РіСЂР°С„Р°: РµСЃР»Рё РЅР°С‡РЅС‘С‚ СЃСЃС‹Р»Р°С‚СЊСЃСЏ РЅР° РїСЂРёРєР»Р°РґРЅРѕР№
        // РјРѕРґСѓР»СЊ, С†РёРєР» Р·Р°РјРєРЅС‘С‚СЃСЏ Рё СЂР°Р·РґРµР»РµРЅРёРµ РїРµСЂРµСЃС‚Р°РЅРµС‚ СЃСѓС‰РµСЃС‚РІРѕРІР°С‚СЊ С„Р°РєС‚РёС‡РµСЃРєРё.
        Assert.DoesNotContain(CoreAssembly, References("SoundMeeter.Infrastructure.Logger"));
        Assert.DoesNotContain(AppAssembly, References("SoundMeeter.Infrastructure.Logger"));
        Assert.DoesNotContain(CoreAssembly, References("SoundMeeter.ChangeLanguage.UseCases"));
        Assert.DoesNotContain(AppAssembly, References("SoundMeeter.ChangeLanguage.UseCases"));
    }

    [Fact]
    public void CoreDoesNotDependOnUiModules()
    {
        // РЇРґСЂРѕ РЅРµ Р·РЅР°С‡РѕРє РІ С‚СЂРµРµ Рё РЅРµ СЃС‚РёР»Рё. РџСЂРѕРІРµСЂСЏРµРј РїРѕ РІСЃРµРјСѓ РіСЂР°С„Сѓ СЏРґСЂР°:
        // UI-СЃР±РѕСЂРєР° РјРѕРіР»Р° Р±С‹ РїСЂРёРµС…Р°С‚СЊ С‡РµСЂРµР· С‚СЂР°РЅР·РёС‚РёРІРЅСѓСЋ СЃСЃС‹Р»РєСѓ РјРѕРґСѓР»СЏ.
        var graph = GraphOf(CoreAssembly);

        foreach (var ui in UiOnlyAssemblies)
            Assert.DoesNotContain(ui, graph);
    }

    [Fact]
    public void ApplicationDependsOnCore()
    {
        // Р‘РµР· СЌС‚РѕРіРѕ СЃР±РѕСЂРєР° РјРѕР¶РµС‚ РѕСЃС‚Р°РІР°С‚СЊСЃСЏ В«Р·РµР»С‘РЅРѕР№В» РїСЂРё СЂР°Р·РґРµР»РµРЅРёРё, РіРґРµ СЏРґСЂРѕ
        // РµСЃС‚СЊ, РЅРѕ РїСЂРёР»РѕР¶РµРЅРёРµ РµРіРѕ РЅРµ РёСЃРїРѕР»СЊР·СѓРµС‚ (СЏРґСЂРѕ РјРµСЂС‚РІРѕ).
        Assert.Contains(CoreAssembly, References(AppAssembly));
    }

    [Fact]
    public void ApplicationIsAnApplicationAndCoreIsALibrary()
    {
        // РўРѕС‡РєР° РІС…РѕРґР° Рё UseWPF РїСЂРёРЅР°РґР»РµР¶Р°С‚ РѕР±РѕР»РѕС‡РєРµ. РЇРґСЂРѕ вЂ” Р±РёР±Р»РёРѕС‚РµРєР°.
        Assert.NotNull(Assembly.Load(AppAssembly).EntryPoint);
        Assert.Null(Assembly.Load(CoreAssembly).EntryPoint);
        Assert.Null(Assembly.Load(CoreContract).EntryPoint);
    }

    [Fact]
    public void UiModulesAreNotVisibleToModules()
    {
        // Р—РЅР°С‡РѕРє РІ С‚СЂРµРµ вЂ” UI-РјРѕРґСѓР»СЊ СѓСЂРѕРІРЅСЏ РїСЂРёР»РѕР¶РµРЅРёСЏ: РЅРё РѕРґРёРЅ РјРѕРґСѓР»СЊ Рё СЏРґСЂРѕ РЅРµ
        // РґРѕР»Р¶РЅС‹ СЃСЃС‹Р»Р°С‚СЊСЃСЏ РЅР° РЅРµРіРѕ. WPF/WinForms РІ РјРѕРґСѓР»СЏС… Р·Р°РїСЂРµС‰РµРЅС‹.
        foreach (var module in AllModules)
            Assert.DoesNotContain("SoundMeeter.TrayIcon.UseCases", ReferencesIfPresent(module));
    }
}

