#!/bin/sh
# Eklentiyi sunucuda Docker icindeki .NET SDK ile derler.
# Kullanim: sh build.sh   (cikti: ./out/Jellyfin.Plugin.XtreamStrm.dll)
set -e
cd "$(dirname "$0")"
rm -rf out bin obj
docker run --rm -u "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish -c Release -o /src/out --nologo -v q
ls -la out
