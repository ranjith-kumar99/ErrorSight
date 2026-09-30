using ErrorSight.Runtime.Metadata;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ErrorSight.Weaver;

/// <summary>Reads an assembly (+ PDB), weaves it and writes it back in place.</summary>
internal static class AssemblyWeaver
{
    private const string RuntimeAssemblyName = "ErrorSight";
    private const string HooksTypeName = "ErrorSight.Runtime.WeavingHooks";
    private const string WovenAttributeName = "ErrorSight.Runtime.ErrorSightWovenAttribute";

    public static string Weave(string assemblyPath, IReadOnlyList<string> references, WeaverOptions options)
    {
        if (!File.Exists(assemblyPath))
            throw new WeaverSkipException($"Assembly '{assemblyPath}' does not exist.");

        var originalBytes = File.ReadAllBytes(assemblyPath);
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var originalPdb = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

        using var resolver = new ReferenceResolver(references);
        using var module = ReadModule(originalBytes, originalPdb, resolver);

        if (string.Equals(module.Assembly.Name.Name, RuntimeAssemblyName, StringComparison.OrdinalIgnoreCase))
            return $"skipped {module.Assembly.Name.Name} (the ErrorSight runtime itself)";

        if (module.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == WovenAttributeName))
            return $"{module.Assembly.Name.Name} is already woven";

        if (CecilHelpers.HasIgnoreAttribute(module.Assembly.CustomAttributes) ||
            CecilHelpers.HasIgnoreAttribute(module.CustomAttributes))
            return $"skipped {module.Assembly.Name.Name} ([assembly: ErrorSightIgnore])";

        if ((module.Attributes & ModuleAttributes.StrongNameSigned) != 0 || module.Assembly.Name.HasPublicKey)
            throw new WeaverSkipException(
                $"'{module.Assembly.Name.Name}' is strong-name signed; signed assemblies are not woven yet.");

        var runtimePath = resolver.FindPath(RuntimeAssemblyName)
            ?? throw new WeaverSkipException(
                $"'{module.Assembly.Name.Name}' does not reference ErrorSight.dll; nothing to weave.");

        using var runtime = AssemblyDefinition.ReadAssembly(runtimePath);
        var hooks = runtime.MainModule.GetType(HooksTypeName)
            ?? throw new WeaverSkipException($"{HooksTypeName} not found in '{runtimePath}'.");
        var onFilter = hooks.Methods.Single(m => m.Name == "OnFilter");
        var wovenAttribute = runtime.MainModule.GetType(WovenAttributeName)
            ?? throw new WeaverSkipException($"{WovenAttributeName} not found in '{runtimePath}'.");

        var weaver = new ModuleWeaver(module, options, module.ImportReference(onFilter));
        var metadata = weaver.Execute();

        if (metadata.Count == 0)
            return $"{module.Assembly.Name.Name}: no methods to instrument";

        module.Assembly.CustomAttributes.Add(new CustomAttribute(
            module.ImportReference(wovenAttribute.Methods.Single(m => m.IsConstructor && !m.IsStatic && !m.HasParameters))));

        using (var resource = new MemoryStream())
        {
            WeaveMetadataSerializer.Write(resource, metadata);
            module.Resources.Add(new EmbeddedResource(
                WeaveMetadataSerializer.ResourceName, ManifestResourceAttributes.Public, resource.ToArray()));
        }

        var writerParameters = new WriterParameters { DeterministicMvid = true };
        if (module.HasSymbols)
        {
            writerParameters.WriteSymbols = true;
            writerParameters.SymbolWriterProvider = module.SymbolReader.GetWriterProvider();
        }

        try
        {
            module.Write(assemblyPath, writerParameters);
        }
        catch
        {
            // Never leave a half-written assembly behind.
            File.WriteAllBytes(assemblyPath, originalBytes);
            if (originalPdb is not null) File.WriteAllBytes(pdbPath, originalPdb);
            throw;
        }

        return $"instrumented {metadata.Count} method(s) in {module.Assembly.Name.Name}";
    }

    private static ModuleDefinition ReadModule(byte[] assembly, byte[]? pdb, ReferenceResolver resolver)
    {
        var parameters = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = true,
            ReadingMode = ReadingMode.Immediate,
        };

        if (pdb is not null)
        {
            parameters.SymbolReaderProvider = new PortablePdbReaderProvider();
            parameters.SymbolStream = new MemoryStream(pdb);
        }
        else
        {
            // Embedded PDB, or none at all.
            parameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false);
        }

        try
        {
            return ModuleDefinition.ReadModule(new MemoryStream(assembly), parameters);
        }
        catch (Exception) when (parameters.ReadSymbols)
        {
            // Unreadable symbols (e.g. a Windows PDB): weave without local names / line mapping.
            parameters.ReadSymbols = false;
            parameters.SymbolReaderProvider = null;
            parameters.SymbolStream = null;
            return ModuleDefinition.ReadModule(new MemoryStream(assembly), parameters);
        }
    }
}
