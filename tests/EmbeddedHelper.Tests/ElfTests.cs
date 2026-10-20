using System.Text.Json;
using System.Text.Json.Nodes;
using EmbeddedHelper.Core;
using Xunit;

namespace EmbeddedHelper.Tests;

[Collection("Offline client")]
public sealed class ElfTests : TestWorkspace
{
    [Fact]
    public void RealStructFixturesAgreeAcrossNativeAndReadElfReaders()
    {
        var native = ElfIndex.Read(Path.Combine(fixtures, "structures.elf"), "must-not-fallback-to-readelf.exe");
        var adapted = ReadElfIndex.Parse(File.ReadAllText(Path.Combine(fixtures, "structures.dwarf.txt")));
        var dwarf5 = ReadElfIndex.Parse(File.ReadAllText(Path.Combine(fixtures, "structures5.dwarf.txt")));
        foreach (var variables in new[] { native, adapted, dwarf5 })
        {
            var speed = Assert.Single(variables, v => v.Name == "motors[0].feedback.speed");
            Assert.True(speed.Supported, speed.Status); Assert.Equal("float32", speed.Type);
            Assert.Equal("global:motors[0].feedback.speed", speed.Identity);
            Assert.Equal(speed.Address + 28, variables.Single(v => v.Name == "motors[1].feedback.speed").Address);
            Assert.Equal(8UL, speed.Address - variables.Single(v => v.Name == "motors[0].count").Address);
            Assert.Equal(24UL, variables.Single(v => v.Name == "motors[0].gains[2]").Address - variables.Single(v => v.Name == "motors[0].count").Address);
            Assert.Equal(1UL, variables.Single(v => v.Name == "packed_state.count").Address - variables.Single(v => v.Name == "packed_state.tag").Address);
            Assert.Equal(20UL, variables.Single(v => v.Name == "matrix[1][2]").Address - variables.Single(v => v.Name == "matrix[0][0]").Address);
            Assert.Contains(variables, v => v.Name == "chassis.feedback.speed" && v.Supported);
            Assert.Contains(variables, v => v.Name == "local_static.feedback.speed" && v.Supported);
            Assert.DoesNotContain(variables, v => v.Name.StartsWith("stack_local", StringComparison.Ordinal));
            Assert.All(variables.Where(v => v.Name.StartsWith("edge.", StringComparison.Ordinal)), v => Assert.False(v.Supported));
            Assert.Equal(1.25, ElfIndex.Decode(speed, [0, 0, 0xa0, 0x3f]));
        }
        Assert.Equal(native.Where(v => v.Supported).Select(v => (v.Name, v.Address, v.Type, v.Size)), adapted.Where(v => v.Supported).Select(v => (v.Name, v.Address, v.Type, v.Size)));
    }
    [Theory]
    [InlineData("4", "structure_type", "可读取")]
    [InlineData("0x4", "structure_type", "可读取")]
    [InlineData("2 byte block: 23 4 (DW_OP_plus_uconst: 4)", "structure_type", "可读取")]
    [InlineData("5 byte block: 91 4 (DW_OP_fbreg: 4)", "structure_type", "不支持")]
    [InlineData("7", "structure_type", "不支持")]
    [InlineData("4", "union_type", "不支持")]
    public void MemberOffsetsAreValidatedNotGuessed(string offset, string tag, string expected)
    {
        var dump = $$"""
        <0><c>: Abbrev Number: 1 (DW_TAG_compile_unit)
         <d> DW_AT_name : source.c
        <1><20>: Abbrev Number: 1 (DW_TAG_base_type)
         <21> DW_AT_byte_size : 4
         <22> DW_AT_encoding : 4 (float)
        <1><30>: Abbrev Number: 1 (DW_TAG_{{tag}})
         <31> DW_AT_byte_size : 8
        <2><40>: Abbrev Number: 1 (DW_TAG_member)
         <41> DW_AT_name : speed
         <42> DW_AT_type : <0x20>
         <43> DW_AT_data_member_location : {{offset}}
        <1><50>: Abbrev Number: 1 (DW_TAG_variable)
         <51> DW_AT_name : motor
         <52> DW_AT_type : <0x30>
         <53> DW_AT_external : 1
         <54> DW_AT_location : (DW_OP_addr: 20000000)
        """;
        var variable = Assert.Single(ReadElfIndex.Parse(dump));
        Assert.Equal(expected == "可读取", variable.Supported);
        if (variable.Supported)
        {
            Assert.Equal("global:motor.speed", variable.Identity); Assert.Equal(0x20000004UL, variable.Address);
            var moved = Assert.Single(ReadElfIndex.Parse(dump.Replace("20000000", "20000100")));
            Assert.Equal(variable.Identity, moved.Identity); Assert.Equal(variable.Address + 256, moved.Address);
        }
        var cycle = ReadElfIndex.Parse(dump.Replace("<42> DW_AT_type : <0x20>", "<42> DW_AT_type : <0x30>").Replace($"DW_AT_data_member_location : {offset}", "DW_AT_data_member_location : 0"));
        Assert.DoesNotContain(cycle, v => v.Supported);
    }
    [Theory]
    [InlineData("4096")]
    [InlineData("18446744073709551615")]
    [InlineData("(exprloc) 2 byte block: 91 4 (DW_OP_fbreg: 4)")]
    public void UnknownOrOversizedArrayDimensionsAreRejected(string upper)
    {
        var dump = $$"""
        <0><c>: Abbrev Number: 1 (DW_TAG_compile_unit)
         <d> DW_AT_name : source.c
        <1><20>: Abbrev Number: 1 (DW_TAG_base_type)
         <21> DW_AT_byte_size : 4
         <22> DW_AT_encoding : 4 (float)
        <1><30>: Abbrev Number: 1 (DW_TAG_array_type)
         <31> DW_AT_type : <0x20>
        <2><40>: Abbrev Number: 1 (DW_TAG_subrange_type)
         <41> DW_AT_upper_bound : {{upper}}
        <1><50>: Abbrev Number: 1 (DW_TAG_variable)
         <51> DW_AT_name : values
         <52> DW_AT_type : <0x30>
         <53> DW_AT_location : (DW_OP_addr: 20000000)
        """;
        Assert.False(Assert.Single(ReadElfIndex.Parse(dump)).Supported);
    }
    [Fact]
    public void DwarfFixtureTypesAndAddressesComeFromElf()
    {
        var variables = ElfIndex.Read(Path.Combine(fixtures, "scalars.elf"));
        Assert.Equal(7, variables.Count(v => v.Supported));
        foreach (var type in new[] { "int8", "uint8", "int16", "uint16", "int32", "uint32", "float32" }) Assert.Single(variables, v => v.Type == type);
        Assert.All(variables, v => Assert.InRange(v.Address, 0x20000000UL, 0x20010000UL));
        Assert.False(variables.Single(v => v.Name == "unsupported64").Supported);
        Assert.Equal(-32, ElfIndex.Decode(variables.Single(v => v.Name == "signed32"), [0xe0, 0xff, 0xff, 0xff]));
        Assert.Equal(1.25, ElfIndex.Decode(variables.Single(v => v.Name == "float32"), [0, 0, 0xa0, 0x3f]));
        Assert.Throws<InvalidDataException>(() => ElfIndex.Decode(variables.First(v => v.Size == 4), [1]));
        Assert.ThrowsAny<Exception>(() => ElfIndex.Read(Path.Combine(fixtures, "invalid.elf")));
    }
    [Fact]
    public void ReadElfDwarf5AdapterResolvesTypeChainsWithoutGuessing()
    {
        const string dump = """
         <0><c>: Abbrev Number: 1 (DW_TAG_compile_unit)
            <d> DW_AT_name : (strp) (offset: 0x1): firmware.c
         <1><20>: Abbrev Number: 2 (DW_TAG_base_type)
            <21> DW_AT_byte_size : (data1) 4
            <22> DW_AT_encoding : (data1) 5 (signed)
         <1><30>: Abbrev Number: 3 (DW_TAG_volatile_type)
            <31> DW_AT_type : (ref4) <0x20>, int
         <1><40>: Abbrev Number: 4 (DW_TAG_variable)
            <41> DW_AT_name : (string) counter
            <42> DW_AT_type : (ref4) <0x30>
            <43> DW_AT_external : (flag_present) 1
            <44> DW_AT_location : (exprloc) 5 byte block: 3 0 0 0 20 (DW_OP_addr: 20000000)
         <1><50>: Abbrev Number: 4 (DW_TAG_variable)
            <51> DW_AT_name : (string) local
            <52> DW_AT_type : (ref4) <0x30>
            <54> DW_AT_location : (exprloc) 2 byte block: 91 4 (DW_OP_fbreg: 4)
        """;
        var variable = Assert.Single(ReadElfIndex.Parse(dump));
        Assert.Equal("global:counter", variable.Identity); Assert.Equal("int32", variable.Type);
        Assert.Equal(0x20000000UL, variable.Address);
    }
}
