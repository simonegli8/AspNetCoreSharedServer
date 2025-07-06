SET /p ApiKey=<NugetApiKey.txt

cd src\AspNetCoreSharedServer\bin\Release\

for /r %%i in (*.nupkg) do (
    dotnet nuget push %%i --api-key %ApiKey% -s https://api.nuget.org/v3/index.json --skip-unchanged
)

cd ..\..\..\AspNetCoreSharedServer.Api\bin\Release

for /r %%i in (*.nupkg) do (
    dotnet nuget push %%i --api-key %ApiKey% -s https://api.nuget.org/v3/index.json --skip-unchanged
)

cd ..\..\..\AspNetCoreSharedServer.Library\bin\Release

for /r %%i in (*.nupkg) do (
    dotnet nuget push %%i --api-key %ApiKey% -s https://api.nuget.org/v3/index.json --skip-unchanged
)

cd ..\..\..\..\