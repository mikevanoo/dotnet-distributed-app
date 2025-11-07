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
            "/event1",
            async (
                [FromBody] [Required] Event1Request body,
                [FromServices] IEventsService<Event1PayloadDto> eventsService
            ) =>
                await eventsService.SendEvent(
                    Topics.Common,
                    new Event1PayloadDto(Guid.NewGuid().ToString(), body.Value)
                )
        );

        return webApplication;
    }
}
