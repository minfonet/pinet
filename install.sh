#!/usr/bin/env bash
# Pi.NET installer.
# Pi.NET is not published to NuGet.org: this packs the tool (unless an existing
# package feed is supplied) and installs the global tool from that local feed.
#
# Usage:
#   ./install.sh                   # pack from source, then install
#   ./install.sh /path/to/feed     # install from a directory containing PiNet.*.nupkg
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
source_dir="${1:-$root/artifacts/packages}"

if [ ! -d "$source_dir" ] || ! ls "$source_dir"/PiNet.*.nupkg >/dev/null 2>&1; then
    dotnet pack "$root/src/PiNet.Cli/PiNet.Cli.csproj" -c Release -o "$source_dir"
fi

dotnet tool install -g PiNet --version 0.1.0 --add-source "$source_dir"
