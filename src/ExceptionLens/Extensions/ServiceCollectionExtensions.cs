using ExceptionLens.Analyzers;
using ExceptionLens.Core;
using ExceptionLens.Formatting;
using ExceptionLens.Integrations.AspNetCore;
using ExceptionLens.Integrations.Logging;
using ExceptionLens.Integrations.OpenTelemetry;
using ExceptionLens.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ExceptionLens.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers ExceptionLens enrichment, formatters, and all built-in analyzers.
    ///
    /// <code>
    ///   builder.Services.AddExceptionLens();
    ///   // or with options:
    ///   builder.Services.AddExceptionLens(opt => opt.IncludeDiagnosticsInResponse = true);
    /// </code>
    /// </summary>
    public static IServiceCollection AddExceptionLens(
        this IServiceCollection services,
        Action<ExceptionLensOptions>? configure = null)
    {
        var options = new ExceptionLensOptions();
        configure?.Invoke(options);

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

        // ── ASP.NET Core middleware ───────────────────────────────────────────
        services.AddTransient<ExceptionLensMiddleware>();

        // ── OpenTelemetry processor ──────────────────────────────────────────
        services.TryAddSingleton<ExceptionLensActivityProcessor>();

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
    /// Adds ExceptionLens exception-enrichment middleware to the pipeline.
    ///
    /// Place early in the pipeline (before MapControllers / MapEndpoints)
    /// so it catches all exceptions:
    ///
    /// <code>
    ///   app.UseExceptionLens();
    ///   app.MapControllers();
    /// </code>
    /// </summary>
    public static IApplicationBuilder UseExceptionLens(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionLensMiddleware>();
}
