using ExceptionLens.Analyzers;
using ExceptionLens.Core;
using ExceptionLens.Formatting;
using ExceptionLens.Integrations.AspNetCore;
using ExceptionLens.Integrations.OpenTelemetry;
using ExceptionLens.Options;
using ExceptionLens.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ExceptionLens.Extensions;

/// <summary>Registration of ExceptionLens services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enables ExceptionLens. That is the only call your application needs:
    /// exceptions are detected automatically and the active OpenTelemetry span (<c>Activity</c>) is
    /// enriched with the root cause — the null expression, masked runtime values and the source location.
    ///
    /// <code>
    ///   builder.Services.AddOpenTelemetry().WithTracing(...);
    ///   builder.Services.AddExceptionLens();
    ///   // or with options:
    ///   builder.Services.AddExceptionLens(o => o.Masking.Mode = MaskingMode.All);
    /// </code>
    /// </summary>
    public static IServiceCollection AddExceptionLens(
        this IServiceCollection services,
        Action<ExceptionLensOptions>? configure = null)
    {
        var options = new ExceptionLensOptions();
        configure?.Invoke(options);

        // Instrumented code runs outside DI; give it the same options.
        ExceptionLensRuntime.Configure(options);
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
            sp.GetRequiredService<ExceptionLensOptions>()));

        // ── OpenTelemetry: automatic span enrichment ─────────────────────────
        services.TryAddSingleton<ExceptionLensTelemetry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExceptionLensHostedService>());

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
    /// Optional. Span enrichment is automatic; add this middleware only if you also want ExceptionLens to
    /// log its text banner for failed requests and/or return a problem-details body
    /// (<see cref="ExceptionLensOptions.IncludeDiagnosticsInResponse"/>).
    ///
    /// <code>
    ///   app.UseExceptionLens();
    ///   app.MapControllers();
    /// </code>
    /// </summary>
    public static IApplicationBuilder UseExceptionLens(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionLensMiddleware>();
}
