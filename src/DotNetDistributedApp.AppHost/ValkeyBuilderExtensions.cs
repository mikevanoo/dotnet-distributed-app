namespace DotNetDistributedApp.AppHost;

// Source adapted from https://github.com/dotnet/aspire/tree/main/src/Aspire.Hosting.Redis
// If/when the Aspire.Hosting.Valkey package includes RedisInsights support, this can be removed.
public static class ValkeyBuilderExtensions
{
    public static IResourceBuilder<ValkeyResource> WithRedisInsightForValkey(
        this IResourceBuilder<ValkeyResource> builder,
        Action<IResourceBuilder<RedisInsightResource>>? configureContainer = null,
        string? containerName = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (
            builder.ApplicationBuilder.Resources.OfType<RedisInsightResource>().SingleOrDefault() is
            { } existingRedisCommanderResource
        )
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(
                existingRedisCommanderResource
            );
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= "redisinsight";

            var resource = new RedisInsightResource(containerName);
            var resourceBuilder = builder
                .ApplicationBuilder.AddResource(resource)
                .WithImage(RedisContainerImageTags.RedisInsightImage, RedisContainerImageTags.RedisInsightTag)
                .WithImageRegistry(RedisContainerImageTags.RedisInsightRegistry)
                .WithHttpEndpoint(targetPort: 5540, name: "http")
                .WithEnvironment(context =>
                {
                    var redisInstances = builder.ApplicationBuilder.Resources.OfType<ValkeyResource>();

                    if (!redisInstances.Any())
                    {
                        // No-op if there are no Redis resources present.
                        return;
                    }

                    var counter = 1;

                    foreach (var redisInstance in redisInstances)
                    {
                        // RedisInsight assumes Redis is being accessed over a default Aspire container network and hardcodes the resource address
                        context.EnvironmentVariables.Add($"RI_REDIS_HOST{counter}", redisInstance.Name);
                        context.EnvironmentVariables.Add(
                            $"RI_REDIS_PORT{counter}",
                            redisInstance.PrimaryEndpoint.TargetPort!.Value
                        );
                        context.EnvironmentVariables.Add($"RI_REDIS_ALIAS{counter}", redisInstance.Name);
                        if (redisInstance.PasswordParameter is not null)
                        {
                            context.EnvironmentVariables.Add(
                                $"RI_REDIS_PASSWORD{counter}",
                                redisInstance.PasswordParameter
                            );
                        }

                        counter++;
                    }
                })
                .WithRelationship(builder.Resource, "RedisInsight")
                .ExcludeFromManifest();

            configureContainer?.Invoke(resourceBuilder);

            return builder;
        }
    }
}

internal static class RedisContainerImageTags
{
    public const string RedisInsightRegistry = "docker.io";
    public const string RedisInsightImage = "redis/redisinsight";
    public const string RedisInsightTag = "2.70";
}

public class RedisInsightResource(string name) : ContainerResource(name)
{
    internal const string PrimaryEndpointName = "http";

    private EndpointReference? _primaryEndpoint;

    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);
}
