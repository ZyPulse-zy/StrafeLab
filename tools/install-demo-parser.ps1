$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$pythonCommand = $env:STRAFELAB_PYTHON
if ([string]::IsNullOrWhiteSpace($pythonCommand)) {
    $pythonCommand = "python.exe"
}

& $pythonCommand -m pip install --user "demoparser2==0.42.0"
if ($LASTEXITCODE -ne 0) {
    throw "demoparser2 安装失败，退出码 $LASTEXITCODE。"
}

Write-Host "Demo 解析器已安装。StrafeLab 会自动寻找 Python；如需指定解释器，请设置 STRAFELAB_PYTHON。"
Write-Host "工程目录：$projectRoot"
