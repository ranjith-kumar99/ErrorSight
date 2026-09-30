using ErrorSight.Analyzers;
using ErrorSight.Core;
using ErrorSight.Options;
using FluentAssertions;

namespace ErrorSight.Tests.Core;

public sealed class ExceptionEnricherTests
{
    private static ExceptionEnricher BuildEnricher(DataCapture level = DataCapture.None) => new(
        new IExceptionAnalyzer[]
        {
            new NullReferenceAnalyzer(),
            new ArgumentNullAnalyzer(),
            new KeyNotFoundAnalyzer(),
            new IndexOutOfRangeAnalyzer(),
            new InvalidOperationAnalyzer(),
            new AggregateExceptionAnalyzer()
        },
        new ErrorSightOptions { DataCapture = level });

    // ── Basic identity ────────────────────────────────────────────────────────

    [Fact]
    public void Enrich_SetsExceptionType()
    {
        var ex = new InvalidOperationException("test");
        var result = BuildEnricher().Enrich(ex);
        result.ExceptionType.Should().Be("InvalidOperationException");
    }

    [Fact]
    public void Enrich_Default_DoesNotCaptureTheMessage()
    {
        var result = BuildEnricher().Enrich(new Exception("customer jane@example.com not found"));
        result.Message.Should().BeNull("messages often contain application data");
        result.DataCapture.Should().Be(DataCapture.None);
    }

    [Fact]
    public void Enrich_Values_SetsMessage()
    {
        var ex = new Exception("hello world");
        var result = BuildEnricher(DataCapture.Values).Enrich(ex);
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
        BuildEnricher().Enrich(caughtEx!).MissingKey.Should().BeNull("the key is application data");
        var result = BuildEnricher(DataCapture.Values).Enrich(caughtEx!);
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

    // ── Exception.Data / caching ──────────────────────────────────────────────

    [Fact]
    public void ExceptionData_SensitiveKeysAreMasked()
    {
        var ex = new InvalidOperationException("x");
        ex.Data["password"] = "hunter2";
        ex.Data["orderId"] = 42;

        BuildEnricher().Enrich(ex).AdditionalData.Should().NotContainKey("orderId", "exception data is only captured with values");
        var result = BuildEnricher(DataCapture.Values).Enrich(ex);
        result.AdditionalData["password"].Should().Be("***");
        result.AdditionalData["orderId"].Should().Be(42);
    }

    [Fact]
    public void Enrich_IsCachedPerException()
    {
        var enricher = BuildEnricher();
        var ex = new InvalidOperationException("x");
        enricher.Enrich(ex).Should().BeSameAs(enricher.Enrich(ex));
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
        var options = new ErrorSightOptions();
        options.ShouldEnrich(new OperationCanceledException()).Should().BeFalse();
    }

    [Fact]
    public void Options_ShouldEnrich_AllowsOtherExceptions()
    {
        var options = new ErrorSightOptions();
        options.ShouldEnrich(new InvalidOperationException()).Should().BeTrue();
    }
}
