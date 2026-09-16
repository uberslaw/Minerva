#Requires -Version 5.1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet restore CpuThrottle.sln
dotnet build CpuThrottle.sln -c Release --no-restore
dotnet test CpuThrottle.sln -c Release --no-build

if (-not (dotnet sln CpuThrottle.sln list | Select-String "CpuThrottle.Tray")) {
    dotnet sln CpuThrottle.sln add src/CpuThrottle.Tray/CpuThrottle.Tray.csproj
}

dotnet build src/CpuThrottle.Tray/CpuThrottle.Tray.csproj -c Release
dotnet publish src/CpuThrottle.Cli/CpuThrottle.Cli.csproj -c Release -r win-x64 --self-contained false -o .\publish\cli
dotnet publish src/CpuThrottle.Tray/CpuThrottle.Tray.csproj -c Release -r win-x64 --self-contained false -o .\publish\tray
dotnet publish samples/CpuThrottle.SampleBurner/CpuThrottle.SampleBurner.csproj -c Release -r win-x64 --self-contained false -o .\publish\sample

Write-Host ""
Write-Host "Process-tree validation (watch Task Manager CPU for SampleBurner tree):"
Write-Host "  .\publish\cli\throttle.exe --cpu 30 -- .\publish\sample\CpuThrottle.SampleBurner.exe --seconds 30 --threads 16 --children 2"
