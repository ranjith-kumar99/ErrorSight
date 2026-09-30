namespace ExceptionLens.Options;

/// <summary>Configuration for the ExceptionLens enrichment pipeline.</summary>
public sealed class ExceptionLensOptions
{
    /// <summary>
    /// Read source-code context lines from PDB when available.
    /// Useful in development; usually disabled in production containers
    /// that don't ship source files.
    /// </summary>
    public bool IncludeSourceContext { get; set; } = true;

    /// <summary>Number of lines of source context above/below the failing line.</summary>
    public int SourceContextLines { get; set; } = 2;

    /// <summary>Include enriched inner exception in the output.</summary>
    public bool IncludeInnerException { get; set; } = true;

    /// <summary>
    /// Emit the formatted ExceptionLens banner to ILogger when an exception
    /// is enriched through middleware / worker / global handler.
    /// </summary>
    public bool LogEnrichedDiagnostics { get; set; } = true;

    /// <summary>
    /// Include structured JSON diagnostics as a log scope property named "ExceptionDiagnostics".
    /// Compatible with Serilog, Application Insights, OpenTelemetry.
    /// </summary>
    public bool StructuredLogging { get; set; } = true;

    /// <summary>
    /// Return a machine-readable RFC 7807 problem-details body from the
    /// ASP.NET Core middleware (development environments only).
    /// </summary>
    public bool IncludeDiagnosticsInResponse { get; set; } = false;

    /// <summary>
    /// Predicate evaluated before enrichment.
    /// Return false to skip enrichment for a given exception (e.g. OperationCanceledException).
    /// </summary>
    public Func<Exception, bool> ShouldEnrich { get; set; } = static ex =>
        ex is not OperationCanceledException;
}
