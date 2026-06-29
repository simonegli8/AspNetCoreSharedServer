SET PackageVersion=3.1.8
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack src\AspNetCoreSharedServer.slnx -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion%.0 -p:AssemblyVersion=%PackageVersion%