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
                [FromBody] [Required] Event1Request body,
                [FromServices] IEventsService<SimpleEventPayloadDto> eventsService
            ) =>
                await eventsService.SendEvent(
                    Topics.Common,
                    new SimpleEventPayloadDto(Guid.NewGuid().ToString(), body.Value)
                )
        );

        return webApplication;
    }
}
