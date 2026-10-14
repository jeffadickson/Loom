#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/src/Loom.Demo"
dotnet run -- --memory-demo
