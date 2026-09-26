param([Parameter(Mandatory=$true)][string]$SourceDirectory, [switch]$NoStartup)
$ErrorActionPreference='Stop'
$taskSource=[IO.Path]::GetFullPath($SourceDirectory)
$taskDestination=Join-Path $env:LOCALAPPDATA 'Programs\StrafeLab'
$taskData=Join-Path $env:LOCALAPPDATA 'StrafeLab'
$taskExe=Join-Path $taskDestination 'StrafeLab.exe'
if(-not(Test-Path -LiteralPath (Join-Path $taskSource 'StrafeLab.exe'))){throw 'The source must be a published StrafeLab directory.'}
if(Get-Process StrafeLab -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $taskExe}){throw 'Close the installed StrafeLab and exit its tray collector before updating.'}
New-Item -ItemType Directory -Force -Path $taskData | Out-Null
$taskBackup=Join-Path $taskData ('install-backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $taskBackup | Out-Null
if(Test-Path -LiteralPath $taskDestination){Copy-Item -LiteralPath $taskDestination -Destination (Join-Path $taskBackup 'previous-app') -Recurse}
New-Item -ItemType Directory -Force -Path $taskDestination | Out-Null
Get-ChildItem -LiteralPath $taskSource | Copy-Item -Destination $taskDestination -Recurse -Force
$taskShell=New-Object -ComObject WScript.Shell
$taskLink=Join-Path ([Environment]::GetFolderPath('Desktop')) 'StrafeLab.lnk'
if(Test-Path -LiteralPath $taskLink){Copy-Item -LiteralPath $taskLink -Destination $taskBackup}
$taskShortcut=$taskShell.CreateShortcut($taskLink)
$taskShortcut.TargetPath=$taskExe
$taskShortcut.WorkingDirectory=$taskDestination
$taskShortcut.IconLocation=$taskExe+',0'
$taskShortcut.Description='StrafeLab - 本地急停分析与后台采集'
$taskShortcut.Save()
if(-not $NoStartup){
    $taskRun='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $taskPrevious=(Get-ItemProperty -LiteralPath $taskRun -ErrorAction SilentlyContinue).StrafeLab
    @{name='StrafeLab';key=$taskRun;previous=$taskPrevious} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskBackup 'startup.json') -Encoding utf8
    if(-not(Test-Path -LiteralPath $taskRun)){New-Item -Path $taskRun -Force | Out-Null}
    New-ItemProperty -LiteralPath $taskRun -Name StrafeLab -PropertyType String -Value ('"'+$taskExe+'" --collector') -Force | Out-Null
}
[pscustomobject]@{Executable=$taskExe;Shortcut=$taskLink;Startup=(-not $NoStartup);Backup=$taskBackup}
