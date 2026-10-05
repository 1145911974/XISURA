$probe = 'C:\Users\Administrator\Documents\ChatGPT\蛟龙\.worktrees\approved-home-reconstruction\tools\capability-probes\post-reboot-probe.ps1'
Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    ('"' + $probe + '"')
)
