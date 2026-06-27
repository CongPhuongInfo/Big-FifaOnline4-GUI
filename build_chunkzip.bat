@echo off
setlocal enabledelayedexpansion

set "FRAMEWORK_BASE=C:\Windows\Microsoft.NET\Framework64"
set "CSC="

for %%V in (4.8 4.7.2 4.7.1 4.7 4.6.2 4.6.1 4.6 4.5.2 4.5.1 4.5 4.0) do (
    if "!CSC!"=="" (
        for /d %%D in ("%FRAMEWORK_BASE%\v%%V*") do (
            if exist "%%D\csc.exe" set "CSC=%%D\csc.exe"
        )
    )
)

if "%CSC%"=="" (
    set "FRAMEWORK_BASE=C:\Windows\Microsoft.NET\Framework"
    for %%V in (4.8 4.7.2 4.7.1 4.7 4.6.2 4.6.1 4.6 4.5.2 4.5.1 4.5 4.0) do (
        if "!CSC!"=="" (
            for /d %%D in ("%FRAMEWORK_BASE%\v%%V*") do (
                if exist "%%D\csc.exe" set "CSC=%%D\csc.exe"
            )
        )
    )
)

if "!CSC!"=="" (
    echo [ERROR] Khong tim thay csc.exe
    exit /b 1
)

echo [INFO] Compiler: !CSC!

"!CSC!" ^
  /target:winexe ^
  /optimize+ ^
  /platform:x86 ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  /r:System.dll ^
  /out:"%cd%\ChunkZipExplorer.exe" ^
  "%cd%\ChunkZipExplorer.cs"

if %errorlevel%==0 (
    echo [OK] Build thanh cong: ChunkZipExplorer.exe
) else (
    echo [ERROR] Build that bai.
)

endlocal
pause
