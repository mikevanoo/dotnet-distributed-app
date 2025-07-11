#!/usr/bin/env -S pwsh -noprofile

function Write-Info
{
    param (
        [string] $Message
    )
    Write-Host "Checking via '$Message'" -ForegroundColor Green
}

Write-Info "dotnet format analyzers"
dotnet format analyzers --verify-no-changes

Write-Info "dotnet csharpier"
dotnet csharpier check .
