# SPDX-License-Identifier: GPL-3.0-only
param([string]$CliPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
if (-not $CliPath) { $CliPath = Join-Path $repoRoot 'bin/Release/net10.0/viking.dll' }
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$dotnet = (Get-Command dotnet -CommandType Application).Source
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('viking-cli-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$fixture = Join-Path $tempRoot 'fictional card.viking.json'
$fixtureText = '{"version":1,"protocol":"Viking","frequencyKhz":125,"id":"12345678","readAtUtc":"2026-01-01T00:00:00Z"}'
[IO.File]::WriteAllText($fixture, $fixtureText)
$cases = @(
    @{Name='help'; Args=@('help'); Code=0; Json=$false},
    @{Name='unknown command'; Args=@('unknown','--json'); Code=2; Json=$true},
    @{Name='missing argument'; Args=@('read','--out','--json'); Code=2; Json=$true},
    @{Name='blank target acknowledgement'; Args=@('write','--id','12345678','--json'); Code=2; Json=$true},
    @{Name='invalid ID'; Args=@('write','--id','xyz','--blank-t5577','--json'); Code=1; Json=$true},
    @{Name='missing source'; Args=@('write','--from','absent-card.json','--blank-t5577','--json'); Code=1; Json=$true},
    @{Name='conflicting source options'; Args=@('verify','--id','12345678','--from',$fixture,'--json'); Code=2; Json=$true},
    @{Name='duplicate option'; Args=@('info','--port','COM3','--port','COM3','--json'); Code=2; Json=$true},
    @{Name='wrong command option'; Args=@('info','--blank-t5577','--json'); Code=2; Json=$true},
    @{Name='missing output folder'; Args=@('read','--out',(Join-Path $tempRoot 'absent/card.json'),'--json'); Code=2; Json=$true},
    @{Name='unknown port'; Args=@('info','--port','__nonexistent_viking_test_port__','--json'); Code=2; Json=$true},
    @{Name='card file path with spaces'; Args=@('verify','--from',$fixture,'--port','__nonexistent_viking_test_port__','--json'); Code=2; Json=$true},
    @{Name='list ports without opening'; Args=@('ports','--json'); Code=0; Json=$true}
)
try {
    foreach ($case in $cases) {
        $si = [Diagnostics.ProcessStartInfo]::new()
        $si.FileName = $dotnet
        $si.WorkingDirectory = $tempRoot
        $si.ArgumentList.Add($CliPath)
        foreach ($arg in $case.Args) { $si.ArgumentList.Add($arg) }
        $si.UseShellExecute = $false
        $si.CreateNoWindow = $true
        $si.RedirectStandardOutput = $true
        $si.RedirectStandardError = $true
        $si.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $si.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
        $proc = [Diagnostics.Process]::Start($si)
        try {
            $outTask = $proc.StandardOutput.ReadToEndAsync()
            $errTask = $proc.StandardError.ReadToEndAsync()
            if (-not $proc.WaitForExit(30000)) {
                $proc.Kill($true)
                throw ($case.Name + ': timed out')
            }
            $stdout = $outTask.GetAwaiter().GetResult()
            $stderr = $errTask.GetAwaiter().GetResult()
            if ($proc.ExitCode -ne $case.Code) { throw ($case.Name + ': unexpected exit ' + $proc.ExitCode + '; ' + $stdout + $stderr) }
            if ($case.Json) {
                $value = $stdout | ConvertFrom-Json
                if ($value.ok -ne ($case.Code -eq 0)) { throw ($case.Name + ': invalid JSON result ' + $stdout) }
                if ($case.Code -ne 0 -and $value.exitCode -ne $case.Code) { throw 'Exit code mismatch in JSON' }
            } elseif ($stdout -notmatch 'viking write') { throw 'Help missing write usage' }
        } finally { $proc.Dispose() }
        Write-Output ('PASS ' + $case.Name)
    }
    if ([IO.File]::ReadAllText($fixture) -ne $fixtureText) { throw 'Source fixture was modified' }
    Write-Output ('All ' + $cases.Count + ' CLI checks passed; no device was opened.')
} finally {
    # Delete only the exact fictional fixture and its now-empty temporary directory.
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
    Remove-Item -LiteralPath $tempRoot
}
