using System.IO;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

internal sealed class ConfigurationService(string dataDirectory)
{
    public string JournalFor(string root) => Path.Combine(dataDirectory, "projects", ConfigTransaction.Hash(root), "transactions");
    public bool HasPending(string root) => ConfigTransaction.Pending(JournalFor(root)).Length > 0;
    public Task ApplyAsync(ConfigPlan plan) => Task.Run(() => ConfigTransaction.Apply(plan, JournalFor(plan.Root)));
    public Task<string[]> RecoverAsync(string root) => Task.Run(() => ConfigTransaction.RecoverIncomplete(root, JournalFor(root)));
}
