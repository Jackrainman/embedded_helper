using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbeddedHelper.Core;

public sealed record FileChange(string Path, string? Before, string After);
public sealed record ConfigPlan(string Root, FileChange[] Changes);
