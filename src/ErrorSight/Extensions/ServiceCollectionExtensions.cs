using ErrorSight.Analyzers;
using ErrorSight.Core;
using ErrorSight.Formatting;
using ErrorSight.Integrations.AspNetCore;
using ErrorSight.Integrations.OpenTelemetry;
using ErrorSight.Options;
using ErrorSight.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ErrorSight.Extensions;

/// <summary>Registration of ErrorSight services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enables ErrorSight. That is the only call your application needs:
    /// exceptions are detected automatically and the active OpenTelemetry span (<c>Activity</c>) is
    /// enriched with the root cause: the null expression, where it came from and the source location.
    /// No application data is captured unless you opt in with <see cref="ErrorSightOptions.DataCapture"/>.
    ///
    /// <code>
    ///   builder.Services.AddOpenTelemetry().WithTracing(...);
    ///   builder.Services.AddErrorSight();
    ///   // or with options:
    ///   builder.Services.AddErrorSight(o => o.DataCapture = DataCapture.Values);   // opt in to values (masked)
    /// </code>
    /// </summary>
    public static IServiceCollection AddErrorSight(
        this IServiceCollection services,
        Action<ErrorSightOptions>? configure = null)
    {
        var options = new ErrorSightOptions();
        configure?.Invoke(options);

        // Instrumented code runs outside DI; give it the same options.
        ErrorSightRuntime.Configure(options);
        services.AddSingleton(options);

        // ── Analyzers ─────────────────────────────────────────────────────────
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionAnalyzer, NullReferenceAnalyzer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionAnalyzer, ArgumentNullAnalyzer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionAnalyzer, KeyNotFoundAnalyzer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionAnalyzer, IndexOutOfRangeAnalyzer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionAnalyzer, InvalidOperationAnalyzer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionAnalyzer, AggregateExceptionAnalyzer>());

        // ── Formatters ───────────────────────────────────────────────────────
        services.TryAddSingleton<TextExceptionFormatter>();
        services.TryAddSingleton<JsonExceptionFormatter>();
        services.TryAddSingleton<IExceptionFormatter>(sp => sp.GetRequiredService<TextExceptionFormatter>());

        // ── Core enricher ────────────────────────────────────────────────────
        services.TryAddSingleton<ExceptionEnricher>(sp => new ExceptionEnricher(
            sp.GetRequiredService<IEnumerable<IExceptionAnalyzer>>(),
            sp.GetRequiredService<ErrorSightOptions>()));

        // ── OpenTelemetry: automatic span enrichment ─────────────────────────
        services.TryAddSingleton<ErrorSightTelemetry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ErrorSightHostedService>());

        return services;
    }

    /// <summary>
    /// Adds a custom exception analyzer to the pipeline.
    /// </summary>
    public static IServiceCollection AddExceptionAnalyzer<TAnalyzer>(
        this IServiceCollection services)
        where TAnalyzer : class, IExceptionAnalyzer
    {
        services.AddSingleton<IExceptionAnalyzer, TAnalyzer>();
        return services;
    }
}

/// <summary>
/// IApplicationBuilder extensions for the ASP.NET Core middleware.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Optional. Span enrichment is automatic; add this middleware only if you also want ErrorSight to
    /// log its text banner for failed requests and/or return a problem-details body
    /// (<see cref="ErrorSightOptions.IncludeDiagnosticsInResponse"/>).
    ///
    /// <code>
    ///   app.UseErrorSight();
    ///   app.MapControllers();
    /// </code>
    /// </summary>
    public static IApplicationBuilder UseErrorSight(this IApplicationBuilder app) =>
        app.UseMiddleware<ErrorSightMiddleware>();
}
