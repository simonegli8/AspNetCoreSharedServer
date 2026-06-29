SET PackageVersion=3.1.10
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack src\AspNetCoreSharedServer.slnx -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion%.0 -p:AssemblyVersion=%PackageVersion%