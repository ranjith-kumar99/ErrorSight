using Mono.Cecil;

namespace ErrorSight.Weaver;

/// <summary>
/// Resolves assemblies from the exact reference paths MSBuild compiled against,
/// falling back to Cecil's default probing.
/// </summary>
internal sealed class ReferenceResolver : IAssemblyResolver
{
    private readonly Dictionary<string, string> _pathsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyDefinition?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DefaultAssemblyResolver _fallback = new();

    public ReferenceResolver(IEnumerable<string> referencePaths)
    {
        foreach (var path in referencePaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            _pathsByName.TryAdd(name, path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) _fallback.AddSearchDirectory(dir);
        }
    }

    public string? FindPath(string assemblyName) =>
        _pathsByName.TryGetValue(assemblyName, out var path) ? path : null;

    public AssemblyDefinition? Resolve(AssemblyNameReference name) =>
        Resolve(name, new ReaderParameters { AssemblyResolver = this });

    public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_cache.TryGetValue(name.Name, out var cached)) return cached;

        AssemblyDefinition? result = null;
        try
        {
            if (_pathsByName.TryGetValue(name.Name, out var path) && File.Exists(path))
            {
                parameters.AssemblyResolver ??= this;
                result = AssemblyDefinition.ReadAssembly(path, parameters);
            }
            else
            {
                result = _fallback.Resolve(name, parameters);
            }
        }
        catch
        {
            // Unresolvable references are tolerated; callers treat them conservatively.
        }

        _cache[name.Name] = result;
        return result;
    }

    public void Dispose()
    {
        foreach (var assembly in _cache.Values) assembly?.Dispose();
        _cache.Clear();
        _fallback.Dispose();
    }
}
