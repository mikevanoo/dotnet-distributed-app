#!/usr/bin/env -S pwsh -noprofile

dotnet nswag openapi2csclient /Input:https://localhost:7376/openapi/v2.json /Output:WeatherApiDtos.cs /Namespace:DotNetDistributedApp.McpServer.Clients /GenerateClientClasses:false /JsonLibrary:SystemTextJson
