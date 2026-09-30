namespace ExceptionLens;

/// <summary>
/// Marks a property, field, parameter or whole type as sensitive: ExceptionLens never records its
/// value, only whether it was null.
///
/// Any attribute named <c>SensitiveAttribute</c>, <c>SensitiveDataAttribute</c>, <c>PersonalDataAttribute</c>
/// or <c>ProtectedPersonalDataAttribute</c> (e.g. ASP.NET Core Identity's), or deriving from
/// Microsoft.Extensions.Compliance's <c>DataClassificationAttribute</c>, is honoured the same way.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter |
    AttributeTargets.Class | AttributeTargets.Struct,
    Inherited = true)]
public sealed class SensitiveAttribute : Attribute;
