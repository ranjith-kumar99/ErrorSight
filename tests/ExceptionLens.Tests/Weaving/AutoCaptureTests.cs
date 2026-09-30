using SampleApp;

namespace ExceptionLens.Tests.Weaving;

/// <summary>
/// The sample code contains no try/catch and no ExceptionLens calls: everything asserted here is
/// captured automatically by the build-time instrumentation.
/// </summary>
[Collection(RuntimeCollection.Name)]
public sealed class AutoCaptureTests
{
    private readonly OrderService _service = new();

    // ── NullReferenceException: which expression was null ───────────────────

    [Fact]
    public void NullAddress_IsIdentified()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()));

        d.NullExpression.Should().Be("order.Customer.Address");
        d.FailingExpression.Should().Be("order.Customer.Address.City.Name");
        d.PossibleCause.Should().Be("order.Customer.Address is null.");
        d.Values.Should().ContainKey("order.Customer.Address").WhoseValue.Should().BeNull();
        d.Text("order").Should().StartWith("Order { Id = 1837");
        d.Text("order.Customer").Should().Contain("Name = \"Jane Doe\"");
    }

    [Fact]
    public void NullCity_IsIdentified()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutCity()));

        d.NullExpression.Should().Be("order.Customer.Address.City");
        d.Values["order.Customer.Address.City"].Should().BeNull();
        d.Text("order.Customer.Address").Should().Contain("Street = \"Main St 1\"");
    }

    [Fact]
    public void NullCustomer_IsIdentified()
    {
        var d = Diagnose.Run(() => _service.GetCity(new Order { Id = 5 }));
        d.NullExpression.Should().Be("order.Customer");
    }

    [Fact]
    public void NullParameter_IsIdentified()
    {
        var d = Diagnose.Run(() => _service.GetCity(null!));
        d.NullExpression.Should().Be("order");
        d.Values["order"].Should().BeNull();
    }

    [Fact]
    public void NullLocal_IsIdentified_AndOtherValuesCaptured()
    {
        var d = Diagnose.Run(() => _service.Login("  jane ", "hunter2"));

        d.NullExpression.Should().Be("sessionToken");
        d.Values["sessionToken"].Should().BeNull();
        d.Text("userName").Should().Be("\"  jane \"");
#if DEBUG
        d.Text("normalized").Should().Be("\"jane\""); // Release keeps single-use locals on the stack
#endif
    }

    [Fact]
    public void StaticMethod_IsInstrumented()
    {
        var d = Diagnose.Run(() => OrderService.Upper(null!));
        d.NullExpression.Should().Be("text");
    }

    [Fact]
    public void StructMethod_ReportsThisMember()
    {
        var point = new Point { X = 3, Label = null! };
        var d = Diagnose.Run(() => point.LabelLength());

        d.NullExpression.Should().Be("this.Label");
        d.Text("this").Should().Contain("X = 3");
        d.Text("offset").Should().Be("6");
    }

    [Fact]
    public void Constructor_IsInstrumented_AfterBaseCall()
    {
        var d = Diagnose.Run(() => new Invoice(new Order { Id = 9 }, copies: 2));

        d.NullExpression.Should().Be("order.Customer");
        d.Text("copies").Should().Be("2");
        d.Text("header").Should().Be("\"INVOICE\"");
    }

    [Fact]
    public void RefOutAndSpanParameters_DoNotBreakCapture()
    {
        var counter = 0;
        string? result = null;
        var d = Diagnose.Run(() => _service.Parse("abc".AsSpan(), ref counter, out result));

        d.NullExpression.Should().Be("result");
        d.Text("counter").Should().Be("1");
        d.Values.Should().NotContainKey("text").And.NotContainKey("buffer"); // ref structs are skipped
    }

    // ── Async, iterators, lambdas, local functions, generics ─────────────────

    [Fact]
    public async Task AsyncMethod_IsIdentified()
    {
        var d = await Diagnose.RunAsync(() => _service.GetCityAsync(Build.OrderWithoutAddress()));

        d.NullExpression.Should().Be("order.Customer.Address");
        d.Frames[0].Method.Should().Be("OrderService.GetCityAsync");
        d.Text("order").Should().StartWith("Order { Id = 1837");
#if DEBUG
        d.Text("customerName").Should().Be("\"Jane Doe\""); // Release keeps single-use locals on the stack
#endif
    }

    [Fact]
    public async Task AsyncCaller_FramesAreCaptured()
    {
        var d = await Diagnose.RunAsync(() => _service.ProcessOrderAsync(Build.OrderWithoutAddress()));

        d.NullExpression.Should().Be("order.Customer.Address");
        d.Frames.Select(f => f.Method).Should().StartWith(new[] { "OrderService.GetCityAsync", "OrderService.ProcessOrderAsync" });
        d.Frames[1].Values.Should().Contain(v => v.Name == "attempt" && v.Value == "3");
    }

    [Fact]
    public void SyncCaller_FramesAreCaptured()
    {
        var d = Diagnose.Run(() => _service.ProcessOrder(Build.OrderWithoutAddress()));

        d.Frames.Select(f => f.Method).Should().StartWith(new[] { "OrderService.GetCity", "OrderService.ProcessOrder" });
        d.Frames[1].Values.Should().Contain(v => v.Name == "orderId" && v.Value == "1837");
#if DEBUG
        // Release builds keep single-use locals on the evaluation stack, so they have no value to capture.
        d.Frames[1].Values.Should().Contain(v => v.Name == "label" && v.Value == "\"order-1837\"");
#endif
    }

    [Fact]
    public void Lambda_CapturedVariablesAreVisible()
    {
        var d = Diagnose.Run(() => _service.ViaLambda(Build.OrderWithoutAddress()));

        d.NullExpression.Should().Be("order.Customer.Address");
        d.Text("prefix").Should().Be("\"city:\"");
    }

    [Fact]
    public void LocalFunction_CapturedVariablesAreVisible()
    {
        var d = Diagnose.Run(() => _service.ViaLocalFunction(Build.OrderWithoutAddress()));

        d.NullExpression.Should().Be("order.Customer.Address");
        d.Text("suffix").Should().Be("\"!\"");
    }

    [Fact]
    public void Iterator_IsIdentified()
    {
        var customers = new[] { new Customer { Name = "No address" } };
        var d = Diagnose.Run(() => _service.CityNames(customers).ToList());

#if DEBUG
        d.NullExpression.Should().Be("customer.Address");
        d.Text("customer").Should().Contain("Name = \"No address\"");
#else
        // Release keeps the foreach variable on the stack: reported as "an element of customers".
        d.NullExpression.Should().Be("customers[…].Address");
        d.Values["customers[…].Address"].Should().BeNull();
#endif
    }

    [Fact]
    public async Task AsyncIterator_IsIdentified()
    {
        var customers = new[] { new Customer { Name = "No address" } };
        var d = await Diagnose.RunAsync(async () =>
        {
            await foreach (var _ in _service.CityNamesAsync(customers)) { }
        });

        d.NullExpression.Should().BeOneOf("customer.Address", "customers[…].Address");
    }

    [Fact]
    public void GenericMethodOnGenericType_IsInstrumented()
    {
        var repository = new Repository<Customer>();
        var d = Diagnose.Run(() => repository.Describe(new Customer(), 7, _ => null!));

        d.NullExpression.Should().Be("text");
        d.Text("key").Should().Be("7");
    }

    [Fact]
    public void Recursion_CapturesInnermostFrameOnce()
    {
        var d = Diagnose.Run(() => _service.Recurse(Build.OrderWithoutAddress(), 3));

        d.NullExpression.Should().Be("order.Customer.Address");
        d.Frames.Should().ContainSingle(f => f.Method == "OrderService.Recurse");
        d.Text("depth").Should().Be("0");
    }

    // ── Other exception types benefit from the same values ───────────────────

    [Fact]
    public void KeyNotFound_ShowsTheKeyVariable()
    {
        var d = Diagnose.Run(() => _service.PriceOf("zz-9"));

        d.MissingKey.Should().Be("ZZ-9");
        d.Text("key").Should().Be("\"ZZ-9\"");
        d.Text("sku").Should().Be("\"zz-9\"");
        d.CollectionName.Should().Be("this._prices");
    }

    [Fact]
    public void IndexOutOfRange_ReportsCollectionIndexAndLength()
    {
        var d = Diagnose.Run(() => _service.LineQuantity(Build.OrderWithoutAddress(), 5));

        d.CollectionName.Should().BeOneOf("lines", "order.Lines.ToArray(…)");
        d.RequestedIndex.Should().Be(5);
        d.CollectionLength.Should().Be(2);
        d.ValidIndexRange.Should().Be("0–1");
    }

    [Fact]
    public void ListIndexer_ArgumentOutOfRange_ReportsCollectionAndIndex()
    {
        var d = Diagnose.Run(() => _service.LineQuantityFromList(Build.OrderWithoutAddress(), 9));

        d.CollectionName.Should().BeOneOf("lines", "order.Lines");
        d.RequestedIndex.Should().Be(9);
        d.CollectionLength.Should().Be(2);
    }

    [Fact]
    public void GenericRepository_EmptyCollection()
    {
        var repository = new Repository<Customer>();
        var d = Diagnose.Run(() => repository.Get(3), o => o.Capture.IncludePrivateFields = true);

        d.CollectionName.Should().BeOneOf("items", "this._items");
        d.RequestedIndex.Should().Be(3);
        d.CollectionLength.Should().Be(0);
    }

    [Fact]
    public void Release_ReportedOffsetBeforeFault_StillFindsTheNullLocal()
    {
        // In Release the JIT reports the start of the stack-empty region (line of `normalized`),
        // not the faulting `sessionToken.Trim()`; the line is corrected from the resolved access.
        var d = Diagnose.Run(() => _service.Login("jane", "pw"));

        d.NullExpression.Should().Be("sessionToken");
        var sourceLine = File.ReadAllLines(SamplesSource("OrderService.cs"))[d.Line!.Value - 1];
        sourceLine.Should().Contain("sessionToken.Trim()");
    }

    [Fact]
    public void TinyMethods_AreNotInstrumented_ButTheCallerIs()
    {
        var d = Diagnose.Run(() => _service.NameLength(new Customer { Name = null! }));

        d.Frames.Should().NotContain(f => f.Method == "OrderService.LengthOf");
        d.Frames[0].Method.Should().Be("OrderService.NameLength");
        d.Frames[0].Values.Should().Contain(v => v.Name == "customer");
    }

    // ── Opt-out and switches ─────────────────────────────────────────────────

    [Fact]
    public void IgnoredMethod_IsNotInstrumented()
    {
        var d = Diagnose.Run(() => _service.Ignored(Build.OrderWithoutAddress()));

        d.Frames.Should().BeEmpty();
        d.NullExpression.Should().BeNull();
    }

    [Fact]
    public void CaptureRuntimeValues_False_DisablesCapture()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()), o => o.CaptureRuntimeValues = false);

        d.Frames.Should().BeEmpty();
        d.Values.Should().BeEmpty();
    }

    [Fact]
    public void CaptureSourceLocation_False_OmitsFileAndLine()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()), o => o.CaptureSourceLocation = false);

        d.SourceFile.Should().BeNull();
        d.Line.Should().BeNull();
        d.NullExpression.Should().Be("order.Customer.Address");
    }

    [Fact]
    public void OperationCanceled_IsNotCaptured()
    {
        Diagnose.Options();
        var exception = new OperationCanceledException();
        Runtime.WeavingHooks.OnFilter(exception, default, typeof(OrderService).TypeHandle, 1, Array.Empty<object>());
        Runtime.ExceptionLensRuntime.TryGetCapturedFrames(exception, out _).Should().BeFalse();
    }

    [Fact]
    public void SourceLocation_PointsAtTheFailingLine()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()));

        d.SourceFileShort.Should().Be("OrderService.cs");
        d.Frames[0].Line.Should().Be(d.Line);
        var sourceLine = File.ReadAllLines(SamplesSource("OrderService.cs"))[d.Line!.Value - 1];
        sourceLine.Should().Contain("order.Customer.Address.City.Name");
    }

    internal static string SamplesSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExceptionLens.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "ExceptionLens.Samples", file);
    }
}
