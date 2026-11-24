$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet test tests/TelPhone.Tests/TelPhone.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Тесты завершились с ошибкой.' }

    dotnet publish src/TelPhone/TelPhone.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -o artifacts/usbPhone-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать usbPhone.' }
    Copy-Item README.md artifacts/usbPhone-win-x64/README.txt
    $licenseDirectory = 'artifacts/usbPhone-win-x64/licenses'
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    Copy-Item licenses/*.txt $licenseDirectory
}
finally {
    Pop-Location
}
