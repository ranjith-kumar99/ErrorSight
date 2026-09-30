using System.ComponentModel;

namespace ErrorSight.Runtime;

/// <summary>Added by the ErrorSight weaver to every instrumented assembly. Not for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ErrorSightWovenAttribute : Attribute;
