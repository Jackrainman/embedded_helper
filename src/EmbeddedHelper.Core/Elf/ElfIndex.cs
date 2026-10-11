using System.Buffers.Binary;
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
        try { return ReadNative(path); }
        catch (NotSupportedException ex)
        {
            var tool = string.IsNullOrWhiteSpace(readElfTool) ? ReadElfIndex.FindTool() : readElfTool;
            if (tool == null || !File.Exists(tool)) throw new InvalidDataException("该 ELF 的 DWARF 格式需要 ARM readelf。请在设置中选择 arm-none-eabi-readelf.exe，或构建为 -gdwarf-4。原始原因：" + ex.Message, ex);
            return ReadElfIndex.Read(tool, path);
        }
    }

    private static Variable[] ReadNative(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 256 * 1024 * 1024) throw new InvalidDataException("ELF 超过 256 MiB 解析上限。");
        var header = new byte[6];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 }) || header[4] != 1 || header[5] != 1)
            throw new InvalidDataException("当前支持 ELF32 小端 ARM 文件。");
        stream.Position = 0;
        var elf = ElfFile.Read(stream);
        var dwarf = DwarfFile.ReadFromElf(elf);
        var result = new List<Variable>();
        foreach (var unit in dwarf.InfoSection.Units)
        {
            if (unit.Root is not { } root) continue;
            var cu = Attribute(root, DwarfAttributeKind.Name)?.ValueAsObject as string ?? "";
            Walk(root, cu, "", result);
        }
        if (result.Count == 0) throw new InvalidDataException("没有找到固定地址变量。请启用 DWARF4 调试信息（-g -gdwarf-4）。");
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
                var visited = new HashSet<DwarfDIE>();
                while (type != null && visited.Add(type) && (type.Tag == DwarfTag.Typedef || type.Tag == DwarfTag.ConstType || type.Tag == DwarfTag.VolatileType || type.Tag == DwarfTag.RestrictType))
                    type = Attribute(type, DwarfAttributeKind.Type)?.ValueAsObject as DwarfDIE;
                var size = checked((int)(type == null ? 0 : Attribute(type, DwarfAttributeKind.ByteSize)?.ValueAsU64 ?? 0));
                var encoding = type == null ? 0 : Attribute(type, DwarfAttributeKind.Encoding)?.ValueAsU64 ?? 0;
                var typeName = type?.Tag == DwarfTag.BaseType ? (encoding, size) switch
                {
                    (5 or 6, 1) => "int8",
                    (7 or 8, 1) => "uint8",
                    (5, 2) => "int16",
                    (7, 2) => "uint16",
                    (5, 4) => "int32",
                    (7, 4) => "uint32",
                    (4, 4) => "float32",
                    _ => "不支持"
                } : "不支持";
                var address = location.Operations[0].Operand1.U64;
                var external = Attribute(die, DwarfAttributeKind.External)?.ValueAsU64 == 1;
                result.Add(new Variable(external ? "global:" + name : cu.Replace('\\', '/') + ":" + scope + ":" + name,
                    name, address, typeName, size, typeName == "不支持" ? "类型尚不支持" : "可读取"));
            }
        }
        var childScope = die.Tag == DwarfTag.Subprogram ? scope + "/" + name : scope;
        foreach (var child in die.Children) Walk(child, cu, childScope, result);
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
