namespace ExceptionLens.Weaver;

/// <summary>
/// Command-line entry point invoked by build/ExceptionLens.targets after CoreCompile:
///
///   dotnet ExceptionLens.Weaver.dll --assembly obj/App.dll --references obj/App.exceptionlens.rsp [--min-il-size 16]
///
/// Weaving never fails the build: problems are reported as MSBuild warnings and the
/// assembly is left untouched (the app runs normally, just without value capture).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? assembly = null;
        string? referencesFile = null;
        var options = new WeaverOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly" when i + 1 < args.Length:
                    assembly = args[++i];
                    break;
                case "--references" when i + 1 < args.Length:
                    referencesFile = args[++i];
                    break;
                case "--min-il-size" when i + 1 < args.Length:
                    options.MinILSize = int.TryParse(args[++i], out var size) ? size : options.MinILSize;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
            }
        }

        if (assembly is null)
        {
            Console.Error.WriteLine("usage: ExceptionLens.Weaver --assembly <path> [--references <rsp>] [--min-il-size <n>]");
            return 2;
        }

        var references = referencesFile is not null && File.Exists(referencesFile)
            ? File.ReadAllLines(referencesFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();

        try
        {
            var result = AssemblyWeaver.Weave(assembly, references, options);
            Console.WriteLine($"ExceptionLens: {result}");
        }
        catch (WeaverSkipException skip)
        {
            Console.WriteLine($"ExceptionLens.Weaver : warning EL0001 : {skip.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ExceptionLens.Weaver : warning EL0002 : Weaving '{Path.GetFileName(assembly)}' failed; " +
                              $"runtime value capture is disabled for it. {ex.GetType().Name}: {ex.Message}");
            if (options.Verbose) Console.WriteLine(ex);
        }

        return 0;
    }
}

internal sealed class WeaverOptions
{
    /// <summary>
    /// Methods whose IL body is this size or smaller are not instrumented, so tiny hot
    /// methods keep being inlined by the JIT (methods with exception handlers are not inlined on .NET 8).
    /// </summary>
    public int MinILSize { get; set; } = 16;

    public bool Verbose { get; set; }
}

/// <summary>An expected reason not to weave an assembly, reported as a warning.</summary>
internal sealed class WeaverSkipException(string message) : Exception(message);
