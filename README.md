# ErrorSight

**Root-cause diagnostics for .NET exceptions, delivered through OpenTelemetry.**

OpenTelemetry gives you the pipeline: your spans already carry `exception.type`, `exception.message` and
`exception.stacktrace`. ErrorSight adds the diagnosis: *which* expression was null, where that null came from,
and the exact source location. Nothing to write in your code.

**ErrorSight does not capture application data by default. It captures diagnostic structure, not customer
content.** Runtime values are an explicit opt-in.

```
dotnet add package ErrorSight
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddErrorSight();   // ← the only line you add

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

With the default settings, this is what the server span receives:

```
Activity.Tags:
    errorsight.cause.type: System.NullReferenceException
    errorsight.cause.expression: order.Customer.Address
    errorsight.cause.location: OrderService.cs:34
    errorsight.cause.method: OrderService.ProcessOrder
Activity.Events:
    errorsight.exception.diagnosed
        exception.type: System.NullReferenceException
        errorsight.data_capture: none
        errorsight.null.expression: order.Customer.Address
        errorsight.null.chain: order → Customer → Address
        errorsight.failing_expression: order.Customer.Address.City.Name
        errorsight.source.file: OrderService.cs
        errorsight.source.line: 34
        errorsight.method: OrderService.ProcessOrder
        errorsight.cause: order.Customer.Address is null.
        errorsight.suggestion: Check whether order.Customer.Address is initialised before accessing its members.
```

No values, no strings, no exception message, no request data. The standard `exception.*` attributes and the
`exception` event are never modified.

## Privacy levels

| | Level 1: `DataCapture.None` (default) | Level 2: `DataCapture.Metadata` | Level 3: `DataCapture.Values` |
|---|---|---|---|
| Exception type, method, file/line, call path | ✓ | ✓ | ✓ |
| Null expression, failing expression, where the null came from | ✓ | ✓ | ✓ |
| Type of the null object, collection type, count / empty | | ✓ | ✓ |
| Runtime values (masked), exception message, missing key, index | | | ✓ |

ErrorSight never captures request or response bodies, headers, cookies, environment variables or source code.

```csharp
builder.Services.AddErrorSight();                                             // Level 1
builder.Services.AddErrorSight(o => o.DataCapture = DataCapture.Metadata);   // Level 2
builder.Services.AddErrorSight(o => o.DataCapture = DataCapture.Values);     // Level 3, masked
```

**Level 2** adds structure about the values without the values themselves:

```
errorsight.object.type: Address          errorsight.collection.type: List<Order>
errorsight.object.null: true             errorsight.collection.count: 0
errorsight.member: Address               errorsight.collection.empty: true
```

**Level 3** adds the values, but only those of the expressions involved in the failure (for a null chain:
`order`, `order.Customer`, `order.Customer.Address`), not every object in scope. They are masked by name (see
[Masking](#masking)):

```
exception.message: Object reference not set to an instance of an object.
errorsight.values: {"order":"Order { Id = 1837, Customer = {…} }",
                    "order.Customer":"Customer { Name = \"Jane Doe\", Email = ***, Address = null }",
                    "order.Customer.Address":null}
```

### Where the null came from

When the null value sits in a local variable, ErrorSight reports the assignment that put it there. No values
are needed for this; it comes from the build-time IL map:

```csharp
var address = customer.Address;     // line 128
var city = address.City.Name;       // line 131 💥
```

```
errorsight.null.expression: address
errorsight.origin.expression: customer.Address
errorsight.origin.line: 128
errorsight.source.line: 131
```

The text banner shows it as a flow:

```
Origin:
  customer.Address   assigned to address  OrderService.cs:128
    ↓
  address.City.Name  dereferenced  OrderService.cs:131
    ↓
  NullReferenceException
```

### Span attributes (small, searchable)

| Attribute | Example |
|---|---|
| `errorsight.cause.type` | `System.NullReferenceException` |
| `errorsight.cause.expression` | `order.Customer.Address` |
| `errorsight.cause.location` | `OrderService.cs:42` |
| `errorsight.cause.method` | `OrderService.ProcessOrder` |

It is not only for `NullReferenceException`. ErrorSight also explains `KeyNotFoundException` (the dictionary;
the key at Level 3), `IndexOutOfRangeException`/`ArgumentOutOfRangeException` (the collection; its count at
Level 2, the index at Level 3), `ArgumentNullException`, LINQ `InvalidOperationException`s, and any exception
type through [custom analyzers](#custom-analyzers).

## How it works

The CLR does not record *which* reference was null, and by the time a `catch` block runs, the stack has unwound
and local variables are gone. So ErrorSight captures them *while the exception is being thrown*, in two
parts:

```
                  your code                              ErrorSight
┌──────────────────────────────────────┐
│ build:  csc → obj/App.dll ──────────────▶ weaver (MSBuild, ships in the package)
│                                      │     adds an exception *filter* to your methods
│ run:    order.Customer.Address.City  │
│              💥 NullReferenceException │
│         CLR searches for a handler ─────▶ filter: snapshot params/locals/this at the configured level,
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
   `try { original body } filter { record state; return false; }`. For every instruction that dereferences an
   object, the weaver also records a source-like expression (`order.Customer.Address`), and for every
   assignment to a local its source expression (`address = customer.Address`), in a small embedded resource.
   PDBs are updated, so debugging and stack traces are unchanged.
2. **Capture at throw time.** Filters run during the CLR's first exception-handling pass, *before* the stack
   unwinds and before `finally` blocks run. The filter looks at the method's parameters, locals and `this`, then
   returns `false`, so the exception keeps propagating: same object, same type, same stack trace. At Level 1 it
   keeps only types and which references were null, which is all the diagnosis needs; values are kept only at
   Level 3, masked as they are captured. Snapshots never execute your code (no property getters or
   `ToString()` calls; fields are read directly), so there are no side effects such as EF Core lazy loading.
3. **Automatic detection.** ErrorSight hooks the same ASP.NET Core diagnostic events that OpenTelemetry's
   ASP.NET Core instrumentation uses, plus `Activity.AddException` from any library or `ActivitySource`. It
   enriches the active span before it ends. It does not depend on the OpenTelemetry SDK, because spans are
   plain `System.Diagnostics.Activity` objects.
4. **Root cause.** The IL map says which receivers the failing statement dereferences. The snapshot says which
   one was null, and the recorded assignments say where a null local came from. In optimized builds the JIT reports the IL offset imprecisely, so the engine considers the
   whole statement region and corrects the reported line from the resolved access.

Async methods, iterators, async iterators, lambdas, local functions, constructors, struct methods, and generic
types and methods are all supported, in Debug and Release builds.

## Masking

Masking applies once you opt in to values (Level 3). It is applied **at capture time**, so masked values are
never stored, logged, or exported. By default, values whose name marks them as sensitive are masked, and
null-ness is always visible (it *is* the diagnosis):

```
paymentToken = ***
order.Customer = Customer { Name = "Jane Doe", Email = ***, Address = null }
```

Default sensitive names include password, secret, token, apikey, credential(s), authorization, cookie,
connectionstring, ssn, creditcard, cardnumber, cvv, iban, email, phone and dateofbirth. Matching is
word-aware: `apiKey`, `X-Api-Key` and `userPassword` match, while `shipping` does not match `pin`.

```csharp
builder.Services.AddErrorSight(o =>
{
    o.DataCapture = DataCapture.Values;
    o.Masking.Mode = MaskingMode.SensitiveOnly;    // SensitiveOnly (default) | All | None
    o.Masking.Style = MaskStyle.Redact;            // Redact "***" (default) | Partial "***1234"
    o.Masking.SensitiveNames.Add("iban");          // or .Clear() to replace the defaults
    o.Masking.ShouldMask = ctx => ctx.Path.StartsWith("patient.");
    o.Masking.Redactor = value => MyRedactor(value);
    o.Masking.MaskExceptionMessages = true;        // e.g. the key inside a KeyNotFoundException message
});
```

## Options

```csharp
builder.Services.AddErrorSight(o =>
{
    o.DataCapture = DataCapture.None; // None (default) | Metadata | Values
    o.CaptureSourceLocation = true;   // file, line, method (default: true)
    o.SendToOpenTelemetry = true;     // enrich the current Activity/span (default: true)
    o.IncludeSourceContext = false;   // read source lines from disk for the local banner (default: false)

    o.Capture.Enabled = true;              // switch capture off at runtime without rebuilding

    o.Capture.MaxFramesPerException = 5;   // instrumented frames per exception, innermost first
    o.Capture.MaxDepth = 3;                // object-graph depth (customer.Address.City = 2)
    o.Capture.MaxMembersPerObject = 20;
    o.Capture.MaxCollectionItems = 5;
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
| Whole project | `<ErrorSightWeave>false</ErrorSightWeave>` in the `.csproj` |
| Test projects | Not woven by default (`IsTestProject`); set `<ErrorSightWeave>true</ErrorSightWeave>` to opt in |
| Assembly | `[assembly: ErrorSight.ErrorSightIgnore]` |
| Type or method (hot paths) | `[ErrorSightIgnore]` |
| Tiny methods | Skipped automatically when the IL body is ≤ 16 bytes (`<ErrorSightMinILSize>`), so the JIT keeps inlining them |
| At runtime | `o.Capture.Enabled = false` |

## Performance

Measured with `benchmarks/ErrorSight.Benchmarks` (.NET 8, Release). It compares identical code with and
without instrumentation:

| | Not instrumented | Instrumented |
|---|---|---|
| Call that doesn't throw | 3.8 ns | 4.7 ns (+0.9 ns) |
| Throw + catch through 3 frames, every frame captured (default level) | 7.6 µs | 16.0 µs (+8.4 µs, +4.5 KB) |
| … same with `DataCapture.Values` | 7.6 µs | 17.5 µs |
| … same, beyond the default 200 captures/s budget | 7.6 µs | ≈ 11.8 µs |
| Diagnose + write span attributes/event (only for exceptions that reach a span) | | ≈ 50 µs |

Numbers come from a shared cloud container and vary by a few µs between runs.

- **Code that doesn't throw pays almost nothing.** The filter only runs when an exception passes through the
  method. The one real cost is that on .NET 8 the JIT does not inline methods that contain exception handlers,
  which is why methods of 16 IL bytes or fewer are left alone.
- **Exceptions cost more, and the cost is bounded.** Each captured frame costs a few µs, frames per
  exception are capped (5), and a process-wide rate limit protects against exception storms.
- **Formatting is lazy.** Exceptions your code catches and handles pay only for the capture, never for
  formatting or diagnosis.

## Limits (by design, today)

- **Release builds keep single-use locals on the evaluation stack**, so those locals are not captured.
  Everything else still is: parameters, `this`, fields, and locals that live longer. An eliminated `foreach`
  variable is reported as an element of its collection, e.g. `customers[…].Address`.
- When a receiver cannot be evaluated (a method-call result such as `GetCustomer().Address`, or beyond the
  depth limit), ErrorSight reports the possible culprits in `errorsight.null.candidates` instead of
  guessing.
- Not instrumented yet: strong-name-signed assemblies (skipped with a build warning), third-party DLLs,
  VB/F#, and NativeAOT/trimmed apps.

## Beyond spans

The diagnosis is also available directly:

```csharp
// Hosted apps: inject it. Without DI: ExceptionEnricher.CreateDefault()
var diagnostics = enricher.Enrich(exception);
Console.WriteLine(new TextExceptionFormatter().Format(diagnostics));  // human-readable banner
var json = new JsonExceptionFormatter().Format(diagnostics);           // per-frame variables at the configured level
```

Captured frames can be read with `ErrorSightRuntime.TryGetCapturedFrames(exception, out var frames)`.

The optional `app.UseErrorSight()` middleware logs the text banner through `ILogger` for failed requests. It
can also return a problem-details body (`IncludeDiagnosticsInResponse`, meant for development only). Span
enrichment does not need it.

Outside a generic host (a plain `ServiceCollection`), start the hooks yourself:
`provider.GetRequiredService<ErrorSightTelemetry>().Start()`.

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
src/ErrorSight            runtime: capture store, masking, root-cause engine, OpenTelemetry hooks
src/ErrorSight/build      MSBuild targets shipped in the package (build/ and buildTransitive/)
src/ErrorSight.Weaver     build-time IL weaver (Mono.Cecil), shipped in the package's tools/weaver
src/Shared                metadata format shared by weaver and runtime
tests/ErrorSight.Samples  plain application code, woven by the real targets
tests/ErrorSight.Tests    capture, privacy levels, semantics, masking and end-to-end OpenTelemetry tests
benchmarks/               overhead measurements
```

## License

MIT
