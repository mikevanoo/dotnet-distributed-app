namespace DotNetDistributedApp.Api;

public static class CoreWebApplicationExtensions
{
    public static WebApplication UseCoreMiddleware(this WebApplication webApplication)
    {
        webApplication.UseExceptionHandler();
        webApplication.UseOutputCache();

        if (webApplication.Environment.IsDevelopment())
        {
            webApplication.MapOpenApi();
            webApplication.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/openapi/v1.json", "v1");
            });
        }

        return webApplication;
    }
}
