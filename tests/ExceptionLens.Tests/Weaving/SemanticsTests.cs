using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExceptionLens.Runtime;
using SampleApp;

namespace ExceptionLens.Tests.Weaving;

/// <summary>Instrumentation must never change what the application does.</summary>
[Collection(RuntimeCollection.Name)]
public sealed class SemanticsTests
{
    private static readonly Assembly Samples = typeof(OrderService).Assembly;

    public SemanticsTests() => Diagnose.Options();

    [Fact]
    public void SamplesAssembly_IsWoven()
    {
        Samples.GetCustomAttribute<ExceptionLensWovenAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void EveryInstrumentedMethod_Compiles()
    {
        // JIT-compile every non-generic method so a malformed IL body (InvalidProgramException) cannot hide.
        foreach (var type in Samples.GetTypes().Where(t => !t.ContainsGenericParameters))
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;
                var prepare = () => RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepare.Should().NotThrow($"{type.Name}.{method.Name} must JIT");
            }
        }
    }

    [Fact]
    public void HappyPaths_ReturnTheSameResults()
    {
        var service = new OrderService();
        service.GetCity(Build.CompleteOrder()).Should().Be("OSLO");
        service.ProcessOrder(Build.CompleteOrder()).Should().Be("order-1:OSLO");
        service.ViaLambda(Build.CompleteOrder()).Should().Be("city:Oslo");
        service.ViaLocalFunction(Build.CompleteOrder()).Should().Be("Oslo!!");
        service.CityNames(new[] { Build.CompleteOrder().Customer }).Should().Equal("Oslo");
        OrderService.Upper(" a ").Should().Be("A");
        new Invoice(Build.CompleteOrder(), 2).Total.Should().Be(7 + 2 * 2);
        new Point { X = 1, Label = "abc" }.LabelLength().Should().Be(5);
    }

    [Fact]
    public async Task AsyncHappyPath_ReturnsTheSameResult()
    {
        var service = new OrderService();
        (await service.GetCityAsync(Build.CompleteOrder())).Should().Be("OkOslo");
        (await service.ProcessOrderAsync(Build.CompleteOrder())).Should().Be("OkOslo3");

        var names = new List<string>();
        await foreach (var name in service.CityNamesAsync(new[] { Build.CompleteOrder().Customer })) names.Add(name);
        names.Should().Equal("Oslo");
    }

    [Theory]
    [InlineData(-5, "negative")]
    [InlineData(0, "zero")]
    [InlineData(5, "positive")]
    [InlineData(5000, "large")]
    public void MultipleReturns_ArePreserved(int input, string expected)
    {
        new OrderService().Classify(input).Should().Be(expected);
    }

    [Fact]
    public void TryFinally_IsPreserved()
    {
        var service = new OrderService();
        service.TryFinally(5).Should().Be(10);
        service.TryFinally(-1).Should().Be(-1);
        var act = () => service.TryFinally(0);
        act.Should().Throw<InvalidOperationException>().WithMessage("zero");
        service.Counter.Should().Be(3);
    }

    [Fact]
    public void RefAndOutParameters_AreWrittenBack()
    {
        var counter = 10;
        var act = () => new OrderService().Parse("abc".AsSpan(), ref counter, out _);
        act.Should().Throw<NullReferenceException>();
        counter.Should().Be(11);
    }

    [Fact]
    public void UserCatchAndWhenFilters_StillWork()
    {
        var service = new OrderService();
        service.CatchesOwn(Build.OrderWithoutAddress()).Should().Be("handled");

        var unhandled = () => service.CatchesOwn(new Order { Id = 0, Customer = new Customer() });
        unhandled.Should().Throw<NullReferenceException>();
    }

    [Fact]
    public void Exception_PropagatesUnchanged()
    {
        var thrown = Record.Exception(() => new OrderService().GetCity(Build.OrderWithoutAddress()));

        thrown.Should().BeOfType<NullReferenceException>();
        var top = new StackTrace(thrown!, fNeedFileInfo: true).GetFrame(0)!;
        top.GetMethod()!.Name.Should().Be(nameof(OrderService.GetCity));
        Path.GetFileName(top.GetFileName()).Should().Be("OrderService.cs");
        thrown!.StackTrace.Should().NotContain("WeavingHooks");
    }

    [Fact]
    public void CapturedValues_AreTheStateAtThrowTime_BeforeFinallyRuns()
    {
        var thrown = Record.Exception(() => new OrderService().StateBeforeFinally(Build.OrderWithoutAddress()))!;

        ExceptionLensRuntime.TryGetCapturedFrames(thrown, out var frames).Should().BeTrue();
        var stage = frames![0].Values.Single(v => v.Name == "stage");
        stage.Value.Should().Be("\"reading\"", "the finally block sets \"finished\" only after the filter ran");
    }

    [Fact]
    public void HandledExceptions_DoNotAffectTheCaller()
    {
        // Exceptions caught by the application are captured but otherwise invisible.
        var service = new OrderService();
        for (var i = 0; i < 50; i++) service.CatchesOwn(Build.OrderWithoutAddress()).Should().Be("handled");
    }

    [Fact]
    public void Weaving_IsIdempotent()
    {
        var weaver = Path.Combine(RepositoryRoot(), "src", "ExceptionLens.Weaver", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net8.0", "ExceptionLens.Weaver.dll");

        var temp = Directory.CreateTempSubdirectory("exceptionlens-");
        try
        {
            var copy = Path.Combine(temp.FullName, "ExceptionLens.Samples.dll");
            File.Copy(Samples.Location, copy);
            File.Copy(Path.ChangeExtension(Samples.Location, ".pdb"), Path.ChangeExtension(copy, ".pdb"));
            var references = Path.Combine(temp.FullName, "refs.rsp");
            File.WriteAllLines(references, new[] { typeof(ExceptionLensRuntime).Assembly.Location });
            var before = File.ReadAllBytes(copy);

            var psi = new ProcessStartInfo("dotnet", $"\"{weaver}\" --assembly \"{copy}\" --references \"{references}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            output.Should().Contain("already woven");
            File.ReadAllBytes(copy).Should().Equal(before);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExceptionLens.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
