SET PackageVersion=1.1.11
SET Configuration=Release

del src\AspNetCoreSharedServer\bin\%Configuration%\*.nupkg
del src\AspNetCoreSharedServer.Api\bin\%Configuration%\*.nupkg
del src\AspNetCoreSharedServer.Library\bin\%Configuration%\*.nupkg

dotnet pack src\AspNetCoreSharedServer.sln -c %Configuration%