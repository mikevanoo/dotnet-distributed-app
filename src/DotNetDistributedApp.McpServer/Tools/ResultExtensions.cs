using DotNetDistributedApp.Api.Common.Errors;
using FluentResults;
using ModelContextProtocol;

namespace DotNetDistributedApp.McpServer.Tools;

internal static class ResultExtensions
{
    /// <summary>
    /// Unwraps a successful result, or fails the tool call with a message the calling model can read and act on.
    /// The MCP SDK replaces the message of every exception except <see cref="McpException"/> with a bare
    /// "An error occurred.", so a failure has to leave a tool as an <see cref="McpException"/> for the caller to
    /// learn anything at all - including enough to correct its own request and try again.
    /// </summary>
    /// <param name="result">The result to unwrap.</param>
    /// <param name="notFoundHint">
    /// Appended only when the failure is a <see cref="NotFoundError"/>, so that a hint about resolving a station key
    /// does not get attached to an unrelated validation failure.
    /// </param>
    public static T ValueOrToolError<T>(this Result<T> result, string? notFoundHint = null)
    {
        if (result.IsSuccess)
        {
            return result.Value;
        }

        var message = string.Join(" ", result.Errors.Select(error => error.Message));

        throw new McpException(
            notFoundHint is not null && result.HasError<NotFoundError>() ? $"{message} {notFoundHint}" : message
        );
    }
}
