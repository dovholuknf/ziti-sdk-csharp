#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Auto-update and publish OpenZiti.NET.Native when a new ziti-sdk-c release is available

.DESCRIPTION
    This script:
    1. Checks for the latest ziti-sdk-c release
    2. Compares with current version in version.props
    3. If new version available:
       - Updates version.props
       - Fetches native binaries
       - Verifies all platform binaries
       - Builds NuGet package
       - Tests package contents
       - Optionally publishes to NuGet.org

.PARAMETER NuGetApiKey
    NuGet API key for publishing (optional - if not provided, will skip publish)

.PARAMETER Force
    Force update even if version hasn't changed

.PARAMETER SkipPublish
    Skip publishing to NuGet (useful for testing)

.EXAMPLE
    .\AutoUpdateAndPublish.ps1 -SkipPublish
    Test the update process without publishing

.EXAMPLE
    .\AutoUpdateAndPublish.ps1 -NuGetApiKey "your-api-key"
    Update and publish to NuGet

.EXAMPLE
    $env:NUGET_API_KEY = "your-api-key"
    .\AutoUpdateAndPublish.ps1
    Update and publish using environment variable
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$NuGetApiKey = $env:NUGET_API_KEY,

    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Script must run from OpenZiti.NET.Native directory
$scriptDir = $PSScriptRoot
if (-not (Test-Path "$scriptDir/version.props")) {
    throw "This script must be run from the OpenZiti.NET.Native directory"
}

Set-Location $scriptDir

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Gray
}

function Write-Warning {
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-Failed {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

# Step 1: Get latest ziti-sdk-c release
Write-Step "Checking for latest ziti-sdk-c release"
try {
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/openziti/ziti-sdk-c/releases/latest"
    $latestVersion = $response.tag_name
    $releaseUrl = $response.html_url
    $releaseNotes = $response.body
    Write-Success "Latest version: $latestVersion"
    Write-Info "Release: $releaseUrl"
} catch {
    Write-Failed "Failed to get latest release from GitHub"
    throw $_
}

# Step 2: Get current version
Write-Step "Reading current version from version.props"
try {
    [xml]$versionFile = Get-Content "$scriptDir/version.props"
    $currentVersion = $versionFile.Project.PropertyGroup.Version
    Write-Success "Current version: $currentVersion"
} catch {
    Write-Failed "Failed to read version.props"
    throw $_
}

# Step 3: Compare versions
Write-Step "Comparing versions"
if ($latestVersion -eq $currentVersion -and -not $Force) {
    Write-Success "Already up to date at version $currentVersion"
    Write-Info "Use -Force to rebuild anyway"
    exit 0
} elseif ($latestVersion -eq $currentVersion -and $Force) {
    Write-Warning "Forcing rebuild of version $currentVersion"
} else {
    Write-Success "Update available: $currentVersion -> $latestVersion"
}

# Step 4: Update version.props
if ($latestVersion -ne $currentVersion) {
    Write-Step "Updating version.props to $latestVersion"
    try {
        [xml]$versionFile = Get-Content "$scriptDir/version.props"
        $versionFile.Project.PropertyGroup.Version = $latestVersion
        $versionFile.Save("$scriptDir/version.props")
        Write-Success "Updated version.props to $latestVersion"
    } catch {
        Write-Failed "Failed to update version.props"
        throw $_
    }
}

# Step 5: Fetch native binaries
Write-Step "Fetching native binaries for version $latestVersion"
try {
    $getNativeOutput = & "$scriptDir/getNative.ps1" 2>&1
    Write-Host $getNativeOutput

    # Check if the expected output directory exists instead of relying on $LASTEXITCODE
    $expectedStageDir = "$scriptDir/third_party/ziti-sdk-c/$latestVersion/runtimes"
    if (-not (Test-Path $expectedStageDir)) {
        throw "getNative.ps1 did not create expected output directory: $expectedStageDir"
    }

    Write-Success "Native binaries fetched successfully"
} catch {
    Write-Failed "Failed to fetch native binaries"
    throw $_
}

# Step 6: Verify binaries downloaded
Write-Step "Verifying all platform binaries"
$expectedPlatforms = @(
    "win-x86", "win-x64", "win-arm64",
    "linux-x64", "linux-arm", "linux-arm64",
    "osx-x64", "osx-arm64"
)

$missing = @()
foreach ($platform in $expectedPlatforms) {
    $path = "$scriptDir/third_party/ziti-sdk-c/$latestVersion/runtimes/$platform/native"
    if (-not (Test-Path $path)) {
        $missing += $platform
        Write-Warning "Missing binaries for $platform"
    } else {
        $files = @(Get-ChildItem $path -File)
        if ($files.Count -eq 0) {
            $missing += $platform
            Write-Warning "No files found for $platform"
        } else {
            Write-Info "✓ $platform ($($files.Count) file(s))"
        }
    }
}

if ($missing.Count -gt 0) {
    Write-Failed "Missing binaries for platforms: $($missing -join ', ')"
    throw "Platform verification failed"
}
Write-Success "All 8 platform binaries verified"

# Step 7: Clean previous build artifacts
Write-Step "Cleaning previous build artifacts"
$nupkgDir = "$scriptDir/nupkg"
if (Test-Path $nupkgDir) {
    Remove-Item $nupkgDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
Write-Success "Build directory cleaned"

# Step 8: Build NuGet package
Write-Step "Building NuGet package"
try {
    dotnet pack "$scriptDir/src/OpenZiti.NET.Native/OpenZiti.NET.Native.csproj" -c Release -o $nupkgDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack exited with code $LASTEXITCODE"
    }

    $packages = @(Get-ChildItem $nupkgDir -Filter "*.nupkg" | Where-Object { $_.Name -notlike "*.symbols.nupkg" })
    if ($packages.Count -eq 0) {
        throw "No package file created"
    }

    Write-Success "Package built successfully"
    foreach ($pkg in $packages) {
        Write-Info "  $($pkg.Name) ($([math]::Round($pkg.Length / 1MB, 2)) MB)"
    }
} catch {
    Write-Failed "Package build failed"
    throw $_
}

# Step 9: Test package contents
Write-Step "Validating package contents"
try {
    $package = Get-ChildItem $nupkgDir -Filter "OpenZiti.NET.Native.$latestVersion.nupkg" | Select-Object -First 1

    if (-not $package) {
        throw "Package file not found: OpenZiti.NET.Native.$latestVersion.nupkg"
    }

    $testDir = "$nupkgDir/test"
    if (Test-Path $testDir) {
        Remove-Item $testDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $testDir | Out-Null

    Expand-Archive -Path $package.FullName -DestinationPath $testDir -Force

    $runtimesPath = Join-Path $testDir "runtimes"
    if (-not (Test-Path $runtimesPath)) {
        throw "Package does not contain runtimes folder"
    }

    # Count runtime folders
    $runtimeFolders = @(Get-ChildItem $runtimesPath -Directory)
    Write-Info "Package contains $($runtimeFolders.Count) runtime folders:"
    foreach ($folder in $runtimeFolders) {
        $nativeFiles = Get-ChildItem "$($folder.FullName)/native" -File -ErrorAction SilentlyContinue
        if ($nativeFiles) {
            Write-Info "  ✓ $($folder.Name): $($nativeFiles.Name -join ', ')"
        }
    }

    Write-Success "Package validation successful"
} catch {
    Write-Failed "Package validation failed"
    throw $_
}

# Step 10: Run smoke test
Write-Step "Running smoke test"
try {
    $smokeTestProjectPath = "$scriptDir/SmokeTest/Smoke.csproj"

    if (-not (Test-Path $smokeTestProjectPath)) {
        Write-Warning "Smoke test project not found at $smokeTestProjectPath"
        Write-Warning "Skipping smoke test"
    } else {
        # Create a temporary NuGet.config that points to our local package
        $tempNuGetConfig = "$scriptDir/SmokeTest/NuGet.config"
        $nuGetConfigContent = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-test" value="$($nupkgDir)" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
        $nuGetConfigContent | Out-File -FilePath $tempNuGetConfig -Encoding utf8

        # Update the smoke test project to reference the new version
        [xml]$smokeProj = Get-Content $smokeTestProjectPath
        $packageRef = $smokeProj.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq "OpenZiti.NET.Native" }
        if ($packageRef) {
            $packageRef.Version = $latestVersion
            $smokeProj.Save($smokeTestProjectPath)
        }

        # Clean NuGet cache for this package to force using local version
        Write-Info "Clearing NuGet cache for OpenZiti.NET.Native..."
        dotnet nuget locals all --clear | Out-Null

        # Clean and restore
        Write-Info "Cleaning smoke test project..."
        dotnet clean $smokeTestProjectPath --configuration Release | Out-Null

        Write-Info "Restoring smoke test with local package..."
        dotnet restore $smokeTestProjectPath --force --no-cache --configfile $tempNuGetConfig
        if ($LASTEXITCODE -ne 0) {
            throw "Smoke test restore failed"
        }

        Write-Info "Running smoke test..."
        $output = dotnet run --project $smokeTestProjectPath --configuration Release --no-restore 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            Write-Failed "Smoke test exited with code $exitCode"
            Write-Host $output
            throw "Smoke test failed"
        }

        # Display smoke test output
        Write-Info "Smoke test output:"
        $output | ForEach-Object { Write-Info "  $_" }

        # Verify output contains version info
        $outputText = $output -join "`n"
        if ($outputText -match "version=(.+)") {
            $smokeVersion = $matches[1].Trim()
            Write-Info "Native library version: $smokeVersion"

            if ($smokeVersion -eq $latestVersion) {
                Write-Success "Version match confirmed: $smokeVersion"
            } else {
                Write-Warning "Version mismatch: expected $latestVersion, got $smokeVersion"
            }
        } else {
            Write-Warning "Could not extract version from smoke test output"
        }

        # Cleanup
        if (Test-Path $tempNuGetConfig) {
            Remove-Item $tempNuGetConfig -Force
        }

        Write-Success "Smoke test passed"
    }
} catch {
    Write-Failed "Smoke test failed"
    throw $_
}

# Step 11: Publish to NuGet (optional)
if ($SkipPublish) {
    Write-Step "Skipping NuGet publish (SkipPublish flag set)"
    Write-Success "Package ready at: $($package.FullName)"
    Write-Info "To publish manually, run:"
    Write-Info "  dotnet nuget push `"$($package.FullName)`" --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json"
} elseif (-not $NuGetApiKey) {
    Write-Step "Skipping NuGet publish (no API key provided)"
    Write-Warning "Set NUGET_API_KEY environment variable or use -NuGetApiKey parameter to publish"
    Write-Success "Package ready at: $($package.FullName)"
    Write-Info "To publish manually, run:"
    Write-Info "  dotnet nuget push `"$($package.FullName)`" --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json"
} else {
    Write-Step "Publishing to NuGet.org"
    try {
        $package = Get-ChildItem $nupkgDir -Filter "OpenZiti.NET.Native.$latestVersion.nupkg" | Select-Object -First 1

        dotnet nuget push $package.FullName --api-key $NuGetApiKey --source https://api.nuget.org/v3/index.json --skip-duplicate

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet nuget push exited with code $LASTEXITCODE"
        }

        Write-Success "Successfully published OpenZiti.NET.Native $latestVersion to NuGet.org"
        Write-Info "Package URL: https://www.nuget.org/packages/OpenZiti.NET.Native/$latestVersion"
    } catch {
        Write-Failed "NuGet publish failed"
        throw $_
    }
}

# Summary
Write-Host "`n" -NoNewline
Write-Host "========================================" -ForegroundColor Green
Write-Host "✓ SUCCESS" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Version: $latestVersion" -ForegroundColor White
Write-Host "Package: $($package.Name)" -ForegroundColor White
if (-not $SkipPublish -and $NuGetApiKey) {
    Write-Host "Published: Yes" -ForegroundColor White
} else {
    Write-Host "Published: No" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Green
