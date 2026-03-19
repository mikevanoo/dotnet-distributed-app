#!/usr/bin/env bash
set -euo pipefail

echo -e "\033[32mChecking via 'dotnet format analyzers'\033[0m"
dotnet format analyzers --verify-no-changes

echo -e "\033[32mChecking via 'dotnet csharpier'\033[0m"
dotnet csharpier check .
