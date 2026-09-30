using ExceptionLens.Analyzers;
using ExceptionLens.Core;
using ExceptionLens.Extensions;
using ExceptionLens.Formatting;
using ExceptionLens.Integrations.OpenTelemetry;
using ExceptionLens.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExceptionLens.Tests.Integration;

[Collection(ExceptionLens.Tests.Weaving.RuntimeCollection.Name)]
public sealed class ServiceCollectionTests
{
    [Fact]
    public void AddExceptionLens_RegistersEnricher()
    {
        var sp = BuildSp();
        sp.GetRequiredService<ExceptionEnricher>().Should().NotBeNull();
    }

    [Fact]
    public void AddExceptionLens_RegistersAllBuiltinAnalyzers()
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
    public void AddExceptionLens_RegistersTextFormatter()
    {
        var sp = BuildSp();
        var formatter = sp.GetRequiredService<IExceptionFormatter>();
        formatter.Should().BeOfType<TextExceptionFormatter>();
    }

    [Fact]
    public void AddExceptionLens_RegistersOptions()
    {
        var sp = BuildSp();
        var options = sp.GetRequiredService<ExceptionLensOptions>();
        options.Should().NotBeNull();
    }

    [Fact]
    public void AddExceptionLens_ConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExceptionLens(opt => opt.StructuredLogging = false);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ExceptionLensOptions>()
            .StructuredLogging.Should().BeFalse();
    }

    [Fact]
    public void AddExceptionLens_RegistersTelemetryAndHostedService()
    {
        var sp = BuildSp();
        sp.GetRequiredService<ExceptionLensTelemetry>().Should().NotBeNull();
        sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().ContainSingle();
    }

    [Fact]
    public void AddExceptionLens_ConfiguresRuntimeCaptureOptions()
    {
        var services = new ServiceCollection();
        services.AddExceptionLens(opt => opt.Masking.Mode = ExceptionLens.Masking.MaskingMode.All);
        var sp = services.BuildServiceProvider();

        ExceptionLens.Runtime.ExceptionLensRuntime.Options.Should().BeSameAs(sp.GetRequiredService<ExceptionLensOptions>());
        ExceptionLens.Runtime.ExceptionLensRuntime.Configure(new ExceptionLensOptions());
    }

    [Fact]
    public void AddExceptionLens_PassesDependencyInjectionValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExceptionLens();
        var build = () => services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        build.Should().NotThrow();
    }

    [Fact]
    public void AddExceptionAnalyzer_AddsCustomAnalyzer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExceptionLens();
        services.AddExceptionAnalyzer<CustomTestAnalyzer>();
        var sp = services.BuildServiceProvider();

        var analyzers = sp.GetRequiredService<IEnumerable<IExceptionAnalyzer>>().ToList();
        analyzers.Should().Contain(a => a is CustomTestAnalyzer);
    }

    private static IServiceProvider BuildSp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExceptionLens();
        return services.BuildServiceProvider();
    }

    private sealed class CustomTestAnalyzer : IExceptionAnalyzer
    {
        public bool CanAnalyze(Exception ex) => false;
        public void Enrich(ExceptionDiagnostics d, Exception ex) { }
    }
}
