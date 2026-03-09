using DotNetDistributedApp.ServiceDefaults;
using Microsoft.AspNetCore.StaticFiles;
using Scalar.AspNetCore;

namespace DotNetDistributedApp.Api;

public static class CoreWebApplicationExtensions
{
    public static WebApplication UseCoreMiddleware(this WebApplication webApplication)
    {
        webApplication.UseExceptionHandler();
        webApplication.UseOutputCache();

        // ReSharper disable once UseObjectOrCollectionInitializer - using this would wipe out the default mappings.
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".yaml"] = "application/yaml";
        webApplication.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });

        if (webApplication.Environment.IsDevelopment())
        {
            webApplication.MapOpenApi();
            webApplication.MapScalarApiReference();
            // add the manually generated geoip OpenApi here because we can't add it to the geoip container itself
            webApplication.MapScalarApiReference(
                $"/scalar/{ResourceNames.GeoIpApi}",
                options =>
                {
                    options.WithTitle("GeoIP API").WithOpenApiRoutePattern($"/openapi/{ResourceNames.GeoIpApi}.yaml");
                    options.AddServer(
                        webApplication.Configuration[$"services:{ResourceNames.GeoIpApi}:http:0"]
                            ?? throw new InvalidOperationException($"Base url for '{ResourceNames.GeoIpApi}' not found.")
                    );
                }
            );
        }

        return webApplication;
    }
}
