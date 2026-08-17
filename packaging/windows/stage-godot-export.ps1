# Stages a Godot export into a runnable tree.
#
# WHY THIS EXISTS: Godot's export puts resources in the .pck, but the native libraries are
# loaded by the OS through P/Invoke, and the loader cannot see inside a .pck. So
# refii_hostbridge, rexruntime, refii_xmadecode, refii_binkdecode and the rest have to be
# real files BESIDE the exported .exe. export_presets.cfg therefore excludes build/* from
# the pack, and this script puts the right architecture's closure next to the executable
# afterwards.
#
#   pwsh packaging/windows/stage-godot-export.ps1 -Arch arm64
#
# Run it after `godot --headless --export-release "Windows ARM64"`.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x86_64', 'arm64')]
    [string]$Arch,

    # Defaults match export_presets.cfg's export_path.
    [string]$ExportDir,

    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$stagingRoot = Join-Path $RepoRoot "Godot\build\$Arch"
if (-not (Test-Path $stagingRoot)) {
    throw "No native staging for $Arch at $stagingRoot. Build refii for that architecture first " +
          "(cmake --build --preset win-$(if ($Arch -eq 'arm64') { 'arm64' } else { 'amd64' })-relwithdebinfo-netplay)."
}

if (-not $ExportDir) {
    $ExportDir = Join-Path $RepoRoot "out\export\$Arch"
}
if (-not (Test-Path $ExportDir)) {
    throw "No export at $ExportDir. Run the Godot export for this architecture first."
}

# The staging root already has the layout the runtime expects: loose DLLs at the top and
# the Godot-loaded natives under bin/. Copy it wholesale so nothing has to be enumerated
# here and kept in sync with the CMake staging lists.
Write-Host "Staging $Arch natives: $stagingRoot -> $ExportDir"
Copy-Item -Path (Join-Path $stagingRoot '*') -Destination $ExportDir -Recurse -Force

# Engine_NativeLibraries searches build/<arch>/bin relative to the project directory, which
# in an exported game is the directory holding the executable. Preserving that layout means
# the exported build resolves libraries exactly as the dev tree does.
$exportedBuild = Join-Path $ExportDir "build\$Arch"
New-Item -ItemType Directory -Force -Path $exportedBuild | Out-Null
Copy-Item -Path (Join-Path $stagingRoot '*') -Destination $exportedBuild -Recurse -Force

Write-Host "Staged. Contents of ${ExportDir}:"
Get-ChildItem $ExportDir | Select-Object -First 20 | ForEach-Object { "  $($_.Name)" }
