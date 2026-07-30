SET PackageVersion=3.2.2
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack AspNetCoreSharedServer.slnx -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion%.0 -p:AssemblyVersion=%PackageVersion%