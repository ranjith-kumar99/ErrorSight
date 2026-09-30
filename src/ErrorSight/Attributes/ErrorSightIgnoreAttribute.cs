namespace ErrorSight;

/// <summary>
/// Excludes a method, a type (and its nested types) or a whole assembly from ErrorSight
/// build-time instrumentation. Use it for hot paths where you want zero instrumentation.
/// To disable weaving for a whole project, set <c>&lt;ErrorSightWeave&gt;false&lt;/ErrorSightWeave&gt;</c>.
/// </summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property |
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly | AttributeTargets.Module,
    Inherited = false)]
public sealed class ErrorSightIgnoreAttribute : Attribute;
