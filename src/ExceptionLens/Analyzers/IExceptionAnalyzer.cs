using ExceptionLens.Core;

namespace ExceptionLens.Analyzers;

/// <summary>
/// Plugin interface for enriching a specific exception type.
/// Implementations are registered in DI and composed by <see cref="ExceptionEnricher"/>.
/// </summary>
public interface IExceptionAnalyzer
{
    /// <summary>Returns true when this analyzer can enrich the given exception.</summary>
    bool CanAnalyze(Exception exception);

    /// <summary>
    /// Fills <paramref name="diagnostics"/> with type-specific fields.
    /// Called after the base stack-trace pass; may overwrite any field.
    /// </summary>
    void Enrich(ExceptionDiagnostics diagnostics, Exception exception);
}
