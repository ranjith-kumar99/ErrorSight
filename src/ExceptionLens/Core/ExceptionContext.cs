using System.Runtime.CompilerServices;

namespace ExceptionLens.Core;

/// <summary>
/// Keys stored inside Exception.Data to carry enrichment context across stack frames.
/// </summary>
internal static class ExceptionDataKeys
{
    public const string CapturedValues = "ExceptionLens.CapturedValues";
    public const string CallerFile = "ExceptionLens.CallerFile";
    public const string CallerMethod = "ExceptionLens.CallerMethod";
    public const string CallerLine = "ExceptionLens.CallerLine";
    public const string NullExpression = "ExceptionLens.NullExpression";
    public const string FailingExpression = "ExceptionLens.FailingExpression";
    public const string CorrelationId = "ExceptionLens.CorrelationId";
    public const string AdditionalData = "ExceptionLens.AdditionalData";
}

/// <summary>
/// Fluent capture helper stored on Exception.Data when developers
/// attach runtime context manually.
/// </summary>
public sealed class CapturedContext
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    public string? NullExpression { get; set; }
    public string? FailingExpression { get; set; }
    public string? CallerFile { get; set; }
    public string? CallerMethod { get; set; }
    public int CallerLine { get; set; }
    public Dictionary<string, object?> AdditionalData { get; } = new(StringComparer.OrdinalIgnoreCase);
}
