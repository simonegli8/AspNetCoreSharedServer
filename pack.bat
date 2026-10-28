SET PackageVersion=4.0.2
SET Configuration=Release

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack AspNetCoreSharedServer.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion%.0 ^
    -p:AssemblyVersion=%PackageVersion%

dotnet pack AspNetCoreSharedServer.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion%.0 ^
    -p:AssemblyVersion=%PackageVersion% ^
    -p:DebugType=portable ^
    -p:DebugSymbols=true ^
    -p:IncludeSymbols=true ^
    -p:SymbolPackageFormat=snupkg