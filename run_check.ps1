$p = Start-Process -FilePath ".\OpenPixel.exe" -ArgumentList "--self-test" -RedirectStandardOutput "st_ps.txt" -RedirectStandardError "st_ps_err.txt" -Wait -PassThru
Write-Host ("EXITCODE=" + $p.ExitCode)
