namespace ErrorSight.Options;

/// <summary>Limits for capture at throw time. The defaults keep capture cheap and bounded.</summary>
public sealed class CaptureOptions
{
    /// <summary>
    /// Record state in instrumented frames when exceptions pass through them. Set to false to switch capture off at
    /// runtime without rebuilding; the diagnosis then relies on the stack trace alone. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Instrumented frames captured per exception, innermost first. Default: 5.</summary>
    public int MaxFramesPerException { get; set; } = 5;

    /// <summary>How deep object graphs are walked (<c>customer.Address.City</c> is depth 2). Default: 3.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Members captured per object. Default: 20.</summary>
    public int MaxMembersPerObject { get; set; } = 20;

    /// <summary>
    /// Elements walked per collection. The count itself is reported from
    /// <see cref="DataCapture.Metadata"/> upwards. Default: 5.
    /// </summary>
    public int MaxCollectionItems { get; set; } = 5;

    /// <summary>Longer strings are truncated (<see cref="DataCapture.Values"/>). Default: 256.</summary>
    public int MaxStringLength { get; set; } = 256;

    /// <summary>
    /// Process-wide ceiling on frame captures per second, protecting against exception storms.
    /// 0 disables the limit. Default: 200.
    /// </summary>
    public int MaxCapturesPerSecond { get; set; } = 200;

    /// <summary>
    /// Also capture private fields and non-public auto-properties. Default: false. Only public auto-properties
    /// and public fields are captured.
    /// </summary>
    public bool IncludePrivateFields { get; set; }

    /// <summary>
    /// Return false to skip capture for an exception. Default skips OperationCanceledException,
    /// which is normal control flow (request aborted, timeout).
    /// </summary>
    public Func<Exception, bool> ShouldCapture { get; set; } = static ex => ex is not OperationCanceledException;
}
