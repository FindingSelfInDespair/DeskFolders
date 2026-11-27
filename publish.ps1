param(
    [switch]$SelfContained
)

$outDir = "$PWD\publish"
if ($SelfContained) {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o "$outDir\standalone"
    Write-Host "Done (Standalone): $outDir\standalone\DeskFolders.exe" -ForegroundColor Green
}
else {
    dotnet publish -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -o "$outDir\lite"
    Write-Host "Done (Lite): $outDir\lite\DeskFolders.exe" -ForegroundColor Green
}