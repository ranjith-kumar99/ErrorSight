# ExceptionLens

**Zero-config exception enrichment for .NET** — turns cryptic runtime errors into structured, actionable diagnostics.

```
ExceptionLens
────────────────────────────────────────

NullReferenceException

❌ NULL VALUE:
  customer.Address

Expression:
  customer.Address.City.Name

Location:
  OrderService.cs:42
  OrderService.GetCity()

Runtime values:
  customer              =  Customer { Id = 1837 }
  customer.Address      =  null

Call path:
  OrderController.CreateOrder()
    ↓
  OrderService.ProcessOrder()
    ↓
  OrderService.GetCity()

Possible cause:
  customer.Address is null.

Suggestion:
  Check whether customer.Address is initialised before accessing its members.

────────────────────────────────────────
```

## Installation

```xml
<PackageReference Include="ExceptionLens" Version="1.0.0" />
```

## Setup

### ASP.NET Core

```csharp
// Program.cs
builder.Services.AddExceptionLens();

// ...

app.UseExceptionLens();   // place before MapControllers
app.MapControllers();
```

### Worker / Console / Lambda

```csharp
services.AddExceptionLens();

// then inject and use the enricher directly:
var enricher = serviceProvider.GetRequiredService<ExceptionEnricher>();

try { ... }
catch (Exception ex)
{
    var diagnostics = enricher.Enrich(ex);
    // log, ship to Serilog/OpenTelemetry/Datadog, etc.
}
```

## What it enriches (zero-code-change)

| Exception type | Extra fields |
|---|---|
| `NullReferenceException` | Failing source line, call chain, possible cause |
| `ArgumentNullException` | `ParameterName`, null value in `Values` |
| `ArgumentException` | `ParameterName`, caller info |
| `KeyNotFoundException` | `MissingKey` parsed from message, `CollectionName` from source |
| `IndexOutOfRangeException` | `RequestedIndex`, `CollectionLength`, `ValidIndexRange` |
| `ArgumentOutOfRangeException` | `RequestedIndex` from `ActualValue` |
| `InvalidOperationException` | LINQ operation name, empty-sequence / duplicate-key patterns |
| `AggregateException` | Inner exception count, first inner exception summary |
| Any | Stack-trace source location (file, line, method, type) |

## Attach runtime values (optional)

For `NullReferenceException`, ExceptionLens can't know which sub-expression was null without source files present. Attach a snapshot explicitly:

```csharp
catch (NullReferenceException ex)
{
    throw ex.Capture(new { customer, customerAddress = customer?.Address })
            .NullAt("customer.Address");
}
```

With that context, ExceptionLens shows:

```
Runtime values:
  customer              =  Customer { Id = 1837 }
  customerAddress       =  null

❌ NULL VALUE:
  customer.Address
```

Additional helpers:

```csharp
ex.CaptureValue("orderId", order.Id)          // single named value
ex.WithCorrelationId(HttpContext.TraceIdentifier)
ex.NullAt("order.Customer.Address")           // explicit null expression
```

## Structured output (JSON)

```csharp
var json = serviceProvider.GetRequiredService<JsonExceptionFormatter>()
                          .Format(diagnostics);
```

```json
{
  "exceptionType": "NullReferenceException",
  "nullExpression": "customer.Address",
  "sourceFileShort": "OrderService.cs",
  "line": 42,
  "method": "GetCity()",
  "possibleCause": "customer.Address is null.",
  "values": {
    "customer": "Customer { Id = 1837 }",
    "customer.Address": null
  }
}
```

Ship this to Serilog, Application Insights, CloudWatch, OpenTelemetry, Sentry, Datadog, or ELK.

## Serilog / structured logging

ExceptionLens enriches every `ILogger.LogError(ex, ...)` call automatically
when `StructuredLogging = true` (the default). The following properties
appear as first-class log fields:

| Property | Description |
|---|---|
| `ExceptionLens.Type` | Exception class name |
| `ExceptionLens.File` | Source file (short) |
| `ExceptionLens.Line` | Line number |
| `ExceptionLens.Method` | Method name |
| `ExceptionLens.NullExpression` | Null sub-expression |
| `ExceptionLens.PossibleCause` | Human-readable cause |
| `ExceptionLens.Values` | JSON-serialised runtime values |

## OpenTelemetry

```csharp
// Inject ExceptionLensActivityProcessor and call on the catch path:
var processor = serviceProvider.GetRequiredService<ExceptionLensActivityProcessor>();
processor.EnrichCurrentActivity(exception);
```

Tags are added to the current `Activity`: `exceptionlens.type`, `exceptionlens.null_expression`, `exceptionlens.cause`, `exceptionlens.values`, etc.

## Options

```csharp
builder.Services.AddExceptionLens(opt =>
{
    opt.IncludeSourceContext = true;       // read source lines from PDB (default: true)
    opt.SourceContextLines = 2;            // context lines above/below (default: 2)
    opt.IncludeInnerException = true;      // recurse into inner exception (default: true)
    opt.LogEnrichedDiagnostics = true;     // emit banner to ILogger (default: true)
    opt.StructuredLogging = true;          // add scope properties (default: true)
    opt.IncludeDiagnosticsInResponse = false; // include in HTTP 500 body (default: false)
    opt.ShouldEnrich = ex =>               // skip OperationCanceledException by default
        ex is not OperationCanceledException;
});
```

## Custom analyzers

```csharp
public class MyDomainExceptionAnalyzer : IExceptionAnalyzer
{
    public bool CanAnalyze(Exception ex) => ex is MyDomainException;

    public void Enrich(ExceptionDiagnostics d, Exception ex)
    {
        var domain = (MyDomainException)ex;
        d.Values["entityId"] = domain.EntityId;
        d.PossibleCause = $"Entity {domain.EntityId} violated domain rule: {domain.Rule}";
    }
}

// Register:
builder.Services.AddExceptionAnalyzer<MyDomainExceptionAnalyzer>();
```

## Integration map

```
ExceptionLens
      │
      ├── ASP.NET Core      builder.Services.AddExceptionLens() + app.UseExceptionLens()
      ├── Worker Service    builder.Services.AddExceptionLens()  →  inject ExceptionEnricher
      ├── Console app       services.AddExceptionLens()          →  inject ExceptionEnricher
      ├── AWS Lambda        services.AddExceptionLens()          →  inject ExceptionEnricher
      ├── Azure Functions   services.AddExceptionLens()          →  inject ExceptionEnricher
      ├── Serilog           automatic via ILogger enrichment
      ├── NLog              automatic via ILogger enrichment
      └── OpenTelemetry     ExceptionLensActivityProcessor.EnrichCurrentActivity(ex)
```

## License

MIT
