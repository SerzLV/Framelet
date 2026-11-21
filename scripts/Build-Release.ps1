$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$project = Join-Path $repository 'Framelet.csproj'
$output = Join-Path $repository 'publish'
$intermediate = (New-Item -Path (Join-Path $repository 'obj') -ItemType Directory -Force).FullName

function Remove-ReleaseStage([string]$name) {
    if ($name -notmatch '^release-[a-f0-9]{32}$') { return }

    $directory = Join-Path $intermediate $name
    $marker = "$directory.cleanup"
    $folder = Get-Item -LiteralPath $directory -ErrorAction SilentlyContinue
    if ($folder) {
        if ($folder.Parent.FullName -ne $intermediate -or
            ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
        try {
            Remove-Item -LiteralPath $folder.FullName -Recurse -Force
        }
        catch {
            # Windows can keep the renamed backup locked while the previous EXE is running.
            New-Item -Path $marker -ItemType File -Force | Out-Null
            Write-Verbose "Will retry cleanup of $directory during the next release build."
            return
        }
    }
    Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
}

Get-ChildItem -LiteralPath $intermediate -Filter 'release-*.cleanup' -File | ForEach-Object {
    Remove-ReleaseStage $_.BaseName
}
$stage = (New-Item -Path (Join-Path $intermediate ('release-' + [Guid]::NewGuid().ToString('N'))) -ItemType Directory).FullName
$keepStage = $false

try {
    $licenses = (New-Item -Path (Join-Path $stage 'licenses') -ItemType Directory).FullName
    Get-ChildItem -LiteralPath (Join-Path $repository 'licenses') -File |
        Copy-Item -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $licenses
    $notices = Get-Content -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Raw -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $licenses 'THIRD-PARTY-NOTICES.md'), $notices.Replace('(Assets/Ocr/LICENSE)', '(LICENSE)'))

    dotnet restore $project -r win-x64 -p:SelfContained=true -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw 'Release restore failed.' }

    $resolved = dotnet msbuild $project -nologo -verbosity:quiet -target:ResolveFrameworkReferences `
        -property:Configuration=Release -property:RuntimeIdentifier=win-x64 -property:SelfContained=true `
        -getItem:ResolvedRuntimePack
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the resolved .NET runtime packs.' }
    $packs = ($resolved -join "`n" | ConvertFrom-Json).Items.ResolvedRuntimePack
    $core = $packs | Where-Object FrameworkName -eq 'Microsoft.NETCore.App' | Select-Object -First 1
    $desktop = $packs | Where-Object FrameworkName -eq 'Microsoft.WindowsDesktop.App' | Select-Object -First 1
    if (!$core -or !$desktop) { throw 'The .NET and Windows Desktop runtime packs are required.' }

    Copy-Item -LiteralPath (Join-Path $core.PackageDirectory 'LICENSE.TXT') `
        -Destination (Join-Path $licenses '.NET-Runtime-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $core.PackageDirectory 'THIRD-PARTY-NOTICES.TXT') `
        -Destination (Join-Path $licenses '.NET-Runtime-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $desktop.PackageDirectory 'LICENSE') `
        -Destination (Join-Path $licenses 'WindowsDesktop-LICENSE.txt')

    $published = Join-Path $stage 'app'
    dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
        "-p:FrameletReleaseLicenses=$licenses" -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

    $exe = Join-Path $published 'Framelet.exe'
    $zip = Join-Path $stage 'Framelet-win-x64.zip'
    $checksums = Join-Path $stage 'SHA256SUMS.txt'
    Compress-Archive -LiteralPath @($exe, $licenses, (Join-Path $licenses 'LICENSE'),
        (Join-Path $licenses 'THIRD-PARTY-NOTICES.md')) -DestinationPath $zip

    Get-FileHash -LiteralPath $exe, $zip | ForEach-Object {
        '{0}  {1}' -f $_.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_.Path)
    } | Set-Content -LiteralPath $checksums -Encoding Ascii

    New-Item -Path $output -ItemType Directory -Force | Out-Null
    $installed = [Collections.Generic.Stack[object]]::new()
    try {
        foreach ($file in @($exe, $zip, $checksums)) {
            $destination = Join-Path $output ([IO.Path]::GetFileName($file))
            $backup = $null
            if (Test-Path -LiteralPath $destination) {
                $backup = "$file.previous"
                [IO.File]::Replace($file, $destination, $backup)
            }
            else {
                [IO.File]::Move($file, $destination)
            }
            $installed.Push(@{ Destination = $destination; Backup = $backup })
        }
    }
    catch {
        try {
            foreach ($item in $installed) {
                if ($item.Backup) {
                    [IO.File]::Replace($item.Backup, $item.Destination, "$($item.Backup).failed")
                }
                else {
                    [IO.File]::Delete($item.Destination)
                }
            }
        }
        catch {
            $keepStage = $true
            throw "Could not restore the previous release. Build files and backups are in $stage. $($_.Exception.Message)"
        }
        throw
    }
    foreach ($name in @('Framelet.exe', 'Framelet-win-x64.zip', 'SHA256SUMS.txt')) {
        Write-Host (Join-Path $output $name)
    }
}
finally {
    if (!$keepStage) {
        Remove-ReleaseStage ([IO.Path]::GetFileName($stage))
    }
}
