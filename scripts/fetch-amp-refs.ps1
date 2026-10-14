# Windows version of fetch-amp-refs.sh. Requires the .NET 8 SDK.
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$lib  = Join-Path $root "lib"
$work = Join-Path ([IO.Path]::GetTempPath()) ("amp-refs-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $url = if ($env:AMP_DOWNLOAD_URL) { $env:AMP_DOWNLOAD_URL } else { "https://cubecoders.com/Downloads/AMP_Latest.zip" }
    Write-Host "Downloading $url ..."
    Invoke-WebRequest $url -OutFile "$work\amp.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead("$work\amp.zip")
    foreach ($e in $zip.Entries) {
        if ($e.FullName -in @("AMP_Linux_x86_64", "AMPVersion", "Plugins/InstanceManagerPlugin/InstanceManagerPlugin.dll")) {
            $dest = Join-Path $work ($e.FullName -replace "/", "\")
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true)
        }
    }
    $zip.Dispose()
    if (-not (Get-Command sfextract -ErrorAction SilentlyContinue)) {
        dotnet tool install -g sfextract | Out-Null
        $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    }
    sfextract "$work\AMP_Linux_x86_64" -o "$work\bundle" | Out-Null
    New-Item -ItemType Directory -Force -Path $lib | Out-Null
    Copy-Item "$work\bundle\ModuleShared.dll" $lib
    Copy-Item "$work\Plugins\InstanceManagerPlugin\InstanceManagerPlugin.dll" $lib
    Copy-Item "$work\AMPVersion" "$lib\AMPVersion.txt"
    Write-Host "AMP version: $(Get-Content "$work\AMPVersion")"
    Write-Host "Reference assemblies written to $lib"
} finally {
    Remove-Item -Recurse -Force $work
}
