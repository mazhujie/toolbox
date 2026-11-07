param([Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string]$Version)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'Toolbox.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Run scripts\build.cmd before packaging.' }

# Pin the official SDK archive so local packages and CI releases contain the same ADB binaries.
$sdkVersion = '37.0.1'
$sdkHash = '45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99'
$downloads = Join-Path $bin 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$sdkArchive = Join-Path $downloads "platform-tools_r$sdkVersion-win.zip"
if (-not (Test-Path -LiteralPath $sdkArchive)) {
    Invoke-WebRequest -Uri "https://dl.google.com/android/repository/platform-tools_r$sdkVersion-win.zip" -OutFile $sdkArchive -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sdkHash) {
    throw 'Platform Tools checksum mismatch. Remove the cached ZIP and retry.'
}

$name = "Toolbox-$Version-portable"
$staging = Join-Path $bin ('package-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $staging $name
$archive = Join-Path $bin "$name.zip"
try {
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $package 'Toolbox.exe')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $package -Recurse
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $package
    foreach ($file in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package "platform-tools\$file"))) { throw "Missing SDK file: $file" }
    }
    @"
Toolbox $Version portable

Extract this entire folder and run Toolbox.exe on Windows 10/11 with .NET Framework 4.8.
Android Platform Tools $sdkVersion is included. Keep its DLLs and NOTICE.txt together.
For USB ADB, enable USB debugging, authorize the computer and install device drivers if needed.
For live packet capture, install Npcap separately: https://npcap.com/#download
Npcap is not bundled. ADB and other tools do not require Npcap.

Source: https://github.com/mazhujie/toolbox
Android Platform Tools: https://developer.android.com/tools/releases/platform-tools
"@ | Set-Content -LiteralPath (Join-Path $package 'Portable README.txt') -Encoding UTF8
    Compress-Archive -LiteralPath $package -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ASCII
    Write-Output "Package: $archive"
    Write-Output "SHA256:  $hash"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
