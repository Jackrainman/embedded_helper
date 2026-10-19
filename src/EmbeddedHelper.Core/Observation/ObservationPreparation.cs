using System.Text.Json;

namespace EmbeddedHelper.Core;

public enum ObservationConfigurationArea { Project, Observation, Connection, Remote, Settings }

public sealed class ObservationPreparationException(ObservationConfigurationArea area, string message, Exception? inner = null) : Exception(message, inner)
{
    public ObservationConfigurationArea Area { get; } = area;
    public WatchSettings? SavedSettings { get; init; }
    public Variable[] Index { get; init; } = [];
}

public sealed record PreparedObservation(string Root, string Target, WatchSettings Settings, ObservationRequest Request, Variable[] Index, string ElfPath, DateTime ElfStamp);

/// <summary>Reads saved project configuration only. Preparing never starts a worker or loads the SDK.</summary>
public static class ObservationPreparation
{
    public static async Task<PreparedObservation> PrepareAsync(ProjectInfo project, BuildTarget target, string elfPath, string dll, Func<string, Task<Variable[]>> index, bool rttOnly = false, string? transport = null)
    {
        var area = ObservationConfigurationArea.Observation;
        WatchSettings? saved = null;
        Variable[] indexed = [];
        try
        {
            var watch = rttOnly ? new WatchSettings(target.Name, [], 100) : await WatchSettingsStore.LoadAsync(project.Root)
                ?? throw new InvalidDataException("请在 MCU Viewer 中解析 ELF、添加变量并保存。");
            if (watch.Target != target.Name) throw new InvalidDataException("当前目标没有已保存的变量设置，请重新选择并保存。");
            if (!rttOnly && (watch.Rate is < 1 or > 1000 || watch.Identities == null || watch.Identities.Length > 64 || watch.Identities.Any(string.IsNullOrWhiteSpace) || watch.Identities.Distinct().Count() != watch.Identities.Length))
                throw new InvalidDataException("变量列表或采样率无效，请在 MCU Viewer 中重新保存。");
            saved = watch;
            area = ObservationConfigurationArea.Connection;
            var connection = await ConnectionSettingsStore.LoadAsync(project.Root, target.Name, transport)
                ?? throw new InvalidDataException("请先为当前目标保存共享连接配置。");
            watch = watch with { Transport = connection.Transport, ProbeSerial = connection.ProbeSerial, EnableRtt = rttOnly };
            saved = watch;
            if (rttOnly && connection.Transport != "local") throw new InvalidDataException("远程 RTT 请使用 RTT 监视中的网页入口。");
            var host = "";
            var port = 19011;
            var probe = 0;
            if (watch.Transport == "local")
            {
                if (!int.TryParse(watch.ProbeSerial, out probe) || probe <= 0) throw new InvalidDataException("工程中未设置具体本地探针序列号，请在连接配置中填写并保存。");
            }
            else
            {
                area = ObservationConfigurationArea.Remote;
                (host, port) = await ConnectionSettingsStore.RemoteEndpointAsync(project);
            }
            area = ObservationConfigurationArea.Observation;
            var rtt = rttOnly;
            if (watch.Identities.Length == 0 && !rtt) throw new InvalidDataException("请在 MCU Viewer 中搜索添加变量并保存；RTT 请到 RTT 监视。");
            if (watch.Identities.Length > 0)
            {
                area = ObservationConfigurationArea.Project;
                if (!File.Exists(elfPath)) throw new InvalidDataException("工程目标的 ELF 不存在。请先在工程概览中确认 ELF 并解析变量。");
                indexed = await index(elfPath);
            }
            area = ObservationConfigurationArea.Observation;
            var byIdentity = indexed.ToDictionary(v => v.Identity);
            if (watch.Identities.Any(id => !byIdentity.ContainsKey(id))) throw new InvalidDataException("已保存的变量在当前 ELF 中缺失，请回工程检查变量选择后重新保存。");
            var variables = watch.Identities.Select(id => byIdentity[id]).ToArray();
            new ReadPlan(1, watch.Rate, variables).Validate();
            area = ObservationConfigurationArea.Project;
            if (string.IsNullOrWhiteSpace(target.Device) || target.Interface is not ("SWD" or "JTAG") || target.SpeedKHz <= 0) throw new InvalidDataException("工程目标的 J-Link 参数不完整，请检查工程配置。");
            area = ObservationConfigurationArea.Settings;
            if (!File.Exists(dll)) throw new InvalidDataException("本机尚未配置 JLink_x64.dll。请先在设置与诊断中选择已安装的库，再返回采集页面。");
            var request = new ObservationRequest(dll, probe, host, port, target.Device, target.Interface, target.SpeedKHz, watch.Rate, rtt, variables);
            return new(project.Root, target.Name, watch, request, indexed, elfPath, variables.Length > 0 ? File.GetLastWriteTimeUtc(elfPath) : default);
        }
        catch (Exception ex) when (ex is not ObservationPreparationException)
        {
            throw new ObservationPreparationException(area, ex.Message, ex) { SavedSettings = saved, Index = indexed };
        }
    }
}
