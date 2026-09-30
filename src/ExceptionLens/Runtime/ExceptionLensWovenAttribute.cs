using System.ComponentModel;

namespace ExceptionLens.Runtime;

/// <summary>Added by the ExceptionLens weaver to every instrumented assembly. Not for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ExceptionLensWovenAttribute : Attribute;
