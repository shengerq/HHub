namespace Hhub.Core;

public sealed record PluginBatchIssue(string Path, string Message);
public sealed record PluginBatchPlan(LocalPluginPackage[] Packages, PluginBatchIssue[] Issues, PluginBatchIssue[] Skipped);

public static class PluginBatch
{
    public static PluginBatchPlan Plan(IEnumerable<string> paths)
    {
        var packages = new Dictionary<string, LocalPluginPackage>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<PluginBatchIssue>(); var skipped = new List<PluginBatchIssue>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Path.GetExtension(path).ToLowerInvariant() is not (".hhub" or ".zip"))
                    throw new InvalidDataException("仅支持 .hhub 或 .zip 插件包");
                var package = PluginCatalog.ReadPackage(path);
                if (packages.TryGetValue(package.Manifest.Id, out var previous))
                {
                    bool newer = Version.Parse(package.Manifest.Version) > Version.Parse(previous.Manifest.Version);
                    skipped.Add(new(newer ? previous.Path : path, "同一插件仅安装本批次最高版本"));
                    if (!newer) continue;
                }
                packages[package.Manifest.Id] = package;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { issues.Add(new(path, error.Message)); }
        }
        return new(packages.Values.ToArray(), issues.ToArray(), skipped.ToArray());
    }
}
