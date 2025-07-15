using FluentResults;

namespace DotNetDistributedApp.Api.Common.Errors;

public class NotFoundError : Error
{
    public NotFoundError(string message)
    {
        Message = message;
    }
}
