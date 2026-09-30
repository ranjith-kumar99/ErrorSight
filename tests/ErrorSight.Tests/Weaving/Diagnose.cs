using ErrorSight.Core;
using ErrorSight.Options;
using ErrorSight.Runtime;

namespace ErrorSight.Tests.Weaving;

/// <summary>Tests that touch the process-wide capture runtime must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeCollection
{
    public const string Name = "ErrorSight runtime";
}

/// <summary>
/// Runs woven sample code the way an application would (no try/catch around it in the sample),
/// then diagnoses the exception that escaped.
/// </summary>
internal static class Diagnose
{
    public static ErrorSightOptions Options(Action<ErrorSightOptions>? configure = null)
    {
        var options = new ErrorSightOptions { IncludeSourceContext = false };
        options.Capture.MaxCapturesPerSecond = 0;
        configure?.Invoke(options);
        ErrorSightRuntime.Configure(options);
        return options;
    }

    public static ExceptionDiagnostics Run(Action action, Action<ErrorSightOptions>? configure = null)
    {
        var options = Options(configure);
        var exception = Record.Exception(action);
        exception.Should().NotBeNull("the sample is expected to throw");
        return ExceptionEnricher.CreateDefault(options).Enrich(exception!);
    }

    public static async Task<ExceptionDiagnostics> RunAsync(Func<Task> action, Action<ErrorSightOptions>? configure = null)
    {
        var options = Options(configure);
        var exception = await Record.ExceptionAsync(action);
        exception.Should().NotBeNull("the sample is expected to throw");
        return ExceptionEnricher.CreateDefault(options).Enrich(exception!);
    }

    public static string? Text(this ExceptionDiagnostics d, string key) =>
        d.Values.TryGetValue(key, out var value) ? value?.ToString() : "<missing>";
}
