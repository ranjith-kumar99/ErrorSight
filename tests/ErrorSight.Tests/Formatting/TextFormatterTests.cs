using ErrorSight.Core;
using ErrorSight.Formatting;
using FluentAssertions;

namespace ErrorSight.Tests.Formatting;

public sealed class TextFormatterTests
{
    private static TextExceptionFormatter Formatter() => new();

    [Fact]
    public void Format_ContainsBanner()
    {
        var d = Diagnostics("NullReferenceException");
        Formatter().Format(d).Should().Contain("ErrorSight");
    }

    [Fact]
    public void Format_ContainsDividers()
    {
        var d = Diagnostics("NullReferenceException");
        Formatter().Format(d).Should().Contain("────");
    }

    [Fact]
    public void Format_ContainsExceptionType()
    {
        var d = Diagnostics("KeyNotFoundException");
        Formatter().Format(d).Should().Contain("KeyNotFoundException");
    }

    [Fact]
    public void Format_NullExpression_ShowsNullValueSection()
    {
        var d = Diagnostics("NullReferenceException");
        d.NullExpression = "customer.Address";
        var output = Formatter().Format(d);
        output.Should().Contain("NULL VALUE");
        output.Should().Contain("customer.Address");
    }

    [Fact]
    public void Format_Values_ShowsRuntimeValues()
    {
        var d = Diagnostics("NullReferenceException");
        d.Values["customer"] = "Customer { Id = 1837 }";
        d.Values["customer.Address"] = null;
        var output = Formatter().Format(d);
        output.Should().Contain("Runtime values");
        output.Should().Contain("null");
    }

    [Fact]
    public void Format_CallChain_ShowsArrows()
    {
        var d = Diagnostics("NullReferenceException");
        d.CallChain.Add(new CallFrame { TypeName = "OrderService", MethodName = "GetCity" });
        d.CallChain.Add(new CallFrame { TypeName = "OrderService", MethodName = "ProcessOrder" });
        d.CallChain.Add(new CallFrame { TypeName = "OrderController", MethodName = "CreateOrder" });
        var output = Formatter().Format(d);
        output.Should().Contain("Call path");
        output.Should().Contain("↓");
        output.Should().Contain("OrderService");
    }

    [Fact]
    public void Format_PossibleCause_IsIncluded()
    {
        var d = Diagnostics("NullReferenceException");
        d.PossibleCause = "customer.Address is null.";
        Formatter().Format(d).Should().Contain("Possible cause");
        Formatter().Format(d).Should().Contain("customer.Address is null.");
    }

    [Fact]
    public void Format_MissingKey_ShowsKeySection()
    {
        var d = Diagnostics("KeyNotFoundException");
        d.MissingKey = "abc123";
        d.CollectionName = "_sessionCache";
        var output = Formatter().Format(d);
        output.Should().Contain("Missing key");
        output.Should().Contain("abc123");
        output.Should().Contain("_sessionCache");
    }

    [Fact]
    public void Format_IndexInfo_ShowsRangeSection()
    {
        var d = Diagnostics("IndexOutOfRangeException");
        d.RequestedIndex = 10;
        d.CollectionLength = 10;
        d.ValidIndexRange = "0–9";
        d.CollectionName = "orders";
        var output = Formatter().Format(d);
        output.Should().Contain("Requested index");
        output.Should().Contain("Collection length");
        output.Should().Contain("Valid range");
        output.Should().Contain("0–9");
    }

    private static ExceptionDiagnostics Diagnostics(string type) => new()
    {
        ExceptionType = type,
        FullTypeName = $"System.{type}",
        Message = "Test message"
    };
}
