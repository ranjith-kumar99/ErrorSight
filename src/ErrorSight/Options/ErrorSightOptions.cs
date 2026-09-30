using ErrorSight.Masking;

namespace ErrorSight.Options;

/// <summary>Configuration for ErrorSight.</summary>
public sealed class ErrorSightOptions
{
    /// <summary>
    /// How much application data is captured. Default: <see cref="Options.DataCapture.None"/>, which is diagnostic
    /// structure only (exception type, source location, method, the null expression and its origin) and never
    /// runtime values. Use <see cref="Options.DataCapture.Metadata"/> for counts, lengths and signs, or
    /// <see cref="Options.DataCapture.Values"/> to capture values, masked by <see cref="Masking"/>.
    /// </summary>
    public DataCapture DataCapture { get; set; } = DataCapture.None;

    /// <summary>Report the source file, line and method of the failure. Default: true.</summary>
    public bool CaptureSourceLocation { get; set; } = true;

    /// <summary>
    /// Add ErrorSight diagnostics to the current <see cref="System.Diagnostics.Activity"/> (span) so any
    /// OpenTelemetry exporter ships them. Default: true.
    /// </summary>
    public bool SendToOpenTelemetry { get; set; } = true;

    /// <summary>Limits and switches for capture at throw time.</summary>
    public CaptureOptions Capture { get; } = new();

    /// <summary>
    /// Masking of captured values (<see cref="Options.DataCapture.Values"/>). Sensitive names and members declared
    /// sensitive are masked by default.
    /// </summary>
    public MaskingOptions Masking { get; } = new();

    /// <summary>
    /// Read source lines from disk, when the files exist, for the text banner and to name the failing expression
    /// in methods that are not instrumented. Default: false, so ErrorSight does not read or capture source code.
    /// </summary>
    public bool IncludeSourceContext { get; set; }

    /// <summary>Number of lines of source context above/below the failing line.</summary>
    public int SourceContextLines { get; set; } = 2;

    /// <summary>Include enriched inner exception in the output.</summary>
    public bool IncludeInnerException { get; set; } = true;

    /// <summary>
    /// Emit the formatted ErrorSight banner to ILogger when an exception is enriched by the
    /// optional ASP.NET Core middleware (<c>app.UseErrorSight()</c>).
    /// </summary>
    public bool LogEnrichedDiagnostics { get; set; } = true;

    /// <summary>
    /// Attach ErrorSight fields as structured log-scope properties when the middleware logs.
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
