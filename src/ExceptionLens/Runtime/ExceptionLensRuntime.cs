using System.Diagnostics.CodeAnalysis;
using ExceptionLens.Options;

namespace ExceptionLens.Runtime;

/// <summary>
/// Process-wide runtime state for build-time instrumentation. Injected filters run before (and
/// independently of) dependency injection, so their configuration lives here.
/// <c>services.AddExceptionLens(...)</c> calls <see cref="Configure"/> for you.
/// </summary>
public static class ExceptionLensRuntime
{
    private static ExceptionLensOptions s_options = new();

    /// <summary>The options used by value capture (defaults until <see cref="Configure"/> is called).</summary>
    public static ExceptionLensOptions Options => Volatile.Read(ref s_options);

    /// <summary>Replaces the options used by value capture.</summary>
    public static void Configure(ExceptionLensOptions options)
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
