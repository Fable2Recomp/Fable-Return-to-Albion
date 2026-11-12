# Stages a Godot export into a runnable tree using multi-threaded robocopy delta sync.
#
# WHY THIS EXISTS: Godot's export puts resources in the .pck, but the native libraries are
# loaded by the OS through P/Invoke, and the loader cannot see inside a .pck. So
# refii_hostbridge, rexruntime, refii_xmadecode, refii_binkdecode and the rest have to be
# real files BESIDE the exported .exe. export_presets.cfg therefore excludes build/* from
# the pack, and this script puts the right architecture's closure next to the executable
# afterwards.
#
#   powershell -ExecutionPolicy Bypass -File packaging/windows/stage-godot-export.ps1 -Arch x86_64
#
# Run it after `godot --headless --export-release "Windows Desktop"` (or "Windows ARM64").

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

function Invoke-RobocopyChecked {
    param([string[]]$Arguments)
    & robocopy.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE ($($Arguments -join ' '))"
    }
}

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

# Copy any top-level loose DLLs from Godot/build/<arch> without wiping the exported .exe / data_*
# at the root of $ExportDir, then mirror bin/ while excluding bin/recompile (~600 MB, which is only
# ever consumed from build/<arch>/bin/recompile below).
Write-Host "Staging $Arch natives (incremental MT:16): $stagingRoot -> $ExportDir"
Invoke-RobocopyChecked @(
    $stagingRoot, $ExportDir, '*.*',
    '/MT:16', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
)

$stagingBin = Join-Path $stagingRoot 'bin'
$exportBin = Join-Path $ExportDir 'bin'
$stagingRecompile = Join-Path $stagingBin 'recompile'
if (Test-Path $stagingBin) {
    Invoke-RobocopyChecked @(
        $stagingBin, $exportBin,
        '/MIR', '/MT:16', '/R:2', '/W:1',
        '/XD', $stagingRecompile,
        '/NFL', '/NDL', '/NJH', '/NJS'
    )
}

# Engine_NativeLibraries searches build/<arch>/bin relative to the project directory, which
# in an exported game is the directory holding the executable.
$exportedBuild = Join-Path $ExportDir "build\$Arch"
New-Item -ItemType Directory -Force -Path $exportedBuild | Out-Null
Invoke-RobocopyChecked @(
    $stagingRoot, $exportedBuild,
    '/MIR', '/MT:16', '/R:2', '/W:1',
    '/NFL', '/NDL', '/NJH', '/NJS'
)

Write-Host "Staged. Contents of ${ExportDir}:"
Get-ChildItem $ExportDir | Select-Object -First 20 | ForEach-Object { "  $($_.Name)" }
