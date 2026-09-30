// Shared between ErrorSight (runtime reader) and ErrorSight.Weaver (writer).
// Compiled into both assemblies via <Compile Include="..\Shared\*.cs" />.

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ErrorSight.Runtime.Metadata;

/// <summary>What a captured slot represents in the source method.</summary>
internal enum SlotKind : byte
{
    This = 0,
    Argument = 1,
    Local = 2,
    /// <summary>A field of a compiler-generated state machine (hoisted local / parameter).</summary>
    Field = 3,
}

[System.Flags]
internal enum SlotFlags : byte
{
    None = 0,
    /// <summary>Declared with a sensitive-data attribute ([Sensitive], [PersonalData], ...).</summary>
    Sensitive = 1,
    /// <summary>The value is a compiler-generated closure; its fields are the real variables.</summary>
    Closure = 2,
}

/// <summary>A name a local slot carries over an IL range (slots are reused across scopes in Release builds).</summary>
internal sealed class WovenLocalScope
{
    public string Name = string.Empty;
    public int Start;
    public int End;
}

internal sealed class WovenSlot
{
    public string Name = string.Empty;
    public SlotKind Kind;
    public SlotFlags Flags;
    /// <summary>Only for locals: every name the slot has, with its IL scope. Null/empty = always in scope.</summary>
    public List<WovenLocalScope>? Scopes;
}

internal sealed class WovenSequencePoint
{
    public int Offset;
    /// <summary>Source line, or -1 for a hidden sequence point.</summary>
    public int Line;
}

internal enum AccessKind : byte
{
    /// <summary>Member access / instance call: <c>receiver.Member</c>.</summary>
    Member = 0,
    /// <summary>Array element or indexer: <c>receiver[index]</c>.</summary>
    Element = 1,
}

/// <summary>
/// An IL instruction that dereferences a receiver and would throw a
/// NullReferenceException if that receiver were null.
/// </summary>
internal sealed class WovenAccess
{
    public int Offset;
    public AccessKind Kind;
    /// <summary>Source-like expression for the receiver, e.g. <c>order.Customer.Address</c>.</summary>
    public string Receiver = string.Empty;
    /// <summary>Source-like expression for the result, e.g. <c>order.Customer.Address.City</c>.</summary>
    public string Result = string.Empty;
    /// <summary>For element access: the index expression, e.g. <c>i</c>.</summary>
    public string? Index;
}

internal sealed class WovenMethod
{
    public int Id;
    public string DisplayName = string.Empty;
    public List<WovenSlot> Slots = new();
    public List<WovenSequencePoint> SequencePoints = new();
    public List<WovenAccess> Accesses = new();
    /// <summary>
    /// IL offsets where the evaluation stack is empty. Optimized JIT code reports exception IL offsets only at
    /// these boundaries, so the real faulting instruction lies between the reported offset and the next one.
    /// </summary>
    public List<int> StackEmptyOffsets = new();
}

internal static class WeaveMetadataSerializer
{
    public const string ResourceName = "ErrorSight.Metadata.bin";
    private const int Magic = 0x444D5345; // "ESMD"
    private const int Version = 2;

    public static void Write(Stream stream, IReadOnlyCollection<WovenMethod> methods)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(methods.Count);
        foreach (var m in methods)
        {
            w.Write(m.Id);
            w.Write(m.DisplayName);

            w.Write(m.Slots.Count);
            foreach (var s in m.Slots)
            {
                w.Write(s.Name);
                w.Write((byte)s.Kind);
                w.Write((byte)s.Flags);
                var scopes = s.Scopes ?? new List<WovenLocalScope>();
                w.Write(scopes.Count);
                foreach (var sc in scopes)
                {
                    w.Write(sc.Name);
                    w.Write(sc.Start);
                    w.Write(sc.End);
                }
            }

            w.Write(m.SequencePoints.Count);
            foreach (var sp in m.SequencePoints)
            {
                w.Write(sp.Offset);
                w.Write(sp.Line);
            }

            w.Write(m.Accesses.Count);
            foreach (var a in m.Accesses)
            {
                w.Write(a.Offset);
                w.Write((byte)a.Kind);
                w.Write(a.Receiver);
                w.Write(a.Result);
                w.Write(a.Index ?? string.Empty);
            }

            w.Write(m.StackEmptyOffsets.Count);
            foreach (var offset in m.StackEmptyOffsets) w.Write(offset);
        }
    }

    public static Dictionary<int, WovenMethod> Read(Stream stream)
    {
        var result = new Dictionary<int, WovenMethod>();
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != Magic) return result;
        if (r.ReadInt32() != Version) return result;

        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var m = new WovenMethod { Id = r.ReadInt32(), DisplayName = r.ReadString() };

            var slotCount = r.ReadInt32();
            for (var j = 0; j < slotCount; j++)
            {
                var s = new WovenSlot
                {
                    Name = r.ReadString(),
                    Kind = (SlotKind)r.ReadByte(),
                    Flags = (SlotFlags)r.ReadByte(),
                };
                var scopeCount = r.ReadInt32();
                if (scopeCount > 0)
                {
                    s.Scopes = new List<WovenLocalScope>(scopeCount);
                    for (var k = 0; k < scopeCount; k++)
                        s.Scopes.Add(new WovenLocalScope { Name = r.ReadString(), Start = r.ReadInt32(), End = r.ReadInt32() });
                }
                m.Slots.Add(s);
            }

            var spCount = r.ReadInt32();
            for (var j = 0; j < spCount; j++)
                m.SequencePoints.Add(new WovenSequencePoint { Offset = r.ReadInt32(), Line = r.ReadInt32() });

            var accessCount = r.ReadInt32();
            for (var j = 0; j < accessCount; j++)
            {
                var a = new WovenAccess
                {
                    Offset = r.ReadInt32(),
                    Kind = (AccessKind)r.ReadByte(),
                    Receiver = r.ReadString(),
                    Result = r.ReadString(),
                };
                var index = r.ReadString();
                a.Index = index.Length == 0 ? null : index;
                m.Accesses.Add(a);
            }

            var emptyCount = r.ReadInt32();
            for (var j = 0; j < emptyCount; j++) m.StackEmptyOffsets.Add(r.ReadInt32());

            result[m.Id] = m;
        }

        return result;
    }
}
