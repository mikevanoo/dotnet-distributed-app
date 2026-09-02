using System.Runtime.CompilerServices;

namespace DotNetDistributedApp.DeploymentTests;

/// <summary>A <see cref="FactAttribute" /> that runs only when the cluster tiers are enabled.</summary>
public sealed class DeploymentFactAttribute : FactAttribute
{
    public DeploymentFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    )
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DeploymentTestEnvironment.ClusterTestsSkipReason;
        SkipUnless = nameof(DeploymentTestEnvironment.ClusterTestsEnabled);
        SkipType = typeof(DeploymentTestEnvironment);
    }
}

/// <summary>A <see cref="TheoryAttribute" /> that runs only when the cluster tiers are enabled.</summary>
public sealed class DeploymentTheoryAttribute : TheoryAttribute
{
    public DeploymentTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    )
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DeploymentTestEnvironment.ClusterTestsSkipReason;
        SkipUnless = nameof(DeploymentTestEnvironment.ClusterTestsEnabled);
        SkipType = typeof(DeploymentTestEnvironment);
    }
}

/// <summary>A <see cref="FactAttribute" /> that runs only when the chart tier is enabled.</summary>
public sealed class ChartFactAttribute : FactAttribute
{
    public ChartFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    )
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DeploymentTestEnvironment.ChartTestsSkipReason;
        SkipUnless = nameof(DeploymentTestEnvironment.ChartTestsEnabled);
        SkipType = typeof(DeploymentTestEnvironment);
    }
}

/// <summary>A <see cref="TheoryAttribute" /> that runs only when the chart tier is enabled.</summary>
public sealed class ChartTheoryAttribute : TheoryAttribute
{
    public ChartTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1
    )
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DeploymentTestEnvironment.ChartTestsSkipReason;
        SkipUnless = nameof(DeploymentTestEnvironment.ChartTestsEnabled);
        SkipType = typeof(DeploymentTestEnvironment);
    }
}
