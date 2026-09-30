using System.Reflection;
using System.Runtime.CompilerServices;
using ExceptionLens.Core;

namespace ExceptionLens.Extensions;

/// <summary>
/// Fluent extension methods for attaching runtime context to exceptions
/// without modifying control flow.
/// </summary>
public static class ExceptionExtensions
{
    /// <summary>
    /// Attaches an anonymous object of runtime values to the exception so
    /// ExceptionLens can include them in the enriched diagnostics.
    ///
    /// <code>
    ///   catch (NullReferenceException ex)
    ///   {
    ///       throw ex.Capture(new { customer, customerAddress = customer?.Address });
    ///   }
    /// </code>
    /// </summary>
    public static TException Capture<TException>(
        this TException exception,
        object? values,
        string? nullExpression = null,
        [CallerMemberName] string callerMethod = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
        where TException : Exception
    {
        if (values is not null)
        {
            var dict = BuildValueDictionary(values);
            if (exception.Data.Contains(ExceptionDataKeys.CapturedValues) &&
                exception.Data[ExceptionDataKeys.CapturedValues] is Dictionary<string, object?> existing)
            {
                foreach (var (k, v) in dict)
                    existing[k] = v;
            }
            else
            {
                exception.Data[ExceptionDataKeys.CapturedValues] = dict;
            }
        }

        if (nullExpression is not null)
            exception.Data[ExceptionDataKeys.NullExpression] = nullExpression;

        exception.Data[ExceptionDataKeys.CallerMethod] = callerMethod;
        exception.Data[ExceptionDataKeys.CallerFile] = callerFile;
        exception.Data[ExceptionDataKeys.CallerLine] = callerLine;

        return exception;
    }

    /// <summary>
    /// Attaches a named value to the exception's captured context.
    /// Useful when you only have one value to add.
    /// </summary>
    public static TException CaptureValue<TException>(
        this TException exception,
        string name,
        object? value)
        where TException : Exception
    {
        if (!exception.Data.Contains(ExceptionDataKeys.CapturedValues))
            exception.Data[ExceptionDataKeys.CapturedValues] = new Dictionary<string, object?>(StringComparer.Ordinal);

        ((Dictionary<string, object?>)exception.Data[ExceptionDataKeys.CapturedValues]!)[name] = value;
        return exception;
    }

    /// <summary>
    /// Attaches a correlation ID (request ID, trace ID) to the exception.
    /// </summary>
    public static TException WithCorrelationId<TException>(this TException exception, string correlationId)
        where TException : Exception
    {
        exception.Data[ExceptionDataKeys.CorrelationId] = correlationId;
        return exception;
    }

    /// <summary>
    /// Attaches the name of the null sub-expression explicitly.
    /// </summary>
    public static TException NullAt<TException>(this TException exception, string expression)
        where TException : Exception
    {
        exception.Data[ExceptionDataKeys.NullExpression] = expression;
        return exception;
    }

    // ── Reflection helper ────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildValueDictionary(object values)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Support both anonymous types and Dictionary<string, object?>
        if (values is Dictionary<string, object?> d)
        {
            foreach (var (k, v) in d)
                dict[k] = v;
            return dict;
        }

        foreach (var prop in values.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try { dict[prop.Name] = prop.GetValue(values); }
            catch { /* skip inaccessible properties */ }
        }

        return dict;
    }
}
