@echo off
setlocal EnableDelayedExpansion
mkdir patches
set git=""
:: --------------------------------------------------
:: Stage 1: Clone repo, install %git%/.NET via winget, apply C# patches
:: --------------------------------------------------
echo ===== Stage 1: Prerequisites & Clone =====

if not exist "DnsServer" (
  echo Cloning Technitium DNS Server...
  %git% clone https://github.com/TechnitiumSoftware/DnsServer DnsServer
) else (
  echo Updating existing clone...
  pushd DnsServer
    %git% pull
  popd
)

echo Applying custom C# patches...
if exist "patches\*" (
  xcopy /Y /E "patches\*" "DnsServer\" >nul
) else (
  echo [Warning] No patches folder found. Skipping code injection.
)

echo Stage 1 complete.
echo.

:: --------------------------------------------------
:: Stage 2: Inject Register/Login links and copy form pages
:: --------------------------------------------------
echo ===== Stage 2: Web UI Modifications =====

set "UI=DnsServer\src\wwwroot\index.html"

echo Injecting Register/Login links into index.html
powershell -Command ^
  "(Get-Content -Raw '%UI%') `
   -replace '<ul class=\"nav navbar-nav\">', `
   '<ul class=\"nav navbar-nav\">`r`n    <li><a href=\"/register\">Register</a></li>`r`n    <li><a href=\"/login\">Login</a></li>' `
  | Set-Content '%UI%'"


echo Copying custom HTML form pages
if exist "patches\wwwroot\register.html" (
  copy /Y "patches\wwwroot\register.html" "DnsServer\src\wwwroot\register.html"
)
if exist "patches\wwwroot\login.html" (
  copy /Y "patches\wwwroot\login.html" "DnsServer\src\wwwroot\login.html"
)

echo Stage 2 complete.
echo.

:: --------------------------------------------------
:: Stage 3: Build and publish for production
:: --------------------------------------------------
echo ===== Stage 3: Build & Publish =====

pushd DnsServer\DnsServerApp
  dotnet publish -c Release -o ..\..\publish
popd

echo Stage 3 complete.
echo.
echo All stages finished successfully. Artifacts in DnsServer\publish
exit /b 0
