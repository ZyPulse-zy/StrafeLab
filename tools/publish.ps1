param([string]$OutputDirectory = "outputs\StrafeLab")
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    $taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
    dotnet publish src\StrafeLab\StrafeLab.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o $taskOutput
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    foreach ($taskPair in @(@('work\python-embed','demo-python'), @('work\demoparser_runtime','demo-runtime'))) {
        $taskSource = Join-Path $taskRoot $taskPair[0]
        if (-not (Test-Path -LiteralPath $taskSource)) { throw "Missing offline runtime: $taskSource. See README." }
        $taskDestination = Join-Path $taskOutput $taskPair[1]
        New-Item -ItemType Directory -Force -Path $taskDestination | Out-Null
        Copy-Item -Path (Join-Path $taskSource '*') -Destination $taskDestination -Recurse -Force
    }
    "python312.zip`n.`n..\demo-runtime`nimport site`n" | Set-Content -Encoding ascii -LiteralPath (Join-Path $taskOutput 'demo-python\python312._pth')
    foreach ($taskDoc in @('KNOWN-ISSUES.md','README.md','RESEARCH.md','VERIFICATION.md','THIRD-PARTY-NOTICES.md','LICENSE')) {
        if(Test-Path $taskDoc){Copy-Item -LiteralPath $taskDoc -Destination $taskOutput -Force}
    }
    Copy-Item -LiteralPath tools\demo-extractor.py -Destination $taskOutput -Force
    Copy-Item -LiteralPath tools\start-analysis.cmd -Destination (Join-Path $taskOutput 'Start-Analysis.cmd') -Force
    foreach ($taskFolder in @('licenses','evidence')) {
        if (Test-Path -LiteralPath $taskFolder) {
            $taskFolderOutput = Join-Path $taskOutput $taskFolder
            New-Item -ItemType Directory -Force -Path $taskFolderOutput | Out-Null
            Copy-Item -Path (Join-Path $taskFolder '*') -Destination $taskFolderOutput -Recurse -Force
        }
    }
    Write-Host "Published: $taskOutput"
} finally { Pop-Location }
