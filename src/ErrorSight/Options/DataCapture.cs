namespace ErrorSight.Options;

/// <summary>
/// How much application data ErrorSight captures. Every level reports the diagnostic <em>structure</em>: the
/// exception type, method, source file and line, type and member names, the expression that was null and where
/// it came from. Higher levels add information about the values themselves.
/// </summary>
public enum DataCapture
{
    /// <summary>
    /// Level 1 (default): structure only. No runtime values, strings, collection contents, exception messages or
    /// exception data are captured, so no application data leaves the process.
    /// </summary>
    None = 0,

    /// <summary>
    /// Level 2: adds metadata about values without their content, namely collection counts, string lengths and
    /// the sign of numbers (negative, zero, positive). Values declared or named sensitive report no metadata.
    /// </summary>
    Metadata = 1,

    /// <summary>
    /// Level 3 (explicit opt-in): adds the values themselves and exception messages, masked according to
    /// <see cref="ErrorSightOptions.Masking"/>. Values declared sensitive are never captured.
    /// </summary>
    Values = 2,
}
