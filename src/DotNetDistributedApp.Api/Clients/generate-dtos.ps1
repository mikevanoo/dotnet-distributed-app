#!/usr/bin/env -S pwsh -noprofile

dotnet nswag openapi2csclient /Input:https://localhost:7060/openapi/v1.json /Output:SpatialApiDtos.cs /Namespace:DotNetDistributedApp.Api.Clients /GenerateClientClasses:false /JsonLibrary:SystemTextJson
