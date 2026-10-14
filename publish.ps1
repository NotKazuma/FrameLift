# Builds a self-contained single-file FrameLift.exe. Output: dist\FrameLift.exe
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish src/FrameLift -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Get-ChildItem dist -File | Where-Object { $_.Name -notin @("FrameLift.exe") } | Remove-Item -Force
    "Published: " + (Resolve-Path dist\FrameLift.exe)
}
finally { Pop-Location }
