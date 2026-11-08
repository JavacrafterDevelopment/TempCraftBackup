# Builds TempCraftBackup.exe with the C# compiler that ships with Windows
# (.NET Framework 4.8's csc.exe) and packages it the way the Launcher installs
# and updates apps. Used locally and by the release workflow.
#
#   .\build.ps1 [-Out dist] [-MinGitRelease latest|<tag>] [-NoGit]
#
# Writes:
#   <Out>\TempCraftBackup\                     the portable folder, ready to run
#   <Out>\TempCraftBackup\git\                 portable Git (MinGit), for "Include git history"
#   <Out>\TempCraftBackup-windows-x64.zip      that folder zipped: what the Launcher downloads
#   <Out>\release-entry.json                   this release's entry for update.json
#
# Every file is listed in launcher-manifest.json with its size and SHA-256; the
# Launcher checks each one before installing anything.
#
# The git\ folder is MinGit, the minimal portable Git that the Git for Windows
# project (https://gitforwindows.org, linked from git-scm.com) publishes for apps
# to bundle. It's downloaded from that project's GitHub release (the latest one
# unless -MinGitRelease names a tag, e.g. v2.56.0.windows.4), checked against the
# SHA-256 the release publishes, and cached in obj\. -NoGit leaves it out.
# Set GH_TOKEN to make the GitHub API request authenticated.

param(
    [string]$Out = 'dist',
    [string]$MinGitRelease = 'latest',
    [switch]$NoGit
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest's progress bar slows downloads to a crawl
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Windows PowerShell 5.1 may not offer TLS 1.2 by default; GitHub requires it.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$name    = 'TempCraftBackup'
$exeName = "$name.exe"
$version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "version.txt holds '$version', not a version like 1.2.3" }
$numeric = ($version -split '-')[0]

# ------------------------------------------------------------------ compile

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "Windows' C# compiler (.NET Framework 4.x csc.exe) wasn't found." }

$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$generated = Join-Path $obj 'BuildInfo.g.cs'
@"
using System.Reflection;

[assembly: AssemblyTitle("$name")]
[assembly: AssemblyProduct("$name")]
[assembly: AssemblyDescription("Backs up the source code of every Craft app")]
[assembly: AssemblyVersion("$numeric.0")]
[assembly: AssemblyFileVersion("$numeric.0")]
[assembly: AssemblyInformationalVersion("$version")]

namespace TempCraftBackup
{
    static class BuildInfo
    {
        public const string Version = "$version";
    }
}
"@ | Set-Content -Path $generated -Encoding UTF8

$built = Join-Path $obj $exeName
$sources = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }) + $generated
$cscArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$built",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:System.Web.Extensions.dll'
)
$icon = Join-Path $root 'assets\app.ico'
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }

# This csc prints a notice that it only supports C# 5; that is expected.
$output = & $csc @cscArgs @sources 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | Write-Host
    throw "csc.exe failed (exit code $LASTEXITCODE)"
}
$output | Where-Object { "$_" -match ': (error|warning) ' } | Write-Host

# ------------------------------------------------------------------ MinGit

# Returns the path of the verified MinGit zip, downloading it if it isn't cached.
function Get-MinGit {
    $headers = @{ 'User-Agent' = "$name-build"; 'Accept' = 'application/vnd.github+json' }
    if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }
    $which = if ($MinGitRelease -eq 'latest') { 'latest' } else { "tags/$MinGitRelease" }
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/git-for-windows/git/releases/$which" -Headers $headers

    # MinGit-<version>-64-bit.zip; not the busybox variant, not 32-bit or arm64.
    $asset = @($release.assets | Where-Object { $_.name -match '^MinGit-[0-9.]+-64-bit\.zip$' }) | Select-Object -First 1
    if (-not $asset) { throw "Git for Windows $($release.tag_name) has no MinGit-<version>-64-bit.zip" }

    # The SHA-256 GitHub records for the upload, or else the one in the release
    # notes' "Filename | SHA-256" table.
    $sha = $null
    if ("$($asset.digest)" -match '^sha256:([0-9a-fA-F]{64})$') { $sha = $Matches[1].ToLower() }
    if (-not $sha) {
        $m = [regex]::Match("$($release.body)", [regex]::Escape($asset.name) + '\s*\|\s*([0-9a-fA-F]{64})')
        if ($m.Success) { $sha = $m.Groups[1].Value.ToLower() }
    }
    if (-not $sha) { throw "Git for Windows $($release.tag_name) publishes no SHA-256 for $($asset.name)" }

    $cache = Join-Path $obj $asset.name
    if ((Test-Path $cache) -and (Get-FileHash $cache -Algorithm SHA256).Hash.ToLower() -eq $sha) {
        Write-Host "MinGit: $($asset.name) ($($release.tag_name)), cached, SHA-256 $sha"
        return $cache
    }
    $partial = "$cache.partial"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $partial -UseBasicParsing -Headers @{ 'User-Agent' = "$name-build" }
    $got = (Get-FileHash $partial -Algorithm SHA256).Hash.ToLower()
    if ($got -ne $sha) {
        Remove-Item $partial -Force
        throw "$($asset.name) downloaded with SHA-256 $got, but Git for Windows publishes $sha"
    }
    Move-Item $partial $cache -Force
    Write-Host "MinGit: $($asset.name) ($($release.tag_name)), downloaded, SHA-256 $sha"
    return $cache
}

$minGitZip = $null
if (-not $NoGit) {
    $minGitZip = Get-MinGit
}

# ------------------------------------------------------------------ package

New-Item -ItemType Directory -Force $Out | Out-Null
$Out      = (Resolve-Path $Out).Path
$folder   = Join-Path $Out $name
$zipPath  = Join-Path $Out "$name-windows-x64.zip"
$manifest = 'launcher-manifest.json'

# Fresh program files, but never touch a data\ folder someone has been running from.
if (Test-Path $folder) {
    Get-ChildItem $folder -Force | Where-Object { $_.Name -ne 'data' } | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force $folder | Out-Null
}
Copy-Item $built (Join-Path $folder $exeName)
if ($minGitZip) {
    [IO.Compression.ZipFile]::ExtractToDirectory($minGitZip, (Join-Path $folder 'git'))
    if (-not (Test-Path (Join-Path $folder 'git\cmd\git.exe'))) { throw "MinGit unpacked without git\cmd\git.exe" }
}

$files = Get-ChildItem $folder -Recurse -File |
    Where-Object { $_.FullName -notlike (Join-Path $folder 'data\*') -and $_.Name -ne $manifest } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path   = $_.FullName.Substring($folder.Length + 1).Replace('\', '/')
            size   = $_.Length
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        }
    }

$json = [ordered]@{ version = $version; exe = $exeName; files = @($files) } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $folder $manifest), $json, (New-Object Text.UTF8Encoding $false))

# Built entry by entry: Windows PowerShell's Compress-Archive writes backslashes
# into nested entry names, which zip readers treat as part of the file name.
if (Test-Path $zipPath) { Remove-Item $zipPath }
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($f in @($files.path) + $manifest) {
        $src = Join-Path $folder ($f.Replace('/', '\'))
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $src, $f, 'Optimal')
    }
} finally { $zip.Dispose() }

# This release's entry for update.json (see tools\publish-update.ps1).
$zipInfo = Get-Item $zipPath
$entry = [ordered]@{
    version = $version
    exe     = $exeName
    package = [ordered]@{
        name   = $zipInfo.Name
        size   = $zipInfo.Length
        sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    }
    files   = @($files)
}
[IO.File]::WriteAllText((Join-Path $Out 'release-entry.json'), ($entry | ConvertTo-Json -Depth 5),
    (New-Object Text.UTF8Encoding $false))

Write-Output "Built $name $version -> $zipPath"
