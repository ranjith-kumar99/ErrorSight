using System.Diagnostics.CodeAnalysis;
using ErrorSight.Options;

namespace ErrorSight.Runtime;

/// <summary>
/// Process-wide runtime state for build-time instrumentation. Injected filters run before (and
/// independently of) dependency injection, so their configuration lives here.
/// <c>services.AddErrorSight(...)</c> calls <see cref="Configure"/> for you.
/// </summary>
public static class ErrorSightRuntime
{
    private static ErrorSightOptions s_options = new();

    /// <summary>The options used by value capture (defaults until <see cref="Configure"/> is called).</summary>
    public static ErrorSightOptions Options => Volatile.Read(ref s_options);

    /// <summary>Replaces the options used by value capture.</summary>
    public static void Configure(ErrorSightOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Volatile.Write(ref s_options, options);
    }

    /// <summary>
    /// Gets the values captured for <paramref name="exception"/> in each instrumented frame it passed
    /// through, innermost (closest to the throw) first.
    /// </summary>
    public static bool TryGetCapturedFrames(Exception exception, [NotNullWhen(true)] out IReadOnlyList<CapturedFrame>? frames)
    {
        ArgumentNullException.ThrowIfNull(exception);
        frames = CaptureStore.Get(exception);
        return frames is not null;
    }
}
