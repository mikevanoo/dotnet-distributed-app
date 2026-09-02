using System.Diagnostics;
using System.Text;

namespace DotNetDistributedApp.DeploymentTests.Infrastructure;

/// <summary>Runs <c>aspire</c> and <c>helm</c> for the chart tier and captures what they wrote.</summary>
public static class CommandLine
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'. Is it on the PATH?");

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var readOutput = ReadAllAsync(process.StandardOutput, standardOutput, cancellationToken);
        var readError = ReadAllAsync(process.StandardError, standardError, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(readOutput, readError);

        return new CommandResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    /// <summary>
    /// Walks up from the test binaries to the directory holding the solution file, which is where
    /// <c>aspire publish</c> and the paths in <see cref="DeploymentTestOptions" /> are anchored.
    /// </summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("DotNetDistributedApp.slnx").Length > 0)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find the repository root above '{AppContext.BaseDirectory}'.");
    }

    private static async Task ReadAllAsync(
        StreamReader reader,
        StringBuilder destination,
        CancellationToken cancellationToken
    ) => destination.Append(await reader.ReadToEndAsync(cancellationToken));
}

public record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Describe() =>
        $"exit code {ExitCode}{Environment.NewLine}{StandardOutput}{Environment.NewLine}{StandardError}";
}
