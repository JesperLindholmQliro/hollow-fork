#!/bin/sh
export DOTNET_ROOT=/tmp/.dotnet
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_NOLOGO=1
export PATH="$DOTNET_ROOT:$PATH"
exec /tmp/.dotnet/dotnet "$@"
