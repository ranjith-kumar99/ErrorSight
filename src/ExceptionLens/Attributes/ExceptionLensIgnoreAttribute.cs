namespace ExceptionLens;

/// <summary>
/// Excludes a method, a type (and its nested types) or a whole assembly from ExceptionLens
/// build-time instrumentation. Use it for hot paths where you want zero instrumentation.
/// To disable weaving for a whole project, set <c>&lt;ExceptionLensWeave&gt;false&lt;/ExceptionLensWeave&gt;</c>.
/// </summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property |
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly | AttributeTargets.Module,
    Inherited = false)]
public sealed class ExceptionLensIgnoreAttribute : Attribute;
