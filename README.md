# ExceptionLens

**Root-cause diagnostics for .NET exceptions, delivered through OpenTelemetry.**

OpenTelemetry gives you the pipeline: your spans already carry `exception.type`, `exception.message` and
`exception.stacktrace`. ExceptionLens adds the diagnosis: *which* expression was null, the runtime values at
the throw site (masked), and the exact source location. Nothing to write in your code.

```
dotnet add package ExceptionLens
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddExceptionLens();   // ← the only line you add

var app = builder.Build();
app.Run();
```

No `try`/`catch`, no `Analyze(ex)`, no attributes on your code.

## What you get

Given ordinary application code:

```csharp
public string ProcessOrder(Order order, string paymentToken)
{
    var city = order.Customer.Address.City.Name;   // Address is null
    return $"Order {order.Id} ships to {city.ToUpperInvariant()}";
}
```

This is the server span as the OpenTelemetry console exporter prints it (real output, Release build):

```
Activity.DisplayName:        POST /orders
Activity.Tags:
    ...
    exceptionlens.cause.type: System.NullReferenceException
    exceptionlens.cause.expression: order.Customer.Address
    exceptionlens.cause.location: Program.cs:28
    exceptionlens.cause.method: OrderService.ProcessOrder
Activity.Events:
    exceptionlens.exception.diagnosed
        exception.type: System.NullReferenceException
        exception.message: Object reference not set to an instance of an object.
        exceptionlens.null.expression: order.Customer.Address
        exceptionlens.null.chain: order → Customer → Address
        exceptionlens.failing_expression: order.Customer.Address.City.Name
        exceptionlens.source.file: Program.cs
        exceptionlens.source.line: 28
        exceptionlens.method: OrderService.ProcessOrder
        exceptionlens.cause: order.Customer.Address is null.
        exceptionlens.suggestion: Check whether order.Customer.Address is initialised before accessing its members.
        exceptionlens.values: {"this":"OrderService { }","order":"Order { Id = 1837, Customer = {…} }",
                               "paymentToken":"***","city":null,
                               "order.Customer":"Customer { Name = \"Jane Doe\", Email = ***, Address = null }",
                               "order.Customer.Address":null}
```

In Jaeger, Grafana Tempo, Azure Monitor, Datadog, Honeycomb, or anything else behind your exporter:

```
HTTP POST /orders
  └── span
        ├── exception.type / exception.message / exception.stacktrace   (unchanged)
        ├── exceptionlens.cause.expression = order.Customer.Address       (searchable attribute)
        └── event exceptionlens.exception.diagnosed
              ├── null.expression   order.Customer.Address
              ├── source            Program.cs:28
              └── values            order, paymentToken = ***, ...
```

The standard `exception.*` attributes and the `exception` event are never modified.

### Span attributes (small, searchable)

| Attribute | Example |
|---|---|
| `exceptionlens.cause.type` | `System.NullReferenceException` |
| `exceptionlens.cause.expression` | `order.Customer.Address` |
| `exceptionlens.cause.location` | `OrderService.cs:42` |
| `exceptionlens.cause.method` | `OrderService.ProcessOrder` |

### Event `exceptionlens.exception.diagnosed`

`exception.type`, `exception.message`, `exceptionlens.null.expression`, `exceptionlens.null.chain`,
`exceptionlens.null.candidates` (when the culprit is ambiguous), `exceptionlens.failing_expression`,
`exceptionlens.source.file`, `exceptionlens.source.line`, `exceptionlens.method`, `exceptionlens.cause`,
`exceptionlens.suggestion`, `exceptionlens.values` (masked JSON), plus `exceptionlens.parameter`,
`exceptionlens.missing_key`, `exceptionlens.collection` and `exceptionlens.index` when they apply.

It is not only for `NullReferenceException`. The same captured values explain `KeyNotFoundException` (the key
variable and the dictionary), `IndexOutOfRangeException`/`ArgumentOutOfRangeException` (collection, index and
length), `ArgumentNullException`, LINQ `InvalidOperationException`s, and any exception type through
[custom analyzers](#custom-analyzers).

## How it works

The CLR does not record *which* reference was null, and by the time a `catch` block runs, the stack has unwound
and local variables are gone. So ExceptionLens captures them *while the exception is being thrown*, in two
parts:

```
                  your code                              ExceptionLens
┌──────────────────────────────────────┐
│ build:  csc → obj/App.dll ──────────────▶ weaver (MSBuild, ships in the package)
│                                      │     adds an exception *filter* to your methods
│ run:    order.Customer.Address.City  │
│              💥 NullReferenceException │
│         CLR searches for a handler ─────▶ filter: snapshot params/locals/this (masked),
│         (first pass, stack intact)   │           return false → exception continues unchanged
│         ...request fails...          │
│         ASP.NET Core / Activity ────────▶ root-cause engine: IL map + snapshot
│                                      │     → "order.Customer.Address is null"
│                                      │     → attributes + event on the current span
└──────────────────────────────────────┘
                                             OpenTelemetry exporter ships it as usual
```

1. **Build-time instrumentation.** The package's MSBuild targets run a weaver on your compiled assembly (after
   `CoreCompile`, before the copy to `bin`). Each method gets a
   `try { original body } filter { record values; return false; }`. For every instruction that dereferences an
   object, the weaver also records a source-like expression (`order.Customer.Address`) in a small embedded
   resource. PDBs are updated, so debugging and stack traces are unchanged.
2. **Capture at throw time.** Filters run during the CLR's first exception-handling pass, *before* the stack
   unwinds and before `finally` blocks run. The filter records the method's parameters, locals and `this`, then
   returns `false`, so the exception keeps propagating: same object, same type, same stack trace. Snapshots
   never execute your code (no property getters or `ToString()` calls; fields are read directly), so there are
   no side effects such as EF Core lazy loading. Masking is applied during capture.
3. **Automatic detection.** ExceptionLens hooks the same ASP.NET Core diagnostic events that OpenTelemetry's
   ASP.NET Core instrumentation uses, plus `Activity.AddException` from any library or `ActivitySource`. It
   enriches the active span before it ends. It does not depend on the OpenTelemetry SDK, because spans are
   plain `System.Diagnostics.Activity` objects.
4. **Root cause.** The IL map says which receivers the failing statement dereferences. The snapshot says which
   one was null. In optimized builds the JIT reports the IL offset imprecisely, so the engine considers the
   whole statement region and corrects the reported line from the resolved access.

Async methods, iterators, async iterators, lambdas, local functions, constructors, struct methods, and generic
types and methods are all supported, in Debug and Release builds.

## Masking

Masking is applied **at capture time**, so masked values are never stored, logged, or exported. By default,
values whose name marks them as sensitive are masked, and null-ness is always visible (it *is* the diagnosis):

```
paymentToken = ***
order.Customer = Customer { Name = "Jane Doe", Email = ***, Address = null }
```

Default sensitive names include password, secret, token, apikey, credential(s), authorization, cookie,
connectionstring, ssn, creditcard, cardnumber, cvv, iban, email, phone and dateofbirth. Matching is
word-aware: `apiKey`, `X-Api-Key` and `userPassword` match, while `shipping` does not match `pin`.

The following attributes are honoured on properties, fields, parameters and whole types:

- ExceptionLens's own `[Sensitive]`.
- Any attribute named `SensitiveAttribute`, `SensitiveDataAttribute`, `PersonalDataAttribute` or
  `ProtectedPersonalDataAttribute`. This includes ASP.NET Core Identity's attributes.
- Any Microsoft.Extensions.Compliance `DataClassificationAttribute`.

```csharp
public class Customer
{
    public string Name { get; set; }
    [Sensitive] public string Notes { get; set; }
}
```

```csharp
builder.Services.AddExceptionLens(o =>
{
    o.Masking.Mode = MaskingMode.All;              // SensitiveOnly (default) | All | None
    o.Masking.Style = MaskStyle.Hash;              // Redact "***" (default) | Hash "sha256:1a2b3c4d" | Partial "***1234"
    o.Masking.SensitiveNames.Add("iban");          // or .Clear() to replace the defaults
    o.Masking.ShouldMask = ctx => ctx.Path.StartsWith("patient.");
    o.Masking.Redactor = value => MyRedactor(value);
    o.Masking.MaskExceptionMessages = true;        // e.g. the key inside a KeyNotFoundException message
});
```

`MaskingMode.All` is a production-safe setting. Every value becomes its type or `***`, but null-ness is kept,
so ExceptionLens still reports `order.Customer.Address is null`.

## Options

```csharp
builder.Services.AddExceptionLens(o =>
{
    o.CaptureRuntimeValues = true;   // build-time capture of params/locals (default: true)
    o.CaptureSourceLocation = true;  // file, line, method (default: true)
    o.SendToOpenTelemetry = true;    // enrich the current Activity/span (default: true)

    o.Capture.MaxFramesPerException = 5;   // instrumented frames per exception, innermost first
    o.Capture.MaxDepth = 3;                // object-graph depth (customer.Address.City = 2)
    o.Capture.MaxMembersPerObject = 20;
    o.Capture.MaxCollectionItems = 5;      // the full Count is always reported
    o.Capture.MaxStringLength = 256;
    o.Capture.MaxCapturesPerSecond = 200;  // process-wide guard against exception storms (0 = unlimited)
    o.Capture.IncludePrivateFields = false; // default: public auto-properties and public fields only
    o.Capture.ShouldCapture = ex => ex is not OperationCanceledException;

    o.ShouldEnrich = ex => ex is not OperationCanceledException;
});
```

### Opting out

| Scope | How |
|---|---|
| Whole project | `<ExceptionLensWeave>false</ExceptionLensWeave>` in the `.csproj` |
| Test projects | Not woven by default (`IsTestProject`); set `<ExceptionLensWeave>true</ExceptionLensWeave>` to opt in |
| Assembly | `[assembly: ExceptionLens.ExceptionLensIgnore]` |
| Type or method (hot paths) | `[ExceptionLensIgnore]` |
| Tiny methods | Skipped automatically when the IL body is ≤ 16 bytes (`<ExceptionLensMinILSize>`), so the JIT keeps inlining them |
| At runtime | `o.CaptureRuntimeValues = false` |

## Performance

Measured with `benchmarks/ExceptionLens.Benchmarks` (.NET 8, Release). It compares identical code with and
without instrumentation:

| | Not instrumented | Instrumented |
|---|---|---|
| Call that doesn't throw | 2.8 ns | 3.2 ns (+0.4 ns) |
| Throw + catch through 3 frames, every frame captured | 5.7 µs | 13.0 µs (+7.3 µs, +4.5 KB) |
| … same, beyond the default 200 captures/s budget | 5.7 µs | ≈ 8.3 µs |
| Diagnose + write span attributes/event (only for exceptions that reach a span) | | ≈ 60 µs |

- **Code that doesn't throw pays almost nothing.** The filter only runs when an exception passes through the
  method. The one real cost is that on .NET 8 the JIT does not inline methods that contain exception handlers,
  which is why methods of 16 IL bytes or fewer are left alone.
- **Exceptions cost more, and the cost is bounded.** Each captured frame costs about 1.7 µs, frames per
  exception are capped (5), and a process-wide rate limit protects against exception storms.
- **Formatting is lazy.** Exceptions your code catches and handles pay only for the capture, never for
  formatting or diagnosis.

## Limits (by design, today)

- **Release builds keep single-use locals on the evaluation stack**, so those locals have no value to capture.
  Everything else still is: parameters, `this`, fields, and locals that live longer. An eliminated `foreach`
  variable is reported as an element of its collection, e.g. `customers[…].Address`.
- When a receiver cannot be evaluated (a method-call result such as `GetCustomer().Address`, or beyond the
  depth limit), ExceptionLens reports the possible culprits in `exceptionlens.null.candidates` instead of
  guessing.
- Not instrumented yet: strong-name-signed assemblies (skipped with a build warning), third-party DLLs,
  VB/F#, and NativeAOT/trimmed apps.

## Beyond spans

The diagnosis is also available directly:

```csharp
// Hosted apps: inject it. Without DI: ExceptionEnricher.CreateDefault()
var diagnostics = enricher.Enrich(exception);
Console.WriteLine(new TextExceptionFormatter().Format(diagnostics));  // human-readable banner
var json = new JsonExceptionFormatter().Format(diagnostics);           // includes per-frame values
```

Captured frames can be read with `ExceptionLensRuntime.TryGetCapturedFrames(exception, out var frames)`.

The optional `app.UseExceptionLens()` middleware logs the text banner through `ILogger` for failed requests. It
can also return a problem-details body (`IncludeDiagnosticsInResponse`, meant for development only). Span
enrichment does not need it.

Outside a generic host (a plain `ServiceCollection`), start the hooks yourself:
`provider.GetRequiredService<ExceptionLensTelemetry>().Start()`.

## Custom analyzers

```csharp
public class MyDomainExceptionAnalyzer : IExceptionAnalyzer
{
    public bool CanAnalyze(Exception ex) => ex is MyDomainException;

    public void Enrich(ExceptionDiagnostics d, Exception ex)
    {
        var domain = (MyDomainException)ex;
        d.PossibleCause = $"Entity {domain.EntityId} violated domain rule: {domain.Rule}";
    }
}

builder.Services.AddExceptionAnalyzer<MyDomainExceptionAnalyzer>();
```

## Repository layout

```
src/ExceptionLens           runtime: capture store, masking, root-cause engine, OpenTelemetry hooks
src/ExceptionLens/build     MSBuild targets shipped in the package (build/ and buildTransitive/)
src/ExceptionLens.Weaver    build-time IL weaver (Mono.Cecil), shipped in the package's tools/weaver
src/Shared                  metadata format shared by weaver and runtime
tests/ExceptionLens.Samples plain application code, woven by the real targets
tests/ExceptionLens.Tests   capture, semantics, masking and end-to-end OpenTelemetry tests
benchmarks/                 overhead measurements
```

## License

MIT
