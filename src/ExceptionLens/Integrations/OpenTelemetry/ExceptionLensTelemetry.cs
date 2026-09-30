using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExceptionLens.Core;
using ExceptionLens.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;

namespace ExceptionLens.Integrations.OpenTelemetry;

/// <summary>
/// Detects exceptions and enriches the active span automatically — no try/catch in application code.
///
/// Hooks (none of them require the OpenTelemetry SDK; spans are plain <see cref="Activity"/> objects,
/// which every OpenTelemetry exporter already ships):
///   • ASP.NET Core's exception diagnostic events — the same events OpenTelemetry's ASP.NET Core
///     instrumentation uses — for unhandled and exception-handler-handled request failures;
///   • <see cref="ActivityListener.ExceptionRecorder"/>, invoked whenever any code (including
///     OpenTelemetry instrumentations) calls <c>Activity.AddException</c>.
///
/// Started automatically by <c>services.AddExceptionLens()</c> in hosted apps. Outside a host,
/// resolve this service and call <see cref="Start"/>.
/// </summary>
public sealed class ExceptionLensTelemetry : IDisposable
{
    private static readonly HashSet<string> AspNetCoreExceptionEvents = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Hosting.UnhandledException",
        "Microsoft.AspNetCore.Diagnostics.UnhandledException",
        "Microsoft.AspNetCore.Diagnostics.HandledException",
    };

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PayloadProperties = new();

    private readonly ExceptionEnricher _enricher;
    private readonly ExceptionLensOptions _options;
    private readonly ConditionalWeakTable<Activity, Exception> _enriched = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _gate = new();
    private ActivityListener? _activityListener;
    private bool _started;

    public ExceptionLensTelemetry(ExceptionEnricher enricher, ExceptionLensOptions options)
    {
        _enricher = enricher;
        _options = options;
    }

    /// <summary>Starts listening. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || !_options.SendToOpenTelemetry) return;
            _started = true;

            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(this)));

            _activityListener = new ActivityListener
            {
                // Listening does not create or sample activities (no Sample callback); it only
                // makes ExceptionRecorder fire for activities other listeners (OpenTelemetry) create.
                ShouldListenTo = static _ => true,
                ExceptionRecorder = (Activity activity, Exception exception, ref TagList _) => Record(activity, exception),
            };
            ActivitySource.AddActivityListener(_activityListener);
        }
    }

    /// <summary>
    /// Diagnoses <paramref name="exception"/> and adds the result to <paramref name="activity"/>
    /// (or <see cref="Activity.Current"/>). Called automatically by the hooks; public for custom pipelines.
    /// </summary>
    public void Record(Activity? activity, Exception? exception)
    {
        activity ??= Activity.Current;
        if (activity is null || exception is null) return;

        try
        {
            if (!_options.SendToOpenTelemetry || !_options.ShouldEnrich(exception)) return;

            lock (_enriched)
            {
                // The same failure is often reported by several hooks; enrich each span once per exception.
                if (_enriched.TryGetValue(activity, out var previous) && ReferenceEquals(previous, exception)) return;
                _enriched.AddOrUpdate(activity, exception);
            }

            var diagnostics = _enricher.Enrich(exception, activity.TraceId.ToString());
            ActivityDiagnostics.Apply(activity, diagnostics);
        }
        catch
        {
            // Telemetry must never break the request.
        }
    }

    private void OnAspNetCoreEvent(string name, object? payload)
    {
        if (!AspNetCoreExceptionEvents.Contains(name) || payload is null) return;

        var exception = Fetch(payload, "exception") as Exception;
        var httpContext = Fetch(payload, "httpContext") as HttpContext;
        var activity = httpContext?.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current;
        Record(activity, exception);
    }

    private static object? Fetch(object payload, string property)
    {
        var info = PayloadProperties.GetOrAdd((payload.GetType(), property),
            static key => key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance));
        try { return info?.GetValue(payload); }
        catch { return null; }
    }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            _activityListener?.Dispose();
            _activityListener = null;
            _started = false;
        }
    }

    private sealed class ListenerObserver(ExceptionLensTelemetry owner) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "Microsoft.AspNetCore") return;
            var subscription = listener.Subscribe(new EventObserver(owner), AspNetCoreExceptionEvents.Contains);
            lock (owner._gate) owner._subscriptions.Add(subscription);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class EventObserver(ExceptionLensTelemetry owner) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => owner.OnAspNetCoreEvent(value.Key, value.Value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Starts <see cref="ExceptionLensTelemetry"/> with the host.</summary>
internal sealed class ExceptionLensHostedService(ExceptionLensTelemetry telemetry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        telemetry.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
