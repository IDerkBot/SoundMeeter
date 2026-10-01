using System.Globalization;
using System.Reflection;

namespace SoundMeeter.Models;

/// <summary>
/// Версия приложения в semver-виде: три числа + необязательный пре-релизный суффикс.
/// Разбор терпим к тегам GitHub Releases: «v1.2.3», «1.2.3», «release-1.2.3»,
/// «1.2.3-beta.1», «1.2.3+abc1234» (суффикс от SourceLink отбрасывается).
/// Сравнение нужно, чтобы отличить релиз новее установленного от более старого.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Пре-релизный суффикс без ведущего дефиса. Пусто у обычного релиза.</summary>
    public string Prerelease { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public AppVersion(int major, int minor, int patch, string prerelease = "")
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? "";
    }

    /// <summary>
    /// Версия запущенной сборки. Берётся из AssemblyInformationalVersion
    /// (её задаёт свойство &lt;Version&gt; в .csproj — это то, что видно в теге релиза).
    /// </summary>
    public static AppVersion Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParse(informational, out var version) ? version : new AppVersion(0, 0, 0);
        }
    }

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = new AppVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();
        var i = 0;
        while (i < span.Length && !char.IsDigit(span[i])) i++;
        if (i >= span.Length) return false;

        Span<int> numbers = stackalloc int[3];
        var count = 0;
        while (i < span.Length && count < 3)
        {
            var start = i;
            while (i < span.Length && char.IsDigit(span[i])) i++;
            if (i > start && int.TryParse(span[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                numbers[count++] = value;

            if (i >= span.Length || span[i] != '.') break;
            i++;
        }

        // Пре-релизный суффикс по semver начинается с «-». Всё остальное после третьего
        // числа — мусор: «+commit» из SourceLink или лишняя компонента («1.2.3.9»).
        var rest = count == 3 ? span[i..] : ReadOnlySpan<char>.Empty;
        var prerelease = rest.Length > 0 && rest[0] == '-' ? rest[1..].Trim().ToString() : "";

        version = new AppVersion(
            count > 0 ? numbers[0] : 0,
            count > 1 ? numbers[1] : 0,
            count > 2 ? numbers[2] : 0,
            prerelease);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        // По semver обычный релиз новее любого пре-релиза той же версии.
        if (IsPrerelease != other.IsPrerelease) return other.IsPrerelease ? 1 : -1;
        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public bool Equals(AppVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    public static bool operator ==(AppVersion? left, AppVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(AppVersion? left, AppVersion? right) => !(left == right);

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
}
