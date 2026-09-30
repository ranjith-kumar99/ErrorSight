using ExceptionLens.Core;

namespace ExceptionLens.Formatting;

public interface IExceptionFormatter
{
    string Format(ExceptionDiagnostics diagnostics);
}
