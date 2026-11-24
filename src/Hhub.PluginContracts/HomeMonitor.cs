namespace Hhub.PluginContracts;

/// <summary>Read-only homepage monitoring service. Numeric samples use [] for unavailable or [number].</summary>
public static class HomeMonitor
{
    public const string Service = "home.monitor";
    public const int MajorVersion = 1;
    public const string CpuName = "cpu.name";
    public const string CpuLoad = "cpu.load"; // Percent
    public const string CpuCores = "cpu.cores";
    public const string CpuThreads = "cpu.threads";
    public const string CpuClock = "cpu.clock"; // MHz
    public const string CpuPower = "cpu.power"; // Watts
    public const string CpuTemperature = "cpu.temperature"; // Celsius
    public const string GpuName = "gpu.name";
    public const string GpuLoad = "gpu.load"; // Percent
    public const string GpuClock = "gpu.clock"; // MHz
    public const string GpuPower = "gpu.power"; // Watts
    public const string GpuTemperature = "gpu.temperature"; // Celsius
    public const string GpuMemoryTemperature = "gpu.memory.temperature"; // Celsius
    public const string GpuMemoryUsed = "gpu.memory.used"; // GiB
    public const string GpuMemoryTotal = "gpu.memory.total"; // GiB
    public const string MemoryManufacturer = "memory.manufacturer"; // Optional display name from installed physical modules.
    public const string MemoryModel = "memory.model"; // Optional installed physical module part numbers.
    public const string MemoryLoad = "memory.load"; // Percent
    public const string MemoryUsed = "memory.used"; // GiB
    public const string MemoryTotal = "memory.total"; // GiB
    public const string MemoryClock = "memory.clock"; // MT/s
    public const string MemoryTemperature = "memory.temperature"; // Celsius
    public const string NetworkDownload = "network.download"; // Mbps
    public const string NetworkUpload = "network.upload"; // Mbps
    public const string Volumes = "storage.volumes"; // [{name, used, total}], GiB
    public const string Disks = "storage.disks"; // [{name, total, temperature}], GiB and Celsius; temperature is [] or [number]
    public const string AuthorizationRequest = "monitor.sensor.authorization.request"; // Optional writable session Boolean.
    public const string AuthorizationStatus = "monitor.sensor.authorization"; // Actual status, independent of the request.
    public const string DriverDownloadUrl = "monitor.sensor.driver.download-url"; // Optional HTTPS link for a missing sensor driver.
}
