using DotNetDistributedApp.Events.TopicInitialiser;

var builder = Host.CreateApplicationBuilder(args);
builder
    .Services.AddHostedService<Worker>()
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(Worker.ActivitySourceName));

var host = builder.Build();
host.Run();
