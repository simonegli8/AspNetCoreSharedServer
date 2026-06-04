SET PackageVersion=3.0.0
SET Configuration=Release

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack src\AspNetCoreSharedServer.slnx -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion% -p:AssemblyVersion=%PackageVersion%