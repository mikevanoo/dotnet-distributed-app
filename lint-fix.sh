#!/usr/bin/env bash
set -euo pipefail

echo -e "\033[32mFixing via 'dotnet format analyzers'\033[0m"
dotnet format analyzers

echo -e "\033[32mFixing via 'dotnet csharpier'\033[0m"
dotnet csharpier format .
