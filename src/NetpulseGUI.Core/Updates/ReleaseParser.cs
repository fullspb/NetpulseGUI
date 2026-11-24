using System.Text.Json;

namespace NetpulseGUI.Core.Updates;

/// <summary>Сведения о доступном обновлении (последний релиз на GitHub).</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string ReleaseUrl,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? Sha256,
    bool IsInstaller);

/// <summary>Разбор ответа GitHub API «последний релиз».</summary>
public static class ReleaseParser
{
    public const string PortableAssetName = "NetpulseGUI.exe";
    public const string InstallerSuffix = "-setup.exe";
    /// <summary>Портативная сборка без встроенного .NET (нужен установленный .NET Desktop Runtime), в zip-архиве.</summary>
    public const string LiteAssetName = "NetpulseGUI-lite.zip";

    /// <summary>
    /// Разбор ответа GitHub API (вынесен отдельно для тестируемости).
    /// <paramref name="lite"/> — предпочесть lite-архив: для lite-копии, а также для установленной копии,
    /// если в системе есть .NET Desktop Runtime (тогда вместо установщика ~74 МБ качается архив ~22 МБ).
    /// </summary>
    public static UpdateInfo? Parse(JsonElement release, Version current, bool installed, string repositoryUrl = "", bool lite = false)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= current) return null;

        JsonElement? installer = null, portable = null, liteAsset = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase)) installer = asset;
            else if (name.Equals(PortableAssetName, StringComparison.OrdinalIgnoreCase)) portable = asset;
            else if (name.Equals(LiteAssetName, StringComparison.OrdinalIgnoreCase)) liteAsset = asset;
        }

        // Lite-архив, если он предпочтителен и есть в релизе: exe заменяется на месте (и у установленной копии).
        var useLite = lite && liteAsset != null;
        if (useLite) portable = liteAsset;

        // Установленной версии нужен установщик, портативной — exe; если нужного файла нет, берём другой.
        var useInstaller = !useLite && (installed ? installer != null || portable == null : portable == null && installer != null);
        var chosen = useInstaller ? installer : portable;
        if (chosen is not { } a) return null;

        string? sha = null;
        if (a.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
        {
            var d = digest.GetString() ?? "";
            if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = d[7..].ToLowerInvariant();
        }

        return new UpdateInfo(
            version,
            tag,
            release.TryGetProperty("html_url", out var url) ? url.GetString() ?? repositoryUrl : repositoryUrl,
            a.GetProperty("name").GetString()!,
            a.GetProperty("browser_download_url").GetString()!,
            a.GetProperty("size").GetInt64(),
            sha,
            useInstaller);
    }
}
