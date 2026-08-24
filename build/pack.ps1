<#
.SYNOPSIS
    Builds Chapter and packages it into an installer and an update feed.

.DESCRIPTION
    The one path to a shippable Chapter, used by a human and by
    .github/workflows/release.yml alike. A release built two different ways is a release
    whose failures cannot be reproduced, so CI runs this script rather than repeating its
    steps in YAML.

    Four steps, in an order that matters:

      1. npm ci        — the lockfile, not whatever is installed. A release built against
                         a drifted node_modules is not the release the lockfile describes.
      2. npm run build — dotnet does not do this. Without it the app ships with no UI, and
                         the only warning is CHAPTER001 scrolling past in the build log.
      3. dotnet publish — self-contained, so a tester needs no .NET installed. Not trimmed:
                         Roslyn and WPF both resolve types by name and a trimmer removes
                         exactly those.
      4. vpk pack      — the installer, the full package, and a delta against the previous
                         release when one is present in the output directory.

.PARAMETER Version
    SemVer 2, e.g. 0.1.0-beta.2. Defaults to the version in Directory.Build.props, which is
    the source of truth — the app reports it in the help panel, so a version passed here
    that disagrees with it produces a build that misreports itself.

.PARAMETER OutputDir
    Where the packages land. Point it at a directory holding previous releases and vpk
    builds a delta against them; point it at an empty one and every update is a full
    download.

.EXAMPLE
    pwsh build/pack.ps1
    pwsh build/pack.ps1 -Version 0.1.0-beta.2 -ReleaseNotes notes.md
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDir = 'artifacts/releases',
    [string] $Runtime = 'win-x64',
    [string] $PublishDir = 'artifacts/publish',
    [string] $ReleaseNotes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($message) { Write-Host "`n=== $message" -ForegroundColor Cyan }

# --- version ---------------------------------------------------------------

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
    $prefix = $props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }
    $suffix = $props.Project.PropertyGroup.VersionSuffix | Where-Object { $_ }
    $Version = if ($suffix) { "$prefix-$suffix" } else { "$prefix" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a SemVer 2 version. Velopack will not parse it."
}

Step "Chapter $Version ($Runtime)"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk is not on PATH. Install it with: dotnet tool install -g vpk --version 1.2.0"
}

# --- front-end -------------------------------------------------------------

Step 'Front-end'
Push-Location (Join-Path $root 'src/Chapter.Web')
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }

    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm run build failed' }
} finally {
    Pop-Location
}

$dist = Join-Path $root 'src/Chapter.Web/dist/index.html'
if (-not (Test-Path $dist)) { throw "The front-end build produced no $dist." }

# --- backend ---------------------------------------------------------------

Step 'Publish'
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

dotnet publish src/Chapter.App/Chapter.App.csproj `
    -c Release -r $Runtime --self-contained true `
    -p:Version=$Version `
    -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The copy is an MSBuild target that runs after Publish, and a silent failure there ships an
# app whose window is blank. Cheaper to assert than to discover from a screenshot.
if (-not (Test-Path (Join-Path $PublishDir 'wwwroot/index.html'))) {
    throw 'The published output has no wwwroot. The CopyWebAssets target did not run.'
}

# --- package ---------------------------------------------------------------

Step 'Package'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$packArgs = @(
    'pack'
    '--packId', 'Chapter'
    '--packVersion', $Version
    '--packDir', $PublishDir
    '--packTitle', 'Chapter'
    '--packAuthors', 'letrquan'
    '--mainExe', 'Chapter.App.exe'
    '--icon', 'src/Chapter.App/chapter.ico'
    '--runtime', $Runtime
    '--outputDir', $OutputDir
)

if ($ReleaseNotes) { $packArgs += @('--releaseNotes', $ReleaseNotes) }

vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Step 'Done'
Get-ChildItem $OutputDir | Sort-Object Name | ForEach-Object {
    '{0,-46} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
