@echo off
REM DarkBox v4.1.0 — sign both EXEs with your code-signing certificate.
REM 1. Buy a Standard or EV code-signing certificate (see SIGNING.md).
REM 2. Set CERT_THUMB below to your certificate thumbprint (certmgr.msc
REM    -> Personal -> Certificates -> Details -> Thumbprint), OR set
REM    CERT_FILE + CERT_PASS to sign with a .pfx file instead.
REM 3. Run this AFTER building, BEFORE uploading. No cert = no signing:
REM    signtool will fail loudly instead of shipping an unsigned build
REM    that looks "fixed".
setlocal
set "CERT_THUMB=PASTE_YOUR_CERT_THUMBPRINT_HERE"
set "CERT_FILE="
set "CERT_PASS="
set "SIGNTOOL=signtool.exe"
where %SIGNTOOL% >nul 2>nul || set "SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe"

if not "%CERT_FILE%"=="" (
  "%SIGNTOOL%" sign /f "%CERT_FILE%" /p "%CERT_PASS%" /tr http://timestamp.digicert.com /td sha256 /fd sha256 /v "DarkBox-Setup.exe"
  "%SIGNTOOL%" sign /f "%CERT_FILE%" /p "%CERT_PASS%" /tr http://timestamp.digicert.com /td sha256 /fd sha256 /v "build-payload\HamiiFolderLocker.exe"
) else (
  "%SIGNTOOL%" sign /sha1 "%CERT_THUMB%" /tr http://timestamp.digicert.com /td sha256 /fd sha256 /v "DarkBox-Setup.exe"
  "%SIGNTOOL%" sign /sha1 "%CERT_THUMB%" /tr http://timestamp.digicert.com /td sha256 /fd sha256 /v "build-payload\HamiiFolderLocker.exe"
)
echo.
echo Done. Verify with: signtool verify /pa DarkBox-Setup.exe
endlocal
