namespace EmbeddedHelper.Core;

// Both DWARF readers feed the same bounded expansion logic. No memory is read here.
internal sealed class ElfTypeNode(string tag)
{
    public string Tag { get; } = tag.Replace("_", "").ToLowerInvariant().Replace("dwtag", "");
    public string Name { get; set; } = "";
    public ulong? Size { get; set; }
    public ulong? Encoding { get; set; }
    public ulong? Offset { get; set; }
    public ulong? Count { get; set; }
    public ulong? Upper { get; set; }
    public ulong? Lower { get; set; }
    public bool BitField { get; set; }
    public bool CustomStride { get; set; }
    public ElfTypeNode? Type { get; set; }
    public List<ElfTypeNode> Children { get; } = [];
}

internal static class ElfTypeExpansion
{
    private const int Limit = 4096;
    public static Variable[] Expand(string identity, string name, ulong address, ElfTypeNode? type)
    {
        var result = new List<Variable>();
        try { Walk(identity, name, address, type, result, new HashSet<ElfTypeNode>(), 0); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        { return [new(identity, name, address, "不支持", 0, "无法展开：" + ex.Message)]; }
        return result.ToArray();
    }
    private static ElfTypeNode? Unwrap(ElfTypeNode? node)
    {
        var seen = new HashSet<ElfTypeNode>();
        while (node != null && node.Tag is "typedef" or "consttype" or "volatiletype" or "restricttype")
        {
            if (!seen.Add(node) || seen.Count > 32) throw new InvalidDataException("类型引用循环或过深");
            node = node.Type;
        }
        return node;
    }
    private static ulong[] Dimensions(ElfTypeNode node)
    {
        if (node.CustomStride) throw new InvalidDataException("不支持自定义数组步长");
        var dimensions = node.Children.Where(c => c.Tag == "subrangetype").Select(c =>
        {
            if (c.Lower is > 0) throw new InvalidDataException("不支持非零数组下界");
            var count = c.Count ?? (c.Upper.HasValue ? checked(c.Upper.Value + 1) : 0);
            if (count == 0 || count > Limit) throw new InvalidDataException("数组长度未知或超过4096");
            return count;
        }).ToArray();
        if (dimensions.Length == 0 || dimensions.Length > 8) throw new InvalidDataException("数组维度未知或过多");
        return dimensions;
    }
    private static ulong SizeOf(ElfTypeNode? raw, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("类型嵌套过深");
        var node = Unwrap(raw) ?? throw new InvalidDataException("缺少类型");
        if (node.Size is > 0) return node.Size.Value;
        if (node.Tag != "arraytype") throw new InvalidDataException("类型大小未知");
        return Dimensions(node).Aggregate(SizeOf(node.Type, depth + 1), (size, count) => checked(size * count));
    }
    private static void Walk(string id, string name, ulong address, ElfTypeNode? raw, List<Variable> output, HashSet<ElfTypeNode> path, int depth)
    {
        if (output.Count >= Limit || depth > 32) throw new InvalidDataException("展开超过4096项或32层");
        var node = Unwrap(raw);
        if (node == null) { output.Add(new(id, name, address, "不支持", 0, "类型信息缺失")); return; }
        if (!path.Add(node)) throw new InvalidDataException("类型引用循环");
        try
        {
            if (node.Tag == "structuretype")
            {
                var size = SizeOf(node);
                var members = node.Children.Where(c => c.Tag == "member").ToArray();
                if (members.Length == 0) { output.Add(new(id, name, address, "不支持", 0, "结构体没有可展开成员")); return; }
                foreach (var member in members)
                {
                    if (output.Count >= Limit) throw new InvalidDataException("展开超过4096项");
                    var suffix = "." + (member.Name.Length > 0 ? member.Name : "<匿名成员>");
                    if (member.BitField || member.Offset == null || member.Name.Length == 0)
                    { output.Add(new(id + suffix, name + suffix, address, "不支持", 0, "位域、匿名成员或成员偏移不支持")); continue; }
                    var offset = member.Offset.Value;
                    var memberSize = SizeOf(member.Type);
                    if (offset >= size || memberSize > size - offset) throw new InvalidDataException("成员超出结构体边界");
                    Walk(id + suffix, name + suffix, checked(address + offset), member.Type, output, path, depth + 1);
                }
            }
            else if (node.Tag == "arraytype")
            {
                var dimensions = Dimensions(node);
                var count = dimensions.Aggregate(1UL, (total, n) => checked(total * n));
                if (count > Limit) throw new InvalidDataException("数组元素超过4096");
                var stride = SizeOf(node.Type);
                if (node.Size.HasValue && node.Size != checked(stride * count)) throw new InvalidDataException("数组大小与维度不一致");
                for (ulong i = 0; i < count; i++)
                {
                    var index = i; var suffix = "";
                    for (int d = dimensions.Length - 1; d >= 0; d--) { suffix = $"[{index % dimensions[d]}]" + suffix; index /= dimensions[d]; }
                    Walk(id + suffix, name + suffix, checked(address + i * stride), node.Type, output, path, depth + 1);
                }
            }
            else
            {
                var size = checked((int)(node.Size ?? 0));
                var scalar = node.Tag == "basetype" ? (node.Encoding, size) switch
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
                if (scalar != "不支持" && (address > uint.MaxValue || (ulong)size - 1 > uint.MaxValue - address)) throw new InvalidDataException("地址超出ELF32范围");
                output.Add(new(id, name, address, scalar, size, scalar == "不支持" ? "类型尚不支持（不跟随指针或展开联合体）" : "可读取"));
            }
        }
        finally { path.Remove(node); }
    }
}
