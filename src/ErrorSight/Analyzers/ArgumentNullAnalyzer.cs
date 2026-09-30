using ErrorSight.Core;

namespace ErrorSight.Analyzers;

/// <summary>Enriches ArgumentNullException and ArgumentException.</summary>
public sealed class ArgumentNullAnalyzer : IExceptionAnalyzer
{
    public bool CanAnalyze(Exception exception) =>
        exception is ArgumentNullException or ArgumentException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        if (exception is ArgumentNullException ane)
        {
            diagnostics.ParameterName = ane.ParamName;

            if (ane.ParamName is not null)
            {
                diagnostics.NullExpression = ane.ParamName;
                diagnostics.PossibleCause = $"Parameter '{ane.ParamName}' is null.";
                diagnostics.Suggestion = $"Ensure the caller passes a non-null value for '{ane.ParamName}'.";
            }
        }
        else if (exception is ArgumentException ae)
        {
            diagnostics.ParameterName = ae.ParamName;

            if (ae.ParamName is not null)
            {
                diagnostics.PossibleCause = $"Invalid value for parameter '{ae.ParamName}'.";
                diagnostics.Suggestion = $"Validate the value passed for '{ae.ParamName}' before calling this method.";
            }
        }

        if (diagnostics.CallChain.Count > 0)
        {
            var callee = diagnostics.CallChain[0];
            var caller = diagnostics.CallChain.Count > 1 ? diagnostics.CallChain[1] : null;
            if (caller is not null)
            {
                diagnostics.AdditionalData["calledFrom"] = caller.Display;
                diagnostics.AdditionalData["callee"] = callee.Display;
            }
        }
    }
}
