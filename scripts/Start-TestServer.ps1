[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 8080
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsDirectory = Join-Path $projectRoot '.tools'
$localDotnet = Join-Path $toolsDirectory 'dotnet\dotnet.exe'
$launcherLock = $null

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $script:dotnetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE. The test server was not started."
    }
}

Push-Location -LiteralPath $projectRoot
try {
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    try {
        $launcherLock = [IO.File]::Open((Join-Path $toolsDirectory 'test-server.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw 'Another 7blog launcher is already running. Wait for it to finish, or use the server it has started. No installation was attempted.'
    }
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    $portProbe.Server.ExclusiveAddressUse = $true
    try { $portProbe.Start() }
    catch [Net.Sockets.SocketException] {
        throw "Port $Port is already in use. 7blog may already be running at http://127.0.0.1:$Port. Stop it first, or choose another -Port. No installation was attempted."
    }
    finally { $portProbe.Stop() }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_HOME = Join-Path $toolsDirectory 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $toolsDirectory 'nuget'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Blog__SiteUrl = "http://127.0.0.1:$Port"
    $env:AllowedHosts = 'localhost;127.0.0.1'

    $script:dotnetExecutable = $null
    $candidates = @($localDotnet)
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) { $candidates += $installed.Source }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
            $script:dotnetExecutable = $candidate
            break
        }
    }

    if (-not $script:dotnetExecutable) {
        Write-Host 'Installing the .NET 10 SDK into .tools/dotnet (no administrator rights needed)...'
        $installer = Join-Path $toolsDirectory 'dotnet-install.ps1'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        }
        catch {
            throw "Could not download the official .NET installer. Check this terminal's network access. Details: $($_.Exception.Message)"
        }
        # Resolve a stable SDK explicitly and use Microsoft's builds feed. The
        # channel/aka.ms redirect can point at an origin that rejects downloads.
        $releaseMetadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
        $sdkVersion = $releaseMetadata.'latest-sdk'
        if ($sdkVersion -notmatch '^10\.0\.\d+$') { throw 'Microsoft release metadata did not contain a stable .NET 10 SDK.' }
        & $installer -Version $sdkVersion -AzureFeed 'https://builds.dotnet.microsoft.com/dotnet' -InstallDir (Split-Path -Parent $localDotnet) -NoPath
        if (-not (Test-Path -LiteralPath $localDotnet)) { throw 'The .NET SDK installation did not complete.' }
        $installedSdks = & $localDotnet --list-sdks
        if ($LASTEXITCODE -ne 0 -or -not ($installedSdks -match '^10\.')) { throw 'The .NET 10 SDK could not be verified after installation.' }
        $script:dotnetExecutable = $localDotnet
    }

    # Child test processes call 'dotnet' by name.
    $env:PATH = "$(Split-Path -Parent $script:dotnetExecutable);$env:PATH"
    $env:DOTNET_ROOT = Split-Path -Parent $script:dotnetExecutable

    Write-Host 'Building 7blog...'
    Invoke-Dotnet -Arguments @('build', 'SevenBlog.csproj', '--configuration', 'Debug')
    Write-Host 'Running content and HTTP integration checks...'
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/SevenBlog.Tests/SevenBlog.Tests.csproj')

    Write-Host ''
    Write-Host "Checks passed. Starting 7blog at http://127.0.0.1:$Port"
    Write-Host 'Wait for the Now listening message, then open that URL. Press Ctrl+C to stop.'
    Invoke-Dotnet -Arguments @('run', '--no-build', '--project', 'SevenBlog.csproj', '--no-launch-profile', '--urls', "http://127.0.0.1:$Port")
}
finally {
    if ($launcherLock) { $launcherLock.Dispose() }
    Pop-Location
}
