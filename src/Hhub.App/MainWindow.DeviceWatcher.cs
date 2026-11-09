using Microsoft.UI.Xaml;
using Windows.Devices.Enumeration;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private DeviceWatcher? _deviceWatcher;
    private IDisposable? _audioDeviceWatcher;
    private bool _deviceEnumerationCompleted;
    private readonly DispatcherTimer _deviceChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _deviceRefreshBusy;
    private bool _deviceRefreshAgain;
    private void StartWatchingDevices()
    {
        if (_deviceWatcher is not null) return;
        _deviceEnumerationCompleted = false;
        _deviceChangeTimer.Tick -= OnDeviceChangeTick;
        _deviceChangeTimer.Tick += OnDeviceChangeTick;
        _deviceWatcher = PlatformServices.CreateDeviceWatcher();
        _deviceWatcher.Added += OnDeviceAdded;
        _deviceWatcher.Removed += OnDeviceUpdated;
        _deviceWatcher.Updated += OnDeviceMetadataUpdated;
        _deviceWatcher.EnumerationCompleted += OnDeviceEnumerationCompleted;
        _deviceWatcher.Start();
        try { _audioDeviceWatcher = AudioDeviceInventory.Watch(() => QueueDeviceRefresh()); }
        catch (System.Runtime.InteropServices.COMException error) { ShowError("音频设备变化监听不可用：" + error.Message); }
    }

    private async void OnDeviceChangeTick(object? sender, object args)
    {
        _deviceChangeTimer.Stop();
        if (_preferences.WatchDevices) await RefreshDevicesAsync();
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device) { if (_deviceEnumerationCompleted) QueueDeviceRefresh(); }
    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate device) { if (_deviceEnumerationCompleted) QueueDeviceRefresh(); }
    private void OnDeviceMetadataUpdated(DeviceWatcher sender, DeviceInformationUpdate device)
    {
        // Property-only notifications (including input-setting broadcasts) must not
        // trigger an expensive device/interface inventory scan.
        if (_deviceEnumerationCompleted && Hhub.Core.DeviceUpdatePolicy.RequiresInventoryRefresh(device.Properties.Keys)) QueueDeviceRefresh();
    }
    private void OnDeviceEnumerationCompleted(DeviceWatcher sender, object args) { _deviceEnumerationCompleted = true; QueueDeviceRefresh(); }

    private void QueueDeviceRefresh() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || !_preferences.WatchDevices) return;
        _deviceChangeTimer.Stop(); _deviceChangeTimer.Start();
    });

    private void StopWatchingDevices()
    {
        _deviceChangeTimer.Stop();
        _audioDeviceWatcher?.Dispose(); _audioDeviceWatcher = null;
        if (_deviceWatcher is null) return;
        _deviceWatcher.Added -= OnDeviceAdded;
        _deviceWatcher.Removed -= OnDeviceUpdated;
        _deviceWatcher.Updated -= OnDeviceMetadataUpdated;
        _deviceWatcher.EnumerationCompleted -= OnDeviceEnumerationCompleted;
        if (_deviceWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _deviceWatcher.Stop();
        _deviceWatcher = null;
        _deviceEnumerationCompleted = false;
    }
}
