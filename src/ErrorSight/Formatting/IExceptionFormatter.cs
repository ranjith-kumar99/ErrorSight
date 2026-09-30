using ErrorSight.Core;

namespace ErrorSight.Formatting;

public interface IExceptionFormatter
{
    string Format(ExceptionDiagnostics diagnostics);
}
