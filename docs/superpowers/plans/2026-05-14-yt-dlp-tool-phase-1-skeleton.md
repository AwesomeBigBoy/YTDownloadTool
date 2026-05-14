# Phase 1 · Skeleton

**Goal:** Create the .NET 8 solution with four projects, lock dependencies, prove NativeAOT publish works, and verify the shell window opens.

**Prerequisites:** Repo prerequisites in `2026-05-14-yt-dlp-tool.md` are done (`git init`, `.gitignore`, initial commit).

---

### Task 1.1: Create solution and project folders

**Files:**
- Create: `YtDlpTool.sln`

- [ ] **Step 1: Create solution file**

```powershell
dotnet new sln -n YtDlpTool
```

- [ ] **Step 2: Verify**

```powershell
Test-Path YtDlpTool.sln
```
Expected: `True`

- [ ] **Step 3: Commit**

```powershell
git add YtDlpTool.sln
git commit -m "chore: create solution file"
```

---

### Task 1.2: Create Domain class library

**Files:**
- Create: `src/YtDlpTool.Domain/YtDlpTool.Domain.csproj`

- [ ] **Step 1: Create project**

```powershell
dotnet new classlib -n YtDlpTool.Domain -o src/YtDlpTool.Domain --framework net8.0
dotnet sln add src/YtDlpTool.Domain/YtDlpTool.Domain.csproj
```

- [ ] **Step 2: Replace generated `Class1.cs` removal and configure csproj**

Delete the auto-generated stub:
```powershell
Remove-Item src/YtDlpTool.Domain/Class1.cs -Force
```

Edit `src/YtDlpTool.Domain/YtDlpTool.Domain.csproj` to look exactly like this:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsAotCompatible>true</IsAotCompatible>
    <RestoreLockedMode>true</RestoreLockedMode>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Build to verify**

```powershell
dotnet build src/YtDlpTool.Domain/YtDlpTool.Domain.csproj
```
Expected: `Build succeeded.` with 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/ YtDlpTool.sln
git commit -m "feat(domain): scaffold domain class library"
```

---

### Task 1.3: Create Process class library

**Files:**
- Create: `src/YtDlpTool.Process/YtDlpTool.Process.csproj`

- [ ] **Step 1: Create project**

```powershell
dotnet new classlib -n YtDlpTool.Process -o src/YtDlpTool.Process --framework net8.0
Remove-Item src/YtDlpTool.Process/Class1.cs -Force
dotnet sln add src/YtDlpTool.Process/YtDlpTool.Process.csproj
dotnet add src/YtDlpTool.Process/YtDlpTool.Process.csproj reference src/YtDlpTool.Domain/YtDlpTool.Domain.csproj
```

- [ ] **Step 2: Configure csproj**

Edit `src/YtDlpTool.Process/YtDlpTool.Process.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsAotCompatible>true</IsAotCompatible>
    <RestoreLockedMode>true</RestoreLockedMode>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\YtDlpTool.Domain\YtDlpTool.Domain.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Build to verify**

```powershell
dotnet build src/YtDlpTool.Process/YtDlpTool.Process.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Process/ YtDlpTool.sln
git commit -m "feat(process): scaffold process layer class library"
```

---

### Task 1.4: Create WPF app project

**Files:**
- Create: `src/YtDlpTool/YtDlpTool.csproj`
- Create: `src/YtDlpTool/App.xaml`
- Create: `src/YtDlpTool/App.xaml.cs`
- Create: `src/YtDlpTool/MainWindow.xaml`
- Create: `src/YtDlpTool/MainWindow.xaml.cs`

- [ ] **Step 1: Create project**

```powershell
dotnet new wpf -n YtDlpTool -o src/YtDlpTool --framework net8.0
dotnet sln add src/YtDlpTool/YtDlpTool.csproj
dotnet add src/YtDlpTool/YtDlpTool.csproj reference src/YtDlpTool.Domain/YtDlpTool.Domain.csproj
dotnet add src/YtDlpTool/YtDlpTool.csproj reference src/YtDlpTool.Process/YtDlpTool.Process.csproj
```

- [ ] **Step 2: Configure csproj for NativeAOT and Windows target**

Replace `src/YtDlpTool/YtDlpTool.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>YtDlpTool</RootNamespace>
    <AssemblyName>YtDlpTool</AssemblyName>
    <ApplicationIcon></ApplicationIcon>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishAot>true</PublishAot>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>
    <SelfContained>true</SelfContained>
    <InvariantGlobalization>false</InvariantGlobalization>
    <RestoreLockedMode>true</RestoreLockedMode>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\YtDlpTool.Domain\YtDlpTool.Domain.csproj" />
    <ProjectReference Include="..\YtDlpTool.Process\YtDlpTool.Process.csproj" />
  </ItemGroup>
</Project>
```

> **Note:** `TargetPlatformMinVersion=10.0.17763.0` is Windows 10 1809, the spec's minimum target. `PublishAot=true` is the NativeAOT switch. `UseWindowsForms=true` is required for `FolderBrowserDialog`.

- [ ] **Step 3: Verify default debug build runs**

```powershell
dotnet build src/YtDlpTool/YtDlpTool.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool/ YtDlpTool.sln
git commit -m "feat(app): scaffold WPF app with NativeAOT enabled"
```

---

### Task 1.5: Create test projects

**Files:**
- Create: `tests/YtDlpTool.Domain.Tests/YtDlpTool.Domain.Tests.csproj`
- Create: `tests/YtDlpTool.Process.Tests/YtDlpTool.Process.Tests.csproj`

- [ ] **Step 1: Domain tests project**

```powershell
dotnet new xunit -n YtDlpTool.Domain.Tests -o tests/YtDlpTool.Domain.Tests --framework net8.0
Remove-Item tests/YtDlpTool.Domain.Tests/UnitTest1.cs -Force
dotnet sln add tests/YtDlpTool.Domain.Tests/YtDlpTool.Domain.Tests.csproj
dotnet add tests/YtDlpTool.Domain.Tests/YtDlpTool.Domain.Tests.csproj reference src/YtDlpTool.Domain/YtDlpTool.Domain.csproj
```

- [ ] **Step 2: Process tests project**

```powershell
dotnet new xunit -n YtDlpTool.Process.Tests -o tests/YtDlpTool.Process.Tests --framework net8.0
Remove-Item tests/YtDlpTool.Process.Tests/UnitTest1.cs -Force
dotnet sln add tests/YtDlpTool.Process.Tests/YtDlpTool.Process.Tests.csproj
dotnet add tests/YtDlpTool.Process.Tests/YtDlpTool.Process.Tests.csproj reference src/YtDlpTool.Process/YtDlpTool.Process.csproj
dotnet add tests/YtDlpTool.Process.Tests/YtDlpTool.Process.Tests.csproj reference src/YtDlpTool.Domain/YtDlpTool.Domain.csproj
```

- [ ] **Step 3: Configure both test csprojs**

Replace `tests/YtDlpTool.Domain.Tests/YtDlpTool.Domain.Tests.csproj` and `tests/YtDlpTool.Process.Tests/YtDlpTool.Process.Tests.csproj` content with the same template (only the `ProjectReference` lines differ):

For Domain tests:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RestoreLockedMode>true</RestoreLockedMode>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\YtDlpTool.Domain\YtDlpTool.Domain.csproj" />
  </ItemGroup>
</Project>
```

For Process tests, replace only the `<ItemGroup>` with project references:
```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\YtDlpTool.Domain\YtDlpTool.Domain.csproj" />
    <ProjectReference Include="..\..\src\YtDlpTool.Process\YtDlpTool.Process.csproj" />
  </ItemGroup>
```

- [ ] **Step 4: Add a smoke test to Domain tests so the test project compiles and runs**

Create `tests/YtDlpTool.Domain.Tests/SmokeTests.cs`:

```csharp
namespace YtDlpTool.Domain.Tests;

public class SmokeTests
{
    [Fact]
    public void Truth_IsTrue()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/YtDlpTool.Domain.Tests.csproj
```
Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0`

- [ ] **Step 6: Commit**

```powershell
git add tests/ YtDlpTool.sln
git commit -m "test: scaffold xUnit test projects with smoke test"
```

---

### Task 1.6: Add NuGet packages to Domain (locked)

**Files:**
- Modify: `src/YtDlpTool.Domain/YtDlpTool.Domain.csproj`
- Create: `src/YtDlpTool.Domain/packages.lock.json` (auto-generated)

- [ ] **Step 1: Add NSec.Cryptography for Ed25519**

```powershell
dotnet add src/YtDlpTool.Domain/YtDlpTool.Domain.csproj package NSec.Cryptography --version 24.4.0
```

- [ ] **Step 2: Restore with lock**

```powershell
dotnet restore src/YtDlpTool.Domain/YtDlpTool.Domain.csproj --force-evaluate
```

- [ ] **Step 3: Verify lock file exists**

```powershell
Test-Path src/YtDlpTool.Domain/packages.lock.json
```
Expected: `True`

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/YtDlpTool.Domain.csproj src/YtDlpTool.Domain/packages.lock.json
git commit -m "feat(domain): add NSec.Cryptography for Ed25519, lock packages"
```

---

### Task 1.7: Add NuGet packages to WPF app (locked)

**Files:**
- Modify: `src/YtDlpTool/YtDlpTool.csproj`
- Create: `src/YtDlpTool/packages.lock.json` (auto)

- [ ] **Step 1: Add CommunityToolkit packages**

```powershell
dotnet add src/YtDlpTool/YtDlpTool.csproj package CommunityToolkit.Mvvm --version 8.3.2
dotnet add src/YtDlpTool/YtDlpTool.csproj package CommunityToolkit.WinUI.Notifications --version 7.1.3
```

- [ ] **Step 2: Restore with lock for all projects**

```powershell
dotnet restore --force-evaluate
```

- [ ] **Step 3: Verify all lock files exist**

```powershell
Get-ChildItem -Recurse -Filter packages.lock.json | Select-Object FullName
```
Expected: at least 4 lock files (Domain, Process, App, both test projects).

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool/ tests/ src/YtDlpTool.Process/
git commit -m "feat(app): add MVVM toolkit and notifications, lock packages"
```

---

### Task 1.8: Write the MainWindow shell (placeholder, real shell in Phase 7)

**Files:**
- Modify: `src/YtDlpTool/MainWindow.xaml`
- Modify: `src/YtDlpTool/MainWindow.xaml.cs`
- Modify: `src/YtDlpTool/App.xaml`

> The default scaffolded MainWindow opens a blank 800x450 window. We're going to make it a clearly recognizable Phase-1 placeholder so manual smoke tests confirm the right binary is running. Phase 7 replaces this entirely.

- [ ] **Step 1: Edit `MainWindow.xaml`**

Replace contents:

```xml
<Window x:Class="YtDlpTool.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="YtDlpTool · Phase 1 Skeleton"
        Width="900" Height="600" MinWidth="900" MinHeight="600"
        WindowStartupLocation="CenterScreen"
        Background="#1A1A24">
    <Grid>
        <TextBlock Foreground="#E8E8F0"
                   FontFamily="Microsoft JhengHei UI, Segoe UI"
                   FontSize="24"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   Text="YtDlpTool — Phase 1 shell. Real UI lives in Phase 7." />
    </Grid>
</Window>
```

- [ ] **Step 2: Edit `MainWindow.xaml.cs`**

Replace contents:

```csharp
using System.Windows;

namespace YtDlpTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Edit `App.xaml`**

Replace contents:

```xml
<Application x:Class="YtDlpTool.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml" />
```

- [ ] **Step 4: Build**

```powershell
dotnet build src/YtDlpTool/YtDlpTool.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 5: Run debug build, then close**

```powershell
dotnet run --project src/YtDlpTool/YtDlpTool.csproj
```
Expected: a 900×600 dark window opens with the placeholder text. Close it (Alt+F4 or X).

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool/MainWindow.xaml src/YtDlpTool/MainWindow.xaml.cs src/YtDlpTool/App.xaml
git commit -m "feat(app): phase-1 placeholder main window"
```

---

### Task 1.9: NativeAOT publish smoke test

**Files:** (none new, only verification)

- [ ] **Step 1: Publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: build succeeds. Note any AOT-related warnings (`IL2026`, `IL3050` — trimming/AOT analyzers). On a fresh WPF app these should be **zero** because WPF on .NET 8 has working AOT support. If any appear, stop and surface them.

- [ ] **Step 2: Locate output exe**

```powershell
Get-ChildItem src/YtDlpTool/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/YtDlpTool.exe
```
Expected: exists, size between 10–25 MB.

- [ ] **Step 3: Run the published exe**

```powershell
Start-Process src/YtDlpTool/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/YtDlpTool.exe
```
Expected: same dark window opens. Close it.

- [ ] **Step 4: Commit (nothing to add, but tag this milestone)**

```powershell
git tag phase-1-aot-verified
```

---

### Task 1.10: Pre-create empty folders referenced in spec

**Files:**
- Create: `src/YtDlpTool/Views/.gitkeep`
- Create: `src/YtDlpTool/ViewModels/.gitkeep`
- Create: `src/YtDlpTool/Dialogs/.gitkeep`
- Create: `src/YtDlpTool/Resources/.gitkeep`
- Create: `src/YtDlpTool/Interop/.gitkeep`
- Create: `src/YtDlpTool.Domain/Models/.gitkeep`
- Create: `src/YtDlpTool.Domain/Services/.gitkeep`
- Create: `src/YtDlpTool.Domain/Security/.gitkeep`
- Create: `src/YtDlpTool.Domain/Logging/.gitkeep`
- Create: `src/YtDlpTool.Domain/Updates/.gitkeep`
- Create: `src/YtDlpTool.Domain/Persistence/.gitkeep`
- Create: `build/.gitkeep`
- Create: `.github/workflows/.gitkeep`

- [ ] **Step 1: Create all empty folders with placeholder files**

```powershell
$dirs = @(
  "src/YtDlpTool/Views",
  "src/YtDlpTool/ViewModels",
  "src/YtDlpTool/Dialogs",
  "src/YtDlpTool/Resources",
  "src/YtDlpTool/Interop",
  "src/YtDlpTool.Domain/Models",
  "src/YtDlpTool.Domain/Services",
  "src/YtDlpTool.Domain/Security",
  "src/YtDlpTool.Domain/Logging",
  "src/YtDlpTool.Domain/Updates",
  "src/YtDlpTool.Domain/Persistence",
  "build",
  ".github/workflows"
)
foreach ($d in $dirs) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
  New-Item -ItemType File -Force -Path "$d/.gitkeep" | Out-Null
}
```

- [ ] **Step 2: Commit**

```powershell
git add .
git commit -m "chore: pre-create folder structure"
```

---

## Phase 1 complete gate

- [ ] `dotnet build` of solution: succeeds with 0 warnings
- [ ] `dotnet test`: 1 test passes (smoke test)
- [ ] `dotnet publish ... /p:PublishAot=true`: succeeds, exe runs
- [ ] All five projects (Domain, Process, App, Domain.Tests, Process.Tests) in solution
- [ ] `git log --oneline` shows ~9 commits in this phase + tag `phase-1-aot-verified`

Proceed to Phase 2.
