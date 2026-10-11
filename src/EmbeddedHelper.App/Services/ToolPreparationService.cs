using System.IO;
using System.Net.Http;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

internal static class ToolPreparationService
{
    public static async Task<string> PrepareAsync(string dataDirectory, ToolRelease release, string? zip)
    {
        string? download = null;
        try
        {
            if (zip == null)
            {
                var directory = System.IO.Path.Combine(dataDirectory, "downloads"); Directory.CreateDirectory(directory);
                download = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbeddedHelper/" + Product.Version);
                using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using (var output = File.Create(download))
                {
                    var buffer = new byte[65536]; long total = 0; int count;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += count; if (total > 128L * 1024 * 1024) throw new IOException("工具下载超过128MiB上限。");
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    }
                }
                zip = download;
            }
            var file = zip;
            return await Task.Run(() => new ToolCache(dataDirectory).InstallAsync(file, release));
        }
        finally { if (download != null && File.Exists(download)) File.Delete(download); }
    }
}
