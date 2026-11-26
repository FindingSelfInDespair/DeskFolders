param(
    [switch]$SelfContained
)

$outDir = "$PWD\publish"
if ($SelfContained) {
    # Полностью автономный один .exe (~65 МБ, запускается даже без установленного .NET 8)
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o "$outDir\standalone"
    Write-Host "Готово (Standalone): $outDir\standalone\DeskFolders.exe" -ForegroundColor Green
}
else {
    # Лёгкий один .exe (~400 КБ, требует установленный .NET 8 Desktop Runtime)
    dotnet publish -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -o "$outDir\lite"
    Write-Host "Готово (Lite): $outDir\lite\DeskFolders.exe" -ForegroundColor Green
}