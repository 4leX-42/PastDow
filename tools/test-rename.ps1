param([string]$Assembly = (Join-Path $PSScriptRoot '../bin/Release/net48/PastDow.exe'))
# Rename before downloading: BrowserMedia.name -> Job.CustomName -> yt-dlp output name
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Assembly).Path)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
$media = $asm.GetType('PastDow.BrowserMedia', $true)
$clean = $media.GetMethod('CleanName', $flags)
function Check($ok, $what) { if (-not $ok) { throw "FAIL: $what" } }
Check ($clean.Invoke($null, @('  Clase 3  ')) -eq 'Clase 3') 'name trimmed'
Check ($null -eq $clean.Invoke($null, @("bad`nname"))) 'control characters dropped'
Check ($null -eq $clean.Invoke($null, @('x' * 201))) 'too long dropped'
Check ($null -eq $clean.Invoke($null, @('   '))) 'blank dropped'
$m = [Activator]::CreateInstance($media)
$media.GetProperty('url').SetValue($m, 'https://www.youtube.com/watch?v=abc')
$media.GetProperty('page').SetValue($m, 'https://www.youtube.com/watch?v=abc')
$media.GetProperty('kind').SetValue($m, 'PAGE')
$media.GetProperty('title').SetValue($m, 'Site title')
$media.GetProperty('name').SetValue($m, 'Mi nombre: 50%')
Check ($media.GetMethod('Valid').Invoke($m, @())) 'media with a name is valid'
$job = $media.GetMethod('Job').Invoke($m, @($false, 0, 'mkv'))
$jt = $job.GetType()
Check ($jt.GetField('CustomName').GetValue($job) -eq 'Mi nombre: 50%') 'job keeps the name'
$args = $asm.GetType('PastDow.Runner', $true).GetMethod('Args', $flags).Invoke($null, @($job))
Check ($args -match '"Mi nombre 50%%\.%\(ext\)s"') "output template uses the literal name (got: $args)"
'Rename: name cleaning, job name and output template passed'
