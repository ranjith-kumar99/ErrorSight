using ErrorSight.Analyzers;
using ErrorSight.Core;
using ErrorSight.Extensions;
using ErrorSight.Formatting;
using ErrorSight.Integrations.OpenTelemetry;
using ErrorSight.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ErrorSight.Tests.Integration;

[Collection(ErrorSight.Tests.Weaving.RuntimeCollection.Name)]
public sealed class ServiceCollectionTests
{
    [Fact]
    public void AddErrorSight_RegistersEnricher()
    {
        var sp = BuildSp();
        sp.GetRequiredService<ExceptionEnricher>().Should().NotBeNull();
    }

    [Fact]
    public void AddErrorSight_RegistersAllBuiltinAnalyzers()
    {
        var sp = BuildSp();
        var analyzers = sp.GetRequiredService<IEnumerable<IExceptionAnalyzer>>().ToList();
        analyzers.Should().Contain(a => a is NullReferenceAnalyzer);
        analyzers.Should().Contain(a => a is ArgumentNullAnalyzer);
        analyzers.Should().Contain(a => a is KeyNotFoundAnalyzer);
        analyzers.Should().Contain(a => a is IndexOutOfRangeAnalyzer);
        analyzers.Should().Contain(a => a is InvalidOperationAnalyzer);
        analyzers.Should().Contain(a => a is AggregateExceptionAnalyzer);
    }

    [Fact]
    public void AddErrorSight_RegistersTextFormatter()
    {
        var sp = BuildSp();
        var formatter = sp.GetRequiredService<IExceptionFormatter>();
        formatter.Should().BeOfType<TextExceptionFormatter>();
    }

    [Fact]
    public void AddErrorSight_RegistersOptions()
    {
        var sp = BuildSp();
        var options = sp.GetRequiredService<ErrorSightOptions>();
        options.Should().NotBeNull();
    }

    [Fact]
    public void AddErrorSight_ConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorSight(opt => opt.StructuredLogging = false);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ErrorSightOptions>()
            .StructuredLogging.Should().BeFalse();
    }

    [Fact]
    public void AddErrorSight_RegistersTelemetryAndHostedService()
    {
        var sp = BuildSp();
        sp.GetRequiredService<ErrorSightTelemetry>().Should().NotBeNull();
        sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().ContainSingle();
    }

    [Fact]
    public void AddErrorSight_ConfiguresRuntimeCaptureOptions()
    {
        var services = new ServiceCollection();
        services.AddErrorSight(opt => opt.Masking.Mode = ErrorSight.Masking.MaskingMode.All);
        var sp = services.BuildServiceProvider();

        ErrorSight.Runtime.ErrorSightRuntime.Options.Should().BeSameAs(sp.GetRequiredService<ErrorSightOptions>());
        ErrorSight.Runtime.ErrorSightRuntime.Configure(new ErrorSightOptions());
    }

    [Fact]
    public void AddErrorSight_PassesDependencyInjectionValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorSight();
        var build = () => services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        build.Should().NotThrow();
    }

    [Fact]
    public void AddExceptionAnalyzer_AddsCustomAnalyzer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorSight();
        services.AddExceptionAnalyzer<CustomTestAnalyzer>();
        var sp = services.BuildServiceProvider();

        var analyzers = sp.GetRequiredService<IEnumerable<IExceptionAnalyzer>>().ToList();
        analyzers.Should().Contain(a => a is CustomTestAnalyzer);
    }

    private static IServiceProvider BuildSp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorSight();
        return services.BuildServiceProvider();
    }

    private sealed class CustomTestAnalyzer : IExceptionAnalyzer
    {
        public bool CanAnalyze(Exception ex) => false;
        public void Enrich(ExceptionDiagnostics d, Exception ex) { }
    }
}
