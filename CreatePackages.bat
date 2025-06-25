SET PackageVersion=1.0.22
SET Configuration=Release

del src\AspNetCoreSharedServer\bin\%Configuration%\*.nupkg
del src\AspNetCoreSharedServer.Api\bin\%Configuration%\*.nupkg

dotnet pack src\AspNetCoreSharedServer.sln -c %Configuration%