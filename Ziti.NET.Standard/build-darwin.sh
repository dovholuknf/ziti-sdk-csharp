#!/bin/bash

echo "Building the solution for the libZiti.NET.standard.dylib"

#Ziti_Net_HOME=$(dirname $0)
Ziti_Net_HOME=$PWD

echo dotnet build $Ziti_Net_HOME/../Ziti.NuGet.sln /property:Configuration=Release /property:Platform=x64

dotnet build $Ziti_Net_HOME/../Ziti.NuGet.sln /property:Configuration=Release /property:Platform=x64

retval=$?
if [[ $retval != 0 ]]; then
    echo " "
    echo "Build of $Ziti_Net_HOME/../Ziti.NuGet.sln for Platform=x64 failed"
    echo "Exiting..."
    exit $retval
else
    echo " "
    echo "result of dotnet build for Platform=x64: $retval"
fi

echo "dotnet build $Ziti_Net_HOME/../Ziti.NuGet.sln /property:Configuration=Release"

dotnet build $Ziti_Net_HOME/../Ziti.NuGet.sln /property:Configuration=Release

retval=$?
if [[ $retval != 0 ]]; then
    echo " "
    echo "Build of $Ziti_Net_HOME%/../Ziti.NuGet.sln failed"
    echo "Exiting..."
    exit $retval
else
    echo " "
    echo "result of msbuild: $retval"
fi

echo "dotnet pack $Ziti_Net_HOME/../Ziti.NuGet.sln --configuration Release --output $Ziti_Net_HOME"

dotnet pack $Ziti_Net_HOME/../Ziti.NuGet.sln --configuration Release --output $Ziti_Net_HOME

retval=$?
if [[ $retval != 0 ]]; then
    echo " "
    echo "nuget pack for $BUILD_VERSION failed"
    echo "Exiting..."
    exit $retval
else
    echo " "
    echo "result of dotnet pack: $retval"
fi

NUGET_PATH="$Ziti_Net_HOME/../NuGet/"
mkdir $NUGET_PATH

NUPKG_FILE=`ls -rt Ziti.NET.Standard.*.nupkg | tail -n 1`

echo nuget push -source $NUGET_PATH $Ziti_Net_HOME/$NUPKG_FILE

nuget push -source $NUGET_PATH "$Ziti_Net_HOME/$NUPKG_FILE"

retval=$?
if [[ $retval != 0 ]]; then
    echo " "
    echo "nuget push for $NUPKG_FILE failed"
    echo "Exiting..."
    exit $retval
else
    echo " "
    echo "result of nuget push: $retval"
fi

echo " "
echo " "
echo "====================================================="
echo "	BUILD COMPLETE	:"
echo "	Package file 	: $NUPKG_FILE"
echo "====================================================="
