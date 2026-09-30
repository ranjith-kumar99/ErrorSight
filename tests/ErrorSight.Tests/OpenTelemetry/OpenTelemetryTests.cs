using System.Diagnostics;
using ErrorSight.Core;
using ErrorSight.Extensions;
using ErrorSight.Integrations.OpenTelemetry;
using ErrorSight.Options;
using ErrorSight.Tests.Weaving;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using SampleApp;
using Conventions = ErrorSight.Integrations.OpenTelemetry.ErrorSightSemanticConventions;

namespace ErrorSight.Tests.OpenTelemetry;

[Collection(RuntimeCollection.Name)]
public sealed class OpenTelemetryTests
{
    // ── End to end: ASP.NET Core + OpenTelemetry SDK, application code untouched ─

    [Fact]
    public async Task UnhandledRequestException_EnrichesTheServerSpan()
    {
        var spans = await RunFailingRequest(configureApp: null);

        var span = spans.Single(a => a.Kind == ActivityKind.Server);
        span.GetTagItem(Conventions.CauseType).Should().Be("System.NullReferenceException");
        span.GetTagItem(Conventions.CauseExpression).Should().Be("order.Customer.Address");
        span.GetTagItem(Conventions.CauseMethod).Should().Be("OrderService.GetCity");
        span.GetTagItem(Conventions.CauseLocation).Should().BeOfType<string>().Which.Should().StartWith("OrderService.cs:");

        var diagnosed = span.Events.Should().ContainSingle(e => e.Name == Conventions.DiagnosedEvent).Subject;
        var tags = diagnosed.Tags.ToDictionary(t => t.Key, t => t.Value);
        tags[Conventions.ExceptionType].Should().Be("System.NullReferenceException");
        tags[Conventions.NullExpression].Should().Be("order.Customer.Address");
        tags[Conventions.NullChain].Should().Be("order → Customer → Address");
        tags[Conventions.FailingExpression].Should().Be("order.Customer.Address.City.Name");
        tags[Conventions.SourceFile].Should().Be("OrderService.cs");
        tags[Conventions.SourceLine].Should().BeOfType<int>().Which.Should().BePositive();
        tags[Conventions.Method].Should().Be("OrderService.GetCity");
        tags[Conventions.DataCapture].Should().Be("none");
    }

    [Fact]
    public async Task Default_SendsNoApplicationData()
    {
        var spans = await RunFailingRequest(configureApp: null);

        var span = spans.Single(a => a.Kind == ActivityKind.Server);
        var diagnosed = span.Events.Single(e => e.Name == Conventions.DiagnosedEvent);
        var keys = diagnosed.Tags.Select(t => t.Key).ToList();
        keys.Should().NotContain(new[] { Conventions.Values, Conventions.ExceptionMessage, Conventions.ObjectType, Conventions.CollectionCount });
        // (url.path from the ASP.NET Core instrumentation contains the order id; ErrorSight's own attributes must not)
        var ours = diagnosed.Tags.Concat(span.TagObjects.Where(t => t.Key.StartsWith("errorsight.", StringComparison.Ordinal)));
        var text = string.Join("|", ours.Select(t => t.Value?.ToString()));
        text.Should().NotContain("Jane Doe").And.NotContain("1837").And.NotContain("jane@example.com");
    }

    [Fact]
    public async Task Metadata_AddsTypesButNoValues()
    {
        var spans = await RunFailingRequest(configureApp: null, configure: o => o.DataCapture = DataCapture.Metadata);

        var tags = spans.Single(a => a.Kind == ActivityKind.Server).Events
            .Single(e => e.Name == Conventions.DiagnosedEvent).Tags.ToDictionary(t => t.Key, t => t.Value);
        tags[Conventions.ObjectType].Should().Be("Address");
        tags[Conventions.ObjectNull].Should().Be(true);
        tags[Conventions.Member].Should().Be("Address");
        tags.Should().NotContainKey(Conventions.Values).And.NotContainKey(Conventions.ExceptionMessage);
    }

    [Fact]
    public async Task Values_OptIn_SendsMaskedValuesOfTheFailingChain()
    {
        var spans = await RunFailingRequest(configureApp: null, configure: o => o.DataCapture = DataCapture.Values);

        var tags = spans.Single(a => a.Kind == ActivityKind.Server).Events
            .Single(e => e.Name == Conventions.DiagnosedEvent).Tags.ToDictionary(t => t.Key, t => t.Value);
        var values = tags[Conventions.Values].Should().BeOfType<string>().Subject;
        values.Should().Contain("\"order.Customer.Address\":null").And.Contain("Email = ***");
        values.Should().NotContain("jane@example.com");
        tags.Should().ContainKey(Conventions.ExceptionMessage);
    }

    [Fact]
    public async Task StandardExceptionAttributes_AreLeftIntact()
    {
        var spans = await RunFailingRequest(configureApp: null);

        var span = spans.Single(a => a.Kind == ActivityKind.Server);
        var exceptionEvent = span.Events.Should().ContainSingle(e => e.Name == "exception").Subject;
        exceptionEvent.Tags.Should().Contain(t => t.Key == "exception.type" && (string)t.Value! == "System.NullReferenceException");
        exceptionEvent.Tags.Should().Contain(t => t.Key == "exception.stacktrace");
        exceptionEvent.Tags.Should().NotContain(t => t.Key.StartsWith("errorsight."));
    }

    [Fact]
    public async Task ExceptionHandlerMiddleware_HandledFailure_IsStillDiagnosed()
    {
        var spans = await RunFailingRequest(app => app.UseExceptionHandler(errorApp => errorApp.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        })));

        var span = spans.Single(a => a.Kind == ActivityKind.Server);
        span.GetTagItem(Conventions.CauseExpression).Should().Be("order.Customer.Address");
        span.Events.Should().ContainSingle(e => e.Name == Conventions.DiagnosedEvent);
    }

    [Fact]
    public async Task SendToOpenTelemetry_False_LeavesSpansUntouched()
    {
        var spans = await RunFailingRequest(configureApp: null, configure: o => o.SendToOpenTelemetry = false);

        var span = spans.Single(a => a.Kind == ActivityKind.Server);
        span.GetTagItem(Conventions.CauseType).Should().BeNull();
        span.Events.Should().NotContain(e => e.Name == Conventions.DiagnosedEvent);
    }

    // ── Activity.AddException (any code, any ActivitySource) ─────────────────

    [Fact]
    public void AddException_OnAnyActivity_IsDiagnosed()
    {
        var options = Diagnose.Options();
        using var source = new ActivitySource("ErrorSight.Tests.Recorder");
        using var sampler = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(sampler);
        using var telemetry = new ErrorSightTelemetry(ExceptionEnricher.CreateDefault(options), options);
        telemetry.Start();

        using var activity = source.StartActivity("process-order")!;
        try
        {
            new OrderService().GetCity(Build.OrderWithoutCity());
        }
        catch (Exception ex)
        {
            activity.AddException(ex);
        }

        activity.GetTagItem(Conventions.CauseExpression).Should().Be("order.Customer.Address.City");
        activity.Events.Should().Contain(e => e.Name == "exception");
        activity.Events.Should().ContainSingle(e => e.Name == Conventions.DiagnosedEvent);
    }

    [Fact]
    public void Apply_WritesAttributesAndEvent()
    {
        var d = Diagnose.Run(() => new OrderService().GetCity(Build.OrderWithoutAddress()));
        using var activity = new Activity("unit").Start();

        ActivityDiagnostics.Apply(activity, d);

        activity.GetTagItem(Conventions.CauseExpression).Should().Be("order.Customer.Address");
        activity.Events.Single().Tags.Should().Contain(t => t.Key == Conventions.NullChain && (string)t.Value! == "order → Customer → Address");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<Activity>> RunFailingRequest(
        Action<WebApplication>? configureApp,
        Action<ErrorSight.Options.ErrorSightOptions>? configure = null)
    {
        var spans = new List<Activity>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // Standard OpenTelemetry setup …
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation(o => o.RecordException = true)
            .AddInMemoryExporter(spans));

        // … plus the single ErrorSight line.
        builder.Services.AddErrorSight(o =>
        {
            o.Capture.MaxCapturesPerSecond = 0;
            o.IncludeSourceContext = false;
            configure?.Invoke(o);
        });

        await using var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapGet("/orders/{id:int}", (int id) => new OrderService().GetCity(Build.OrderWithoutAddress()));

        await app.StartAsync();
        try
        {
            var response = await app.GetTestClient().GetAsync("/orders/1837");
            ((int)response.StatusCode).Should().Be(500);
        }
        catch (NullReferenceException)
        {
            // TestServer rethrows unhandled application exceptions to the caller.
        }

        // The server span is exported when hosting stops the request activity, which can complete
        // just after the client observed the failure.
        SpinWait.SpinUntil(() => { lock (spans) return spans.Any(a => a.Kind == ActivityKind.Server); }, TimeSpan.FromSeconds(10));
        await app.StopAsync();

        return spans;
    }
}
