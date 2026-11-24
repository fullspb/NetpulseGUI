using System.Text.Json;
using NetpulseGUI.Core.Updates;
using Xunit;

namespace NetpulseGUI.Tests;

public class ReleaseParserTests
{
    private const string Release = """
    {
      "tag_name": "v1.2.0",
      "html_url": "https://github.com/fullspb/NetpulseGUI/releases/tag/v1.2.0",
      "draft": false,
      "prerelease": false,
      "assets": [
        { "name": "NetpulseGUI-1.2.0-setup.exe", "size": 100, "browser_download_url": "https://x/setup.exe", "digest": "sha256:ABCDEF" },
        { "name": "NetpulseGUI.exe", "size": 200, "browser_download_url": "https://x/NetpulseGUI.exe", "digest": null }
      ]
    }
    """;

    private static UpdateInfo? Parse(string json, string current, bool installed)
    {
        using var doc = JsonDocument.Parse(json);
        return ReleaseParser.Parse(doc.RootElement, Version.Parse(current), installed);
    }

    [Fact]
    public void InstalledCopyGetsInstallerWithDigest()
    {
        var u = Parse(Release, "1.1.0", installed: true)!;
        Assert.Equal(new Version(1, 2, 0), u.Version);
        Assert.True(u.IsInstaller);
        Assert.Equal("NetpulseGUI-1.2.0-setup.exe", u.AssetName);
        Assert.Equal("abcdef", u.Sha256);
        Assert.Equal(100, u.AssetSize);
    }

    [Fact]
    public void PortableCopyGetsExe()
    {
        var u = Parse(Release, "1.1.0", installed: false)!;
        Assert.False(u.IsInstaller);
        Assert.Equal("https://x/NetpulseGUI.exe", u.AssetUrl);
        Assert.Null(u.Sha256);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    public void SameOrOlderReleaseIsNotAnUpdate(string current) => Assert.Null(Parse(Release, current, true));

    [Fact]
    public void PrereleaseIsIgnored() =>
        Assert.Null(Parse(Release.Replace("\"prerelease\": false", "\"prerelease\": true"), "1.0.0", true));

    [Fact]
    public void FallsBackToOtherAssetWhenPreferredMissing()
    {
        var json = Release.Replace("\"NetpulseGUI.exe\"", "\"other.zip\"");
        var u = Parse(json, "1.0.0", installed: false)!;
        Assert.True(u.IsInstaller);
    }
}
