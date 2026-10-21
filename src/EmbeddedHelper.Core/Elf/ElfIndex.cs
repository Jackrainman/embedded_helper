using System.Buffers.Binary;
using LibObjectFile;
using LibObjectFile.Dwarf;
using LibObjectFile.Elf;

namespace EmbeddedHelper.Core;

public sealed record Variable(string Identity, string Name, ulong Address, string Type, int Size, string Status)
{
    public bool Supported => Status == "可读取";
    public string AddressText => $"0x{Address:X8}";
}

public static class ElfIndex
{
    public static Variable[] Read(string path, string? readElfTool = null)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 256 * 1024 * 1024) throw new InvalidDataException("ELF 超过 256 MiB 解析上限。");
        if (stream.Length < 52) throw new InvalidDataException("ELF/AXF 文件头不完整。");
        var header = new byte[20];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 }) || header[4] != 1 || header[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) != 40)
            throw new InvalidDataException("当前支持 ELF32 小端 ARM 文件。");
        stream.Position = 0;
        var elf = ElfFile.Read(stream);
        var debug = elf.Sections.OfType<ElfStreamSection>().FirstOrDefault(s => s.Name.ToString() == ".debug_info")?.Stream;
        if (debug == null || debug.Length == 0) throw new InvalidDataException("ELF 缺少可用调试信息。");
        DwarfFile dwarf;
        var nativeIdentities = true;
        var originalPosition = debug.Position;
        try
        {
            debug.Position = 0;
            using var reader = new BinaryReader(debug, System.Text.Encoding.UTF8, leaveOpen: true);
            while (debug.Position < debug.Length)
            {
                if (debug.Length - debug.Position < 4) throw new InvalidDataException("ELF 调试信息长度无效。");
                ulong length = reader.ReadUInt32();
                if (length == uint.MaxValue)
                {
                    if (debug.Length - debug.Position < 8) throw new InvalidDataException("ELF 调试信息长度无效。");
                    length = reader.ReadUInt64();
                }
                if (length < 2 || length > (ulong)(debug.Length - debug.Position)) throw new InvalidDataException("ELF 调试信息长度无效。");
                var end = debug.Position + (long)length;
                if (reader.ReadUInt16() >= 5)
                {
                    nativeIdentities = false;
                    throw new NotSupportedException("DWARF5 需要 ARM readelf。");
                }
                debug.Position = end;
            }
            debug.Position = originalPosition;
            dwarf = DwarfFile.ReadFromElf(elf);
        }
        catch (Exception ex) when (ex is NotSupportedException or EndOfStreamException or ObjectFileException)
        {
            // Only DWARF decoding failures may use another reader; file/container errors stay outside this block.
            var tool = string.IsNullOrWhiteSpace(readElfTool) ? ReadElfIndex.FindTool() : readElfTool;
            if (tool == null || !File.Exists(tool))
                throw new InvalidDataException("内置 DWARF 解析失败，需要 ARM readelf。请在设置与诊断中选择 arm-none-eabi-readelf.exe。原始原因：" + ex.Message, ex);
            try { return ReadElfIndex.Read(tool, path, nativeIdentities); }
            catch (Exception fallback) when (fallback is IOException or InvalidDataException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                throw new InvalidDataException("内置解析与 ARM readelf 均失败。内置原因：" + ex.Message + "；readelf 原因：" + fallback.Message,
                    new AggregateException(ex, fallback));
            }
        }
        finally { debug.Position = originalPosition; }
        if (dwarf.InfoSection.Units.Count == 0) throw new InvalidDataException("ELF 缺少可用调试信息。");
        var result = new List<Variable>();
        foreach (var unit in dwarf.InfoSection.Units)
        {
            if (unit.Root is not { } root) continue;
            var cu = Attribute(root, DwarfAttributeKind.Name)?.ValueAsObject as string ?? "";
            Walk(root, cu, "", result);
        }
        return result.OrderBy(v => v.Name, StringComparer.Ordinal).ToArray();
    }

    private static DwarfAttribute? Attribute(DwarfDIE die, DwarfAttributeKind kind) => die.Attributes.FirstOrDefault(a => a.Kind == kind);
    private static void Walk(DwarfDIE die, string cu, string scope, List<Variable> result)
    {
        var name = Attribute(die, DwarfAttributeKind.Name)?.ValueAsObject as string ?? "";
        if (die.Tag == DwarfTag.Variable && name.Length > 0)
        {
            var location = Attribute(die, DwarfAttributeKind.Location)?.ValueAsObject as DwarfExpression;
            if (location?.Operations.Count == 1 && location.Operations[0].Kind == DwarfOperationKind.Addr)
            {
                var type = Attribute(die, DwarfAttributeKind.Type)?.ValueAsObject as DwarfDIE;
                var address = location.Operations[0].Operand1.U64;
                var external = Attribute(die, DwarfAttributeKind.External)?.ValueAsU64 == 1;
                var identity = external ? "global:" + name : cu.Replace('\\', '/') + ":" + scope + ":" + name;
                result.AddRange(ElfTypeExpansion.Expand(identity, name, address, BuildType(type, new())));
            }
        }
        var childScope = die.Tag == DwarfTag.Subprogram ? scope + "/" + name : scope;
        foreach (var child in die.Children) Walk(child, cu, childScope, result);
    }

    private static ulong? Constant(DwarfDIE die, DwarfAttributeKind kind)
    {
        var attribute = Attribute(die, kind);
        if (attribute == null) return null;
        if (attribute.ValueAsObject is DwarfExpression expression)
            return expression.Operations.Count == 1 && expression.Operations[0].Kind == DwarfOperationKind.PlusUconst
                ? expression.Operations[0].Operand1.U64 : null;
        if (attribute.ValueAsObject != null) return null;
        return attribute.ValueAsU64;
    }
    private static ElfTypeNode? BuildType(DwarfDIE? die, Dictionary<DwarfDIE, ElfTypeNode> cache, int depth = 0)
    {
        if (die == null) return null;
        if (cache.TryGetValue(die, out var existing)) return existing;
        if (depth > 64) throw new InvalidDataException("DWARF类型嵌套超过64层。");
        var node = new ElfTypeNode(die.Tag.ToString())
        {
            Name = Attribute(die, DwarfAttributeKind.Name)?.ValueAsObject as string ?? "",
            Size = Constant(die, DwarfAttributeKind.ByteSize),
            Encoding = Constant(die, DwarfAttributeKind.Encoding),
            Offset = Constant(die, DwarfAttributeKind.DataMemberLocation),
            Count = Constant(die, DwarfAttributeKind.Count),
            Upper = Constant(die, DwarfAttributeKind.UpperBound),
            Lower = Constant(die, DwarfAttributeKind.LowerBound),
            BitField = Attribute(die, DwarfAttributeKind.BitSize) != null,
            CustomStride = Attribute(die, DwarfAttributeKind.ByteStride) != null || Attribute(die, DwarfAttributeKind.BitStride) != null
        };
        cache[die] = node;
        // Pointer and union targets are deliberately not followed.
        if (node.Tag is not ("pointertype" or "uniontype"))
        {
            node.Type = BuildType(Attribute(die, DwarfAttributeKind.Type)?.ValueAsObject as DwarfDIE, cache, depth + 1);
            foreach (var child in die.Children)
                if (child.Tag == DwarfTag.Member || child.Tag == DwarfTag.SubrangeType) node.Children.Add(BuildType(child, cache, depth + 1)!);
        }
        return node;
    }

    public static double Decode(Variable variable, ReadOnlySpan<byte> bytes)
    {
        if (!variable.Supported || bytes.Length != variable.Size) throw new InvalidDataException("变量类型不支持或读取长度不完整。");
        return variable.Type switch
        {
            "int8" => (sbyte)bytes[0],
            "uint8" => bytes[0],
            "int16" => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            "int32" => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            "float32" => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            _ => throw new InvalidDataException("变量类型尚不支持。")
        };
    }
}
