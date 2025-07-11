#!/usr/bin/env -S pwsh -noprofile

function Write-Info
{
    param (
        [string] $Message
    )
    Write-Host "Fixing via '$Message'" -ForegroundColor Green
}

Write-Info "dotnet format analyzers"
dotnet format analyzers

Write-Info "dotnet csharpier"
dotnet csharpier format .
