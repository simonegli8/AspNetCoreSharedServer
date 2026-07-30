#!/usr/bin/env bash

PackageVersion="3.2.2"
Configuration="Debug"

rm -f nupkg/*.nupkg
rm -f nupkg/*.snupkg

dotnet pack AspNetCoreSharedServer.slnx \
    -c "$Configuration" \
    -p:Version="$PackageVersion" \
    -p:FileVersion="${PackageVersion}.0" \
    -p:AssemblyVersion="$PackageVersion"

dotnet pack AspNetCoreSharedServer.slnx \
    -c "$Configuration" \
    -p:Version="$PackageVersion" \
    -p:FileVersion="${PackageVersion}.0" \
    -p:AssemblyVersion="$PackageVersion" \
    -p:DebugType=portable \
    -p:DebugSymbols=true \
    -p:IncludeSymbols=true \
    -p:SymbolPackageFormat=snupkg