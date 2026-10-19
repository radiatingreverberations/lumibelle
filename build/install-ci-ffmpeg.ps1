param([ValidateRange(0, 60)][int]$RetrySeconds = 15)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Test-MediaTools {
    foreach ($name in @('ffmpeg', 'ffprobe')) {
        $tool = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $tool) { return $false }
        & $tool.Source -version *> $null
        if ($LASTEXITCODE -ne 0) { return $false }
    }
    return $true
}

if (Test-MediaTools) { Write-Host 'FFmpeg and ffprobe are ready.'; exit 0 }

for ($attempt = 1; $attempt -le 3; $attempt++) {
    Write-Host "Installing FFmpeg (attempt $attempt of 3)."
    & choco install ffmpeg --no-progress -y
    # Chocolatey can return success after a feed error with zero packages installed.
    # Check the executables the fixtures actually need before allowing tests to run.
    if (Test-MediaTools) { Write-Host 'FFmpeg and ffprobe are ready.'; exit 0 }
    if ($attempt -lt 3) {
        Write-Warning 'FFmpeg or ffprobe is unavailable; retrying the installation.'
        Start-Sleep -Seconds $RetrySeconds
    }
}

throw 'FFmpeg installation failed: ffmpeg and ffprobe must both run before media tests can start.'
