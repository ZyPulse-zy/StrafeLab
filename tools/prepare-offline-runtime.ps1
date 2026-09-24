param([string]$HostPython = 'python.exe')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskWork = Join-Path $taskRoot 'work'
$taskEmbed = Join-Path $taskWork 'python-embed'
$taskRuntime = Join-Path $taskWork 'demoparser_runtime'
$taskArchive = Join-Path $taskWork 'python-3.12.10-embed-amd64.zip'
$taskHash = '4ACBED6DD1C744B0376E3B1CF57CE906F9DC9E95E68824584C8099A63025A3C3'
New-Item -ItemType Directory -Force -Path $taskWork,$taskEmbed,$taskRuntime | Out-Null
if (-not (Test-Path -LiteralPath $taskArchive)) {
    Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $taskArchive
}
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskHash) {
    throw 'Python archive does not match the verified release artifact.'
}
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskEmbed -Force
& $HostPython -m pip install --only-binary=:all: --platform win_amd64 --python-version 3.12 --implementation cp --abi cp312 --target $taskRuntime --upgrade -r (Join-Path $PSScriptRoot 'demo-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Failed to prepare the local Demo runtime.' }
"python312.zip`n.`n..\demoparser_runtime`nimport site`n" | Set-Content -Encoding ascii -LiteralPath (Join-Path $taskEmbed 'python312._pth')
& (Join-Path $taskEmbed 'python.exe') -c 'from demoparser2 import DemoParser; import pandas, polars, pyarrow; print("Offline Demo runtime ready")'
if ($LASTEXITCODE -ne 0) { throw 'The embedded runtime import check failed.' }
