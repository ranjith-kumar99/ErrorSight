using ErrorSight.Formatting;
using ErrorSight.Options;
using SampleApp;

namespace ErrorSight.Tests.Weaving;

/// <summary>Level 1 (default) structure only, level 2 metadata, level 3 values.</summary>
[Collection(RuntimeCollection.Name)]
public sealed class DataCaptureTests
{
    private static readonly string[] Secrets = { "Jane Doe", "jane@example.com", "hunter2", "1837", "VIP", "A-1" };

    private readonly OrderService _service = new();

    [Fact]
    public void Default_FindsTheNullExpression_WithoutCapturingAnyValue()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()));

        d.DataCapture.Should().Be(DataCapture.None);
        d.NullExpression.Should().Be("order.Customer.Address");
        d.FailingExpression.Should().Be("order.Customer.Address.City.Name");
        d.SourceFileShort.Should().Be("OrderService.cs");
        d.Values.Should().BeEmpty();
        d.Message.Should().BeNull();
        d.NullType.Should().BeNull("types are metadata (level 2)");
        d.Frames[0].Values.Should().OnlyContain(v => v.Value == null && v.Members == null && v.Count == null);

        var output = new JsonExceptionFormatter().Format(d) + new TextExceptionFormatter().Format(d);
        foreach (var secret in Secrets) output.Should().NotContain(secret);
    }

    [Fact]
    public void Default_KeyNotFound_DoesNotReportTheKey()
    {
        var d = Diagnose.Run(() => _service.PriceOf("secret-sku"));

        d.MissingKey.Should().BeNull();
        d.CollectionName.Should().Be("this._prices");
        d.PossibleCause.Should().Be("The requested key does not exist in this._prices.");
        (new JsonExceptionFormatter().Format(d)).Should().NotContain("SECRET-SKU");
    }

    [Fact]
    public void Metadata_ReportsTypesAndCounts_NotValues()
    {
        var d = Diagnose.Run(() => _service.LineQuantityFromList(Build.OrderWithoutAddress(), 9),
            o => o.DataCapture = DataCapture.Metadata);

        d.CollectionType.Should().Be("List<OrderLine>");
        d.CollectionLength.Should().Be(2);
        d.RequestedIndex.Should().BeNull("the index is a value");
        d.Values.Should().BeEmpty();
        var json = new JsonExceptionFormatter().Format(d);
        foreach (var secret in Secrets) json.Should().NotContain(secret);
    }

    [Fact]
    public void Metadata_ReportsTheTypeOfTheNullObject()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()), o => o.DataCapture = DataCapture.Metadata);
        d.NullType.Should().Be("Address");
    }

    [Fact]
    public void Values_OnlyTheFailingChainIsReported()
    {
        var d = Diagnose.Values(() => _service.ProcessOrder(Build.OrderWithoutAddress()));

        d.Values.Keys.Should().Equal("order", "order.Customer", "order.Customer.Address");
        d.Values["order.Customer.Address"].Should().BeNull();
    }

    // ── Where the null came from ─────────────────────────────────────────────

    [Fact]
    public void Origin_NullLocal_PointsAtItsAssignment()
    {
        var d = Diagnose.Run(() => _service.ShippingCity(new Customer { Name = "No address" }));

        d.NullExpression.Should().Be("address");
        d.NullOrigin!.Variable.Should().Be("address");
        d.NullOrigin.Expression.Should().Be("customer.Address");
        var source = File.ReadAllLines(AutoCaptureTests.SamplesSource("OrderService.cs"));
        source[d.NullOrigin.Line!.Value - 1].Should().Contain("var address = customer.Address;");
        source[d.Line!.Value - 1].Should().Contain("address.City.Name");
        d.PossibleCause.Should().StartWith("address is null. It was assigned customer.Address on line");
    }

    [Fact]
    public void Origin_ExplicitNull_IsReported()
    {
        var d = Diagnose.Run(() => _service.Login("jane", "pw"));

        d.NullExpression.Should().Be("sessionToken");
        d.NullOrigin!.Expression.Should().Be("null");
    }

    [Fact]
    public void Origin_IsShownInTheBanner()
    {
        var d = Diagnose.Run(() => _service.ShippingCity(new Customer()));
        var text = new TextExceptionFormatter().Format(d);

        text.Should().Contain("Origin:").And.Contain("assigned to address").And.Contain("dereferenced");
    }
}
