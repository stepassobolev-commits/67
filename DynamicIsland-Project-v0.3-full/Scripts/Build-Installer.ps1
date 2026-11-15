$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "DynamicIsland\DynamicIsland.csproj"
$publish = Join-Path $root "publish"
$dist = Join-Path $root "dist"

Write-Host "== Dynamic Island: сборка приложения ==" -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Не найден .NET 10 SDK. Установите его с https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}
if (-not (Get-Command iscc -ErrorAction SilentlyContinue)) {
    $isccCandidates = @(
        "$env:ProgramFiles (x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
} else {
    $iscc = (Get-Command iscc).Source
}
if (-not $iscc) {
    Write-Host "Не найден Inno Setup 6. Установите его: https://jrsoftware.org/isdl.php"
    exit 1
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish, $dist | Out-Null

dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "Ошибка восстановления пакетов NuGet." }

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки приложения." }

& $iscc (Join-Path $root "Installer\DynamicIsland.iss")
if ($LASTEXITCODE -ne 0) { throw "Ошибка создания установщика Inno Setup." }

Write-Host ""
Write-Host "Готово! Установщик находится здесь:" -ForegroundColor Green
Write-Host (Join-Path $dist "DynamicIsland-Setup.exe")
