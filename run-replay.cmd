@echo off
dotnet run --project "%~dp0nina.plugin.livestack.replay\nina.plugin.livestack.replay.csproj" --configuration Release -- %*
