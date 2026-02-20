using System.ComponentModel.DataAnnotations;
using DotNetDistributedApp.Api.Common.Events;
using Microsoft.AspNetCore.Mvc;

namespace DotNetDistributedApp.Api.Events;

public static class EventsWebApplicationExtensions
{
    public static WebApplication MapEventsEndpoints(this WebApplication webApplication)
    {
        var eventsGroup = webApplication.MapGroup("/events");
        eventsGroup.MapPost(
            "/simple-event",
            async (
                [FromBody] [Required] SimpleEventRequest body,
                [FromServices] IEventsService<SimpleEventPayloadDto> eventsService
            ) =>
                await eventsService.SendEvent(
                    Topics.Common,
                    new SimpleEventPayloadDto(Guid.NewGuid().ToString(), body.Value)
                )
        );
        eventsGroup.MapPost(
            "/failing-event",
            async ([FromServices] IEventsService<FailingEventPayloadDto> eventsService) =>
                await eventsService.SendEvent(Topics.Common, new FailingEventPayloadDto(Guid.NewGuid().ToString()))
        );

        return webApplication;
    }
}
