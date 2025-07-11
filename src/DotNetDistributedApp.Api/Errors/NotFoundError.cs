using FluentResults;

namespace DotNetDistributedApp.Api.Errors;

public class NotFoundError : Error
{
    public NotFoundError(string message)
    {
        Message = message;
    }
}
