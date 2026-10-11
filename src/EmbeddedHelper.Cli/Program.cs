using System.Text.Json;
using EmbeddedHelper.Core;

try
{
    if (args is ["--version"] or ["--self-test"]) { Console.WriteLine(Product.Version); return 0; }
    if (args is ["--project", var path, "check"])
    {
        Console.WriteLine(JsonSerializer.Serialize(ProjectReader.Read(path), Product.Json));
        return 0;
    }
    if (args is ["elf", var elf]) { Console.WriteLine(JsonSerializer.Serialize(ElfIndex.Read(elf), Product.Json)); return 0; }
    Console.Error.WriteLine("embedded-helper-cli --version | --project <目录> check | elf <文件>\n此版本提供离线检查，版本化烧录工具接入尚未开放。");
    return 2;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
