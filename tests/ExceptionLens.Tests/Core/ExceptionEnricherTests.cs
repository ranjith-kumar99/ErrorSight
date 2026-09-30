using ExceptionLens.Analyzers;
using ExceptionLens.Core;
using ExceptionLens.Extensions;
using ExceptionLens.Options;
using FluentAssertions;

namespace ExceptionLens.Tests.Core;

public sealed class ExceptionEnricherTests
{
    private static ExceptionEnricher BuildEnricher() => new(
        new IExceptionAnalyzer[]
        {
            new NullReferenceAnalyzer(),
            new ArgumentNullAnalyzer(),
            new KeyNotFoundAnalyzer(),
            new IndexOutOfRangeAnalyzer(),
            new InvalidOperationAnalyzer(),
            new AggregateExceptionAnalyzer()
        },
        new ExceptionLensOptions());

    // ── Basic identity ────────────────────────────────────────────────────────

    [Fact]
    public void Enrich_SetsExceptionType()
    {
        var ex = new InvalidOperationException("test");
        var result = BuildEnricher().Enrich(ex);
        result.ExceptionType.Should().Be("InvalidOperationException");
    }

    [Fact]
    public void Enrich_SetsMessage()
    {
        var ex = new Exception("hello world");
        var result = BuildEnricher().Enrich(ex);
        result.Message.Should().Be("hello world");
    }

    [Fact]
    public void Enrich_SetsTimestamp()
    {
        var before = DateTime.UtcNow;
        var result = BuildEnricher().Enrich(new Exception("x"));
        result.Timestamp.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Enrich_SetsCorrelationId()
    {
        var result = BuildEnricher().Enrich(new Exception("x"), "trace-abc");
        result.CorrelationId.Should().Be("trace-abc");
    }

    // ── ArgumentNullException ─────────────────────────────────────────────────

    [Fact]
    public void Enrich_ArgumentNullException_SetsParameterName()
    {
        var ex = new ArgumentNullException("customer");
        var result = BuildEnricher().Enrich(ex);
        result.ParameterName.Should().Be("customer");
        result.NullExpression.Should().Be("customer");
        result.Values.Should().ContainKey("customer");
        result.Values["customer"].Should().BeNull();
    }

    [Fact]
    public void Enrich_ArgumentNullException_HasPossibleCause()
    {
        var ex = new ArgumentNullException("order");
        var result = BuildEnricher().Enrich(ex);
        result.PossibleCause.Should().Contain("order");
    }

    // ── KeyNotFoundException ──────────────────────────────────────────────────

    [Fact]
    public void Enrich_KeyNotFoundException_ExtractsMissingKey()
    {
        var dict = new Dictionary<string, int> { ["a"] = 1 };
        Exception? caughtEx = null;
        try { _ = dict["missing-key"]; }
        catch (KeyNotFoundException ex) { caughtEx = ex; }

        caughtEx.Should().NotBeNull();
        var result = BuildEnricher().Enrich(caughtEx!);
        result.MissingKey.Should().Be("missing-key");
    }

    // ── InvalidOperationException (LINQ) ─────────────────────────────────────

    [Fact]
    public void Enrich_InvalidOperation_EmptySequence_SetsCause()
    {
        Exception? caughtEx = null;
        try { _ = Enumerable.Empty<int>().First(); }
        catch (InvalidOperationException ex) { caughtEx = ex; }

        caughtEx.Should().NotBeNull();
        var result = BuildEnricher().Enrich(caughtEx!);
        result.PossibleCause.Should().Contain("empty");
        result.Suggestion.Should().Contain("FirstOrDefault");
    }

    [Fact]
    public void Enrich_InvalidOperation_MoreThanOne_SetsCause()
    {
        Exception? caughtEx = null;
        try { _ = new[] { 1, 2 }.Single(); }
        catch (InvalidOperationException ex) { caughtEx = ex; }

        caughtEx.Should().NotBeNull();
        var result = BuildEnricher().Enrich(caughtEx!);
        result.PossibleCause.Should().Contain("Single");
    }

    // ── IndexOutOfRangeException ──────────────────────────────────────────────

    [Fact]
    public void Enrich_IndexOutOfRange_SetsCause()
    {
        var arr = new int[3];
        Exception? caughtEx = null;
        try { _ = arr[10]; }
        catch (IndexOutOfRangeException ex) { caughtEx = ex; }

        caughtEx.Should().NotBeNull();
        var result = BuildEnricher().Enrich(caughtEx!);
        result.ExceptionType.Should().Be("IndexOutOfRangeException");
        result.PossibleCause.Should().NotBeNullOrEmpty();
    }

    // ── AggregateException ────────────────────────────────────────────────────

    [Fact]
    public void Enrich_AggregateException_CountsInnerExceptions()
    {
        var agg = new AggregateException(
            new InvalidOperationException("a"),
            new ArgumentNullException("b"));

        var result = BuildEnricher().Enrich(agg);
        result.AdditionalData.Should().ContainKey("innerExceptionCount");
        result.AdditionalData["innerExceptionCount"].Should().Be(2);
    }

    // ── Capture() extension ───────────────────────────────────────────────────

    [Fact]
    public void Capture_AttachesValuesToException()
    {
        var customer = new { Id = 1837 };
        object? address = null;
        var ex = new NullReferenceException()
            .Capture(new { customer, customerAddress = address });

        var result = BuildEnricher().Enrich(ex);
        result.Values.Should().ContainKey("customer");
        result.Values.Should().ContainKey("customerAddress");
        result.Values["customerAddress"].Should().BeNull();
    }

    [Fact]
    public void NullAt_SetsNullExpression()
    {
        var ex = new NullReferenceException()
            .NullAt("customer.Address");

        var result = BuildEnricher().Enrich(ex);
        result.NullExpression.Should().Be("customer.Address");
    }

    [Fact]
    public void WithCorrelationId_SetsId()
    {
        var ex = new Exception("x").WithCorrelationId("req-123");
        var result = BuildEnricher().Enrich(ex);
        result.CorrelationId.Should().Be("req-123");
    }

    // ── Inner exception ───────────────────────────────────────────────────────

    [Fact]
    public void Enrich_IncludesInnerException()
    {
        var inner = new ArgumentNullException("param1");
        var outer = new InvalidOperationException("outer", inner);
        var result = BuildEnricher().Enrich(outer);

        result.InnerException.Should().NotBeNull();
        result.InnerException!.ExceptionType.Should().Be("ArgumentNullException");
        result.InnerException.ParameterName.Should().Be("param1");
    }

    // ── Options ───────────────────────────────────────────────────────────────

    [Fact]
    public void Options_ShouldEnrich_SkipsOperationCanceled()
    {
        var options = new ExceptionLensOptions();
        options.ShouldEnrich(new OperationCanceledException()).Should().BeFalse();
    }

    [Fact]
    public void Options_ShouldEnrich_AllowsOtherExceptions()
    {
        var options = new ExceptionLensOptions();
        options.ShouldEnrich(new InvalidOperationException()).Should().BeTrue();
    }
}
