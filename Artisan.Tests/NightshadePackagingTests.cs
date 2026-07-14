using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Artisan.Tests;

public class NightshadePackagingTests
{
    [Fact]
    public void ReleasePackageContainsNightshadeManifest()
    {
        var packagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Artisan/bin/Release/Nightshade/latest.zip"));
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("Nightshade.json");
        Assert.NotNull(entry);
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        Assert.Equal("Nightshade", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("Nightshade", document.RootElement.GetProperty("InternalName").GetString());
    }
}
