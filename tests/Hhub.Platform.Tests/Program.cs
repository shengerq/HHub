using Hhub.App;
using Windows.Devices.Enumeration;
using Hhub.Core;
using System.Diagnostics;

if (NativeTray.NativeDataSize != 976) throw new Exception("NOTIFYICONDATAW x64 layout is incorrect");
Console.WriteLine("PASS: tray notification structure matches the Windows x64 ABI");
int iconArgument = Array.IndexOf(args, "--tray-icon");
if (iconArgument >= 0)
{
    NativeTray.ValidateIconFile(Path.GetFullPath(args[iconArgument + 1]));
    Console.WriteLine("PASS: tray icon loads as a native Windows icon without displaying UI");
}

var devices = await PlatformServices.EnumerateDevicesAsync();
if (PlatformServices.DeviceEnumerationIssues.Count > 0) throw new Exception(string.Join("; ", PlatformServices.DeviceEnumerationIssues));
Console.WriteLine($"PASS: read-only Core Audio enumeration completed; {PlatformServices.LastDeviceNodes.Count(node => node.IsAudioEndpoint)} active output endpoints");
using (AudioDeviceInventory.Watch(() => { })) { }
Console.WriteLine("PASS: audio endpoint notifications register and release without changing audio state");
if (devices.Select(device => device.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Count)
    throw new Exception("Device instances are not unique");
Console.WriteLine($"PASS: device enumeration completed; {devices.Count} records, distinct instance IDs");
Console.WriteLine($"Interfaces available: {devices.Count(device => !string.IsNullOrEmpty(device.InterfacePath))}");
TaskCompletionSource enumerated = new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
var watcher = PlatformServices.CreateDeviceWatcher();
watcher.Added += (_, _) => { };
watcher.Updated += (_, _) => { };
watcher.Removed += (_, _) => { };
watcher.EnumerationCompleted += (_, _) => enumerated.TrySetResult();
watcher.Stopped += (_, _) => stopped.TrySetResult();
watcher.Start();
await enumerated.Task.WaitAsync(TimeSpan.FromSeconds(20));
Console.WriteLine("PASS: device watcher initial enumeration completed");
watcher.Stop();
await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine("PASS: device watcher stopped");
var activity = PlatformServices.CaptureActivity();
string self = Process.GetCurrentProcess().ProcessName;
if (SceneEngine.EffectiveScene([new SceneRule { Name = "Self", Condition = "running", Application = self, SceneId = "active" }], activity, "daily", false) != "active")
    throw new Exception("Running-process scene trigger did not match its own process");
Console.WriteLine("PASS: real running-process information feeds scene matching");
