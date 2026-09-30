using System.Diagnostics;
using System.Runtime.CompilerServices;
using ErrorSight;
using ErrorSight.Core;
using ErrorSight.Integrations.OpenTelemetry;
using ErrorSight.Options;
using ErrorSight.Runtime;

// Compares identical code with and without ErrorSight instrumentation ([ErrorSightIgnore]).
var options = new ErrorSightOptions { IncludeSourceContext = false };
options.Capture.MaxCapturesPerSecond = 0; // measure the worst case: every exception captured
ErrorSightRuntime.Configure(options);

var order = new Order { Id = 1, Customer = new Customer { Name = "Jane", Address = new Address { City = new City { Name = "Oslo" } } } };
var broken = new Order { Id = 2, Customer = new Customer { Name = "John" } };
var woven = new Instrumented();
var plain = new NotInstrumented();

Console.WriteLine($"ErrorSight overhead — .NET {Environment.Version}, {(Debugger.IsAttached ? "debugger" : "no debugger")}");
Console.WriteLine();

// ── 1. Happy path ────────────────────────────────────────────────────────────
const int calls = 20_000_000;
var plainNs = Measure(calls, () => plain.CityLength(order));
var wovenNs = Measure(calls, () => woven.CityLength(order));
Console.WriteLine($"Happy path, per call      plain {plainNs,8:F2} ns   instrumented {wovenNs,8:F2} ns   (+{wovenNs - plainNs:F2} ns)");

// ── 2. Exception through 3 frames, caught by the caller ───────────────────────
const int throws = 20_000;
var plainThrow = Measure(throws, () => Catch(() => plain.Outer(broken)));
var wovenThrow = Measure(throws, () => Catch(() => woven.Outer(broken)));
var allocPlain = Allocated(throws, () => Catch(() => plain.Outer(broken)));
var allocWoven = Allocated(throws, () => Catch(() => woven.Outer(broken)));
Console.WriteLine($"Throw+catch (3 frames)    plain {plainThrow / 1000,8:F2} µs   instrumented {wovenThrow / 1000,8:F2} µs   (+{(wovenThrow - plainThrow) / 1000:F2} µs, +{(allocWoven - allocPlain) / 1024.0:F1} KB)");

options.CaptureRuntimeValues = false;
var filtersOnly = Measure(throws, () => Catch(() => woven.Outer(broken)));
Console.WriteLine($"  filters only (capture disabled):          {filtersOnly / 1000,8:F2} µs");
options.CaptureRuntimeValues = true;

options.Capture.MaxCapturesPerSecond = 200;
var limitedThrow = Measure(throws, () => Catch(() => woven.Outer(broken)));
Console.WriteLine($"  with default rate limit (200 captures/s): {limitedThrow / 1000,8:F2} µs");
options.Capture.MaxCapturesPerSecond = 0;

// ── 3. Diagnosis (runs only for exceptions that reach a span / are enriched) ──
var enricher = ExceptionEnricher.CreateDefault(options);
var diagnose = Measure(5_000, () =>
{
    var ex = Catch(() => woven.Outer(broken))!;
    var d = enricher.Enrich(ex);
    using var activity = new Activity("bench").Start();
    ActivityDiagnostics.Apply(activity, d);
});
Console.WriteLine($"Throw + diagnose + span   {diagnose / 1000,8:F2} µs per exception");

var probe = enricher.Enrich(Catch(() => woven.Outer(broken))!);
Console.WriteLine();
Console.WriteLine($"Diagnosis: {probe.NullExpression} is null ({probe.SourceFileShort}:{probe.Line}, {probe.Frames.Count} frames captured)");

static Exception Catch(Action action)
{
    try { action(); return null; }
    catch (Exception ex) { return ex; }
}

static double Measure(int iterations, Action action)
{
    for (var i = 0; i < Math.Min(iterations, 100_000) / 10; i++) action(); // warm up (tiering)
    var best = double.MaxValue;
    for (var round = 0; round < 3; round++)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) action();
        sw.Stop();
        best = Math.Min(best, sw.Elapsed.TotalNanoseconds / iterations);
    }
    return best;
}

static double Allocated(int iterations, Action action)
{
    action();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < iterations; i++) action();
    return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
}

public sealed class Order { public int Id { get; set; } public Customer Customer { get; set; } }
public sealed class Customer { public string Name { get; set; } public Address Address { get; set; } }
public sealed class Address { public City City { get; set; } }
public sealed class City { public string Name { get; set; } }

public sealed class Instrumented
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CityLength(Order order)
    {
        var city = order.Customer.Address.City.Name;
        return city.Length + order.Id;
    }

    public int Outer(Order order)
    {
        var attempt = order.Id > 0 ? 1 : 2;
        var result = Middle(order);
        return result + attempt;
    }

    public int Middle(Order order)
    {
        var factor = order.Customer.Name.Length > 3 ? 2 : 3;
        var length = CityLength(order);
        return length * factor;
    }
}

[ErrorSightIgnore]
public sealed class NotInstrumented
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CityLength(Order order)
    {
        var city = order.Customer.Address.City.Name;
        return city.Length + order.Id;
    }

    public int Outer(Order order)
    {
        var attempt = order.Id > 0 ? 1 : 2;
        var result = Middle(order);
        return result + attempt;
    }

    public int Middle(Order order)
    {
        var factor = order.Customer.Name.Length > 3 ? 2 : 3;
        var length = CityLength(order);
        return length * factor;
    }
}
