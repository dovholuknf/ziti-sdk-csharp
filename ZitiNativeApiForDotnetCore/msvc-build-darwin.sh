#!/bin/bash

if [[ "$ZITI_SDK_C_BRANCH" == "" ]]; then
    echo "ZITI_SDK_C_BRANCH is not set - ZITI_SDK_C_BRANCH_CMD will be empty"
    ZITI_SDK_C_BRANCH_CMD=" "
else
    echo "SETTING ZITI_SDK_C_BRANCH_CMD to: -DZITI_SDK_C_BRANCH=$ZITI_SDK_C_BRANCH."
    ZITI_SDK_C_BRANCH_CMD="-DZITI_SDK_C_BRANCH=$ZITI_SDK_C_BRANCH"
fi

echo "================ZITI_SDK_C_BRANCH_CMD: $ZITI_SDK_C_BRANCH_CMD"

# CSDK_HOME=$(dirname $0)
CSDK_HOME=$PWD

BUILDFOLDER="$CSDK_HOME/build-win"

mkdir $BUILDFOLDER 2> /dev/null
mkdir "$BUILDFOLDER/osx-x64" 2> /dev/null

cmake -S $CSDK_HOME -B "$BUILDFOLDER/osx-x64" -G "Ninja Multi-Config" -DCMAKE_INSTALL_INCLUDEDIR=include -DCMAKE_INSTALL_LIBDIR=lib $ZITI_SDK_C_BRANCH_CMD

# run the below commands from microsoft developer command prompt
# uncomment to generate a new ziti.def
# defgen 32 build-win\x86\_deps\ziti-sdk-c-build\library\Release\ziti.dll
# copy ziti.def library
# cl /C /EP /I build-win/x86/_deps/ziti-sdk-c-src/includes /c library/sharp-errors.c > library/ZitiStatus.cs
# copy library/ZitiStatus.cs ../Ziti.NET.Standard/src/OpenZiti 

echo "Build from cmake using: "
echo "    cmake --build $BUILDFOLDER/osx-x64 --config Debug"
cmake --build "$BUILDFOLDER/osx-x64" --config Debug
echo "    cmake --build $BUILDFOLDER/osx-x64 --config Release"
cmake --build "$BUILDFOLDER/osx-x64" --config Release
echo " " 
echo "Or open $BUILDFOLDER/ziti-sdk.sln"

# install nuget
NUGET_PATH="$CSDK_HOME/../NuGet"
mkdir $NUGET_PATH
nuget pack $CSDK_HOME/../native-package-darwin.nuspec -Version 0.26.29 -OutputDirectory $CSDK_HOME
nuget push -source $NUGET_PATH $CSDK_HOME/Ziti.NET.Standard.native.darwin.0.26.29.nupkg

echo "Exiting..."
