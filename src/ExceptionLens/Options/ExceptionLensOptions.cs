using ExceptionLens.Masking;

namespace ExceptionLens.Options;

/// <summary>Configuration for the ExceptionLens enrichment pipeline.</summary>
public sealed class ExceptionLensOptions
{
    /// <summary>
    /// Capture the values of parameters, locals and <c>this</c> at the throw site (requires the build-time
    /// instrumentation that the ExceptionLens package adds to your project). Default: true.
    /// </summary>
    public bool CaptureRuntimeValues { get; set; } = true;

    /// <summary>Report the source file, line and method of the failure. Default: true.</summary>
    public bool CaptureSourceLocation { get; set; } = true;

    /// <summary>
    /// Add ExceptionLens diagnostics to the current <see cref="System.Diagnostics.Activity"/> (span) so any
    /// OpenTelemetry exporter ships them. Default: true.
    /// </summary>
    public bool SendToOpenTelemetry { get; set; } = true;

    /// <summary>Limits for runtime value capture.</summary>
    public CaptureOptions Capture { get; } = new();

    /// <summary>Masking of captured values. Sensitive names are masked by default.</summary>
    public MaskingOptions Masking { get; } = new();

    /// <summary>
    /// Read source-code context lines when the source files exist on disk.
    /// Useful in development; production containers usually don't ship source files.
    /// </summary>
    public bool IncludeSourceContext { get; set; } = true;

    /// <summary>Number of lines of source context above/below the failing line.</summary>
    public int SourceContextLines { get; set; } = 2;

    /// <summary>Include enriched inner exception in the output.</summary>
    public bool IncludeInnerException { get; set; } = true;

    /// <summary>
    /// Emit the formatted ExceptionLens banner to ILogger when an exception is enriched by the
    /// optional ASP.NET Core middleware (<c>app.UseExceptionLens()</c>).
    /// </summary>
    public bool LogEnrichedDiagnostics { get; set; } = true;

    /// <summary>
    /// Attach ExceptionLens fields as structured log-scope properties when the middleware logs.
    /// Compatible with Serilog, Application Insights, OpenTelemetry logging.
    /// </summary>
    public bool StructuredLogging { get; set; } = true;

    /// <summary>
    /// Return a machine-readable RFC 7807 problem-details body from the optional ASP.NET Core
    /// middleware (development environments only).
    /// </summary>
    public bool IncludeDiagnosticsInResponse { get; set; } = false;

    /// <summary>
    /// Predicate evaluated before enrichment.
    /// Return false to skip enrichment for a given exception (e.g. OperationCanceledException).
    /// </summary>
    public Func<Exception, bool> ShouldEnrich { get; set; } = static ex =>
        ex is not OperationCanceledException;
}
