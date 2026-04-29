using System.ComponentModel.DataAnnotations;
using DotNetDistributedApp.Api.Common.Events;
using Microsoft.AspNetCore.Mvc;

namespace DotNetDistributedApp.Api.Events;

public static class EventsWebApplicationExtensions
{
    public static WebApplication MapEventsEndpoints(this WebApplication webApplication)
    {
        var api = webApplication.NewVersionedApi("Events");
        var v1v2 = api.MapGroup("/v{version:apiVersion}/events").HasApiVersion(1.0).HasApiVersion(2.0);

        v1v2.MapPost(
            "/simple-event",
            async ([FromBody] [Required] SimpleEventRequest body, [FromServices] IEventsService eventsService) =>
            {
                await eventsService.SendEvent(
                    Topics.Common,
                    new SimpleEventPayloadDto(Guid.NewGuid().ToString(), body.Value)
                );
            }
        );

        v1v2.MapPost(
            "/failing-event",
            async ([FromServices] IEventsService eventsService) =>
                await eventsService.SendEvent(Topics.Common, new FailingEventPayloadDto(Guid.NewGuid().ToString()))
        );

        return webApplication;
    }
}
