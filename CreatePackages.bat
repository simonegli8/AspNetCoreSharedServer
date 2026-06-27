SET PackageVersion=3.1.4
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack src\AspNetCoreSharedServer.slnx -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion% -p:AssemblyVersion=%PackageVersion%