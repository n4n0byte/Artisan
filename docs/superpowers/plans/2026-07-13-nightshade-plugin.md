# Nightshade Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package the complete Artisan implementation as an independently installable Dalamud plugin named Nightshade.

**Architecture:** Preserve the existing C# implementation and namespaces, but use a separate assembly, Dalamud internal name, manifest, debug deployment directory, and release ZIP. This lets Dalamud install Nightshade alongside Artisan.

**Tech Stack:** .NET 10, Dalamud.NET.Sdk, DalamudPackager, xUnit.

## Global Constraints

- The display name and internal name are `Nightshade`.
- The release artifact is `Artisan/bin/Release/Nightshade/latest.zip` and contains `Nightshade.json`.
- The existing Artisan implementation remains functionally intact.

---

### Task 1: Define the Nightshade package identity

**Files:**
- Create: `Artisan/Nightshade.json`
- Create: `Artisan.Tests/NightshadePackagingTests.cs`

**Interfaces:**
- Consumes: DalamudPackager's manifest convention.
- Produces: a manifest and a regression test for the Nightshade package identity.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Artisan.Tests;

public class NightshadePackagingTests
{
    [Fact]
    public void ReleasePackageContainsNightshadeManifest()
    {
        using var archive = ZipFile.OpenRead("../Artisan/bin/Release/Nightshade/latest.zip");
        var entry = archive.GetEntry("Nightshade.json");
        Assert.NotNull(entry);
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        Assert.Equal("Nightshade", document.RootElement.GetProperty("Name").GetString());
        Assert.Equal("Nightshade", document.RootElement.GetProperty("InternalName").GetString());
    }
}
```

- [ ] **Step 2: Verify the test is red**

Run: `dotnet test Artisan.Tests/Artisan.Tests.csproj --filter FullyQualifiedName~NightshadePackagingTests --no-restore`

Expected: FAIL because the Nightshade ZIP has not yet been generated.

- [ ] **Step 3: Create the manifest**

Create `Artisan/Nightshade.json` with this content:

```json
{
  "Author": "Taurenkey (Puni.sh)",
  "Name": "Nightshade",
  "Punchline": "A 'simple' crafting plugin.",
  "Description": "The all-in-one crafting plugin.",
  "InternalName": "Nightshade",
  "ApplicableVersion": "any",
  "IconUrl": "https://s3.puni.sh/media/plugin/6/icon-3h8wd5b9qr.png",
  "Tags": ["crafting"],
  "RepoUrl": "https://github.com/PunishXIV/Artisan",
  "DalamudApiLevel": 15
}
```

- [ ] **Step 4: Commit**

Run: `git add Artisan/Nightshade.json Artisan.Tests/NightshadePackagingTests.cs; git commit -m "test: define Nightshade package identity"`

### Task 2: Create separate Nightshade build artifacts

**Files:**
- Modify: `Artisan/Artisan.csproj:14-47`

**Interfaces:**
- Consumes: `Artisan/Nightshade.json`.
- Produces: `Nightshade.dll`, a development deployment directory named `Nightshade`, and `Nightshade/latest.zip`.

- [ ] **Step 1: Update the build properties**

Set these existing properties:

```xml
<DalamudDevPlugins>$(appdata)\XIVLauncher\devPlugins\Nightshade\</DalamudDevPlugins>
<AssemblyName>Nightshade</AssemblyName>
<RootNamespace>Artisan</RootNamespace>
<PackageId>Nightshade</PackageId>
<Product>Nightshade</Product>
```

- [ ] **Step 2: Build the release ZIP**

Run: `dotnet build Artisan/Artisan.csproj -c Release --no-restore`

Expected: `Artisan/bin/Release/Nightshade/latest.zip` exists.

- [ ] **Step 3: Verify green**

Run: `dotnet test Artisan.Tests/Artisan.Tests.csproj --filter FullyQualifiedName~NightshadePackagingTests --no-restore`

Expected: PASS, 1 test.

- [ ] **Step 4: Commit**

Run: `git add Artisan/Artisan.csproj Artisan/Nightshade.json Artisan.Tests/NightshadePackagingTests.cs; git commit -m "feat: package Artisan fork as Nightshade"`

### Task 3: Verify local Dalamud deployment

**Files:**
- Verify: `Artisan/bin/Release/Nightshade/latest.zip`
- Verify: `%APPDATA%/XIVLauncher/devPlugins/Nightshade/Nightshade.dll`

**Interfaces:**
- Consumes: Task 2 build configuration.
- Produces: artifacts that can be used by a custom repository and loaded as a separate development plugin.

- [ ] **Step 1: Build the debug deployment**

Run: `dotnet build Artisan/Artisan.csproj --no-restore`

Expected: `%APPDATA%/XIVLauncher/devPlugins/Nightshade/Nightshade.dll` exists.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test Artisan.Tests/Artisan.Tests.csproj --no-restore`

Expected: PASS, 2 tests.

- [ ] **Step 3: Inspect ZIP manifest**

Run: `Expand-Archive -LiteralPath Artisan/bin/Release/Nightshade/latest.zip -DestinationPath $env:TEMP/nightshade-package -Force; Get-Content -Raw $env:TEMP/nightshade-package/Nightshade.json`

Expected: both `Name` and `InternalName` are `Nightshade`.

## Self-Review

- Task 1 creates the independent manifest and its regression test.
- Task 2 assigns Nightshade identities to the assembly, package, and development output.
- Task 3 verifies both release and development-plugin artifacts.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-13-nightshade-plugin.md`. Two execution options:

1. Subagent-Driven (recommended) - I dispatch a fresh subagent per task, review between tasks, fast iteration

2. Inline Execution - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
