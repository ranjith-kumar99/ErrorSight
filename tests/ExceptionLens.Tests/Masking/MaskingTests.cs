using ExceptionLens.Core;
using ExceptionLens.Masking;
using ExceptionLens.Runtime;
using ExceptionLens.Tests.Weaving;
using SampleApp;

namespace ExceptionLens.Tests.Masking;

[Collection(RuntimeCollection.Name)]
public sealed class MaskingTests
{
    private readonly OrderService _service = new();

    private static CapturedValue Root(ExceptionDiagnostics d, string name) =>
        d.Frames[0].Values.Single(v => v.Name == name);

    // ── Default: sensitive names ─────────────────────────────────────────────

    [Fact]
    public void Default_SensitiveParameter_IsMasked()
    {
        var d = Diagnose.Run(() => _service.Login("jane", "hunter2"));

        d.Text("password").Should().Be("***");
        Root(d, "password").IsMasked.Should().BeTrue();
        d.Text("userName").Should().Be("\"jane\"");
    }

    [Fact]
    public void Default_SensitiveMembers_AreMasked_OthersVisible()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()));
        var customer = Root(d, "order").Member("Customer")!;

        customer.Member("Name")!.Value.Should().Be("\"Jane Doe\"");
        customer.Member("Email")!.Value.Should().Be("***");
        customer.Member("Password")!.Value.Should().Be("***");
        customer.Member("Notes")!.Value.Should().Be("***", "[Sensitive] members are masked");
        customer.Member("Address")!.IsNull.Should().BeTrue("null-ness is never masked");
    }

    [Fact]
    public void Default_SensitiveTypeAndApiKey_AreMasked()
    {
        var card = new PaymentCard { Number = "4111111111111111", Holder = "Jane" };
        var d = Diagnose.Run(() => _service.Charge(card, "sk_live_12345678", new Customer()));

        d.Text("card").Should().Be("***", "PaymentCard is [Sensitive]");
        Root(d, "card").Members.Should().BeNull("a masked object is not walked");
        d.Text("apiKey").Should().Be("***");
        d.Text("amount").Should().Be("12.5");
        d.NullExpression.Should().Be("customer.Address");
    }

    [Fact]
    public void Default_SerializedDiagnostics_ContainNoSecrets()
    {
        var d = Diagnose.Run(() => _service.Login("jane", "hunter2"));
        var json = new ExceptionLens.Formatting.JsonExceptionFormatter().Format(d);
        var text = new ExceptionLens.Formatting.TextExceptionFormatter().Format(d);

        json.Should().NotContain("hunter2");
        text.Should().NotContain("hunter2");
    }

    // ── Modes ────────────────────────────────────────────────────────────────

    [Fact]
    public void ModeAll_OnlyTypesAndNullness_AndDiagnosisStillWorks()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()), o => o.Masking.Mode = MaskingMode.All);

        d.NullExpression.Should().Be("order.Customer.Address");
        d.Values["order.Customer.Address"].Should().BeNull();
        var customer = Root(d, "order").Member("Customer")!;
        customer.Member("Name")!.Value.Should().Be("***");
        customer.Member("Id")!.Value.Should().Be("***");
        Root(d, "order").Member("Id")!.Value.Should().Be("***");

        var json = new ExceptionLens.Formatting.JsonExceptionFormatter().Format(d);
        json.Should().NotContain("Jane Doe").And.NotContain("1837");
    }

    [Fact]
    public void ModeNone_ShowsEverything()
    {
        var d = Diagnose.Run(() => _service.Login("jane", "hunter2"), o => o.Masking.Mode = MaskingMode.None);
        d.Text("password").Should().Be("\"hunter2\"");
    }

    [Fact]
    public void ModeAll_MasksKeyNotFoundKeyInMessage()
    {
        var d = Diagnose.Run(() => _service.PriceOf("secret-sku"), o => o.Masking.Mode = MaskingMode.All);

        d.MissingKey.Should().Be("***");
        d.Message.Should().NotContain("SECRET-SKU");
        d.PossibleCause.Should().NotContain("SECRET-SKU");
        d.Text("key").Should().Be("***");
    }

    // ── Styles and customisation ─────────────────────────────────────────────

    [Fact]
    public void HashStyle_IsDeterministic()
    {
        var first = Diagnose.Run(() => _service.Login("jane", "hunter2"), o => o.Masking.Style = MaskStyle.Hash);
        var second = Diagnose.Run(() => _service.Login("jane", "hunter2"), o => o.Masking.Style = MaskStyle.Hash);

        first.Text("password").Should().MatchRegex("^sha256:[0-9a-f]{8}$");
        first.Text("password").Should().Be(second.Text("password"));
    }

    [Fact]
    public void PartialStyle_KeepsLastFourCharacters()
    {
        var d = Diagnose.Run(() => _service.Charge(new PaymentCard(), "sk_live_12345678", new Customer()),
            o => o.Masking.Style = MaskStyle.Partial);
        d.Text("apiKey").Should().Be("***5678");
    }

    [Fact]
    public void CustomSensitiveName_IsMasked()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutCity()), o => o.Masking.SensitiveNames.Add("street"));
        Root(d, "order").Member("Customer")!.Member("Address")!.Member("Street")!.Value.Should().Be("***");
    }

    [Fact]
    public void ShouldMaskPredicate_IsApplied()
    {
        var d = Diagnose.Run(() => _service.GetCity(Build.OrderWithoutAddress()),
            o => o.Masking.ShouldMask = ctx => ctx.Path == "order.Id");

        Root(d, "order").Member("Id")!.Value.Should().Be("***");
        Root(d, "order").Member("Customer")!.Member("Id")!.Value.Should().Be("42");
    }

    [Fact]
    public void CustomRedactor_IsUsed()
    {
        var d = Diagnose.Run(() => _service.Login("jane", "hunter2"), o => o.Masking.Redactor = v => $"<{v.Length} chars>");
        d.Text("password").Should().Be("<7 chars>");
    }

    // ── Name matching ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("password", true)]
    [InlineData("userPassword", true)]
    [InlineData("PasswordHash", true)]
    [InlineData("apiKey", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("api_key", true)]
    [InlineData("accessTokens", true)]
    [InlineData("OAuthToken", true)]
    [InlineData("EmailAddress", true)]
    [InlineData("cardNumber", true)]
    [InlineData("PinCode", true)]
    [InlineData("[\"password\"]", true)]
    [InlineData("shipping", false)]
    [InlineData("Author", false)]
    [InlineData("Address", false)]
    [InlineData("tokenizer", false)]
    [InlineData("customerName", false)]
    public void NameMatching_IsWordAware(string name, bool sensitive)
    {
        new ValueMasker(new MaskingOptions()).IsSensitiveName(name).Should().Be(sensitive);
    }
}
