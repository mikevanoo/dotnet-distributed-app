using System.Net;
using DotNetDistributedApp.Api.Errors;
using FluentResults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DotNetDistributedApp.Api;

public static class FluentResultsExtensions
{
    public static Results<Ok<T>, NotFound, ProblemHttpResult> ToApiResponse<T>(this Result<T> result)
    {
        if (result.IsFailed)
        {
            return ToApiFailedResponse(result);
        }

        if (result is { IsSuccess: true, Value: null })
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(result.Value);
    }

    private static Results<Ok<T>, NotFound, ProblemHttpResult> ToApiFailedResponse<T>(Result<T> result)
    {
        if (result.HasError<NotFoundError>())
        {
            return TypedResults.NotFound();
        }

        return ToProblemDetails(result.Errors);
    }

    private static ProblemHttpResult ToProblemDetails(IReadOnlyList<IError> errors)
    {
        var problemDetails = new ProblemDetails
        {
            Status = (int)HttpStatusCode.BadRequest,
            Title = "The request could not be processed by the server",
            Detail = string.Join(';', errors.Select(e => e.Message)),
        };

        if (errors.Count != 0)
        {
            foreach (var item in errors[0].Metadata)
            {
                problemDetails.Extensions.Add(item.Key, item.Value);
            }
        }

        return TypedResults.Problem(problemDetails);
    }
}
