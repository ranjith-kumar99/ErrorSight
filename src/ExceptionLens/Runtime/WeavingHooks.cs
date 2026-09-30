using System.ComponentModel;

namespace ExceptionLens.Runtime;

/// <summary>
/// Entry point called by code the ExceptionLens weaver injects into your methods. Not for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class WeavingHooks
{
    [ThreadStatic]
    private static bool t_capturing;

    /// <summary>
    /// Invoked from an injected exception filter while the CLR searches for a handler — before the stack
    /// unwinds, so <paramref name="values"/> hold exactly what the method saw when the exception occurred.
    /// Always returns <c>false</c>: the filter never catches, and the exception continues unchanged.
    /// </summary>
    public static bool OnFilter(
        object? exception,
        RuntimeMethodHandle method,
        RuntimeTypeHandle declaringType,
        int methodId,
        object?[]? values)
    {
        // Re-entrancy guard: an exception raised while capturing must not trigger another capture.
        if (t_capturing || exception is not Exception ex) return false;

        t_capturing = true;
        try
        {
            CaptureStore.Record(ex, method, declaringType, methodId, values);
        }
        catch
        {
            // Capture is best-effort and must never disturb the application's exception flow.
        }
        finally
        {
            t_capturing = false;
        }

        return false;
    }
}
