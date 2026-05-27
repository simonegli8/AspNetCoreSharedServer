SET PackageVersion=2.0.1
SET Configuration=Debug

del src\AspNetCoreSharedServer\bin\%Configuration%\*.nupkg
del src\AspNetCoreSharedServer.Api\bin\%Configuration%\*.nupkg
del src\AspNetCoreSharedServer.Library\bin\%Configuration%\*.nupkg

dotnet pack src\AspNetCoreSharedServer.slnx -c %Configuration%