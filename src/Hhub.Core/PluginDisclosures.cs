namespace Hhub.Core;

public static class PluginDisclosures
{
    public static IEnumerable<string> LicenseFiles(PluginManifest manifest) => manifest.Disclosure is { } value
        ? new[] { value.LicenseFile }.Concat(value.OpenSourceProjects.Concat(value.Dependencies).Select(item => item.LicenseFile)).Distinct(StringComparer.OrdinalIgnoreCase)
        : [];

    public static void Validate(PluginDisclosure? value)
    {
        if (value is null) return; // Installed API-1 plugins remain usable; the UI identifies missing declarations.
        static void Required(string? text, string label)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) throw new InvalidDataException("插件声明缺少有效的" + label);
        }
        static void License(string? license, string? path)
        {
            Required(license, "许可名称"); Required(path, "许可文件");
            _ = PluginCatalog.ResolveInside(Path.GetTempPath(), path!);
        }
        if (value.SchemaVersion != 1) throw new InvalidDataException("不支持此插件依赖声明版本");
        License(value.License, value.LicenseFile);
        if (value.Runtimes is null || value.Runtimes.Length is < 1 or > 64
            || value.OpenSourceProjects is null || value.Dependencies is null
            || value.OpenSourceProjects.Length + value.Dependencies.Length > 1000)
            throw new InvalidDataException("插件必须声明运行环境以及开源项目、依赖数组");
        foreach (var runtime in value.Runtimes)
        {
            if (runtime is null) throw new InvalidDataException("运行环境声明不能为空");
            Required(runtime.Name, "运行环境名称"); Required(runtime.Version, "运行环境版本");
            Required(runtime.Description, "运行环境用途");
            if (runtime.Architecture is not ("x64" or "arm64" or "x86" or "any") || runtime.Provision is not ("host" or "bundled" or "system"))
                throw new InvalidDataException("运行环境架构或提供方式无效");
        }
        foreach (var component in value.OpenSourceProjects.Concat(value.Dependencies))
        {
            if (component is null) throw new InvalidDataException("依赖声明不能为空");
            Required(component.Name, "项目名称"); Required(component.Version, "项目版本或修订号");
            Required(component.Attribution, "项目署名"); License(component.License, component.LicenseFile);
            if (!Uri.TryCreate(component.ProjectUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                throw new InvalidDataException("开源项目及依赖必须提供有效的 HTTPS 项目地址");
        }
    }
}
