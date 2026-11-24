using Hhub.PluginContracts;

namespace Hhub.Desktop.Pages;

internal sealed class PeripheralInventory(PluginLab? lab) : IDisposable
{
    internal sealed record Snapshot(ConnectedPeripheral[] Devices, string? Message, bool Available,
        IReadOnlyDictionary<string, Peripherals.BatteryReading> Battery);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Func<string, string?>> _artwork = new(StringComparer.Ordinal);
    private bool _started;
    private Snapshot? _current;
    public event Action<Snapshot>? Changed;

    public void Subscribe(Action<Snapshot> display)
    {
        Changed += display;
        if (_current is { } snapshot) display(snapshot);
        if (_started) return;
        _started = true;
        _ = ObserveAsync(_lifetime.Token);
    }

    public void Unsubscribe(Action<Snapshot> display) => Changed -= display;

    public void RefreshArtwork(string pluginId, string directory, string dataDirectory)
    {
        try
        {
            if (DevicePluginSettings.ReadOverview(directory, dataDirectory).Artwork is not { } resolve) return;
            _artwork[pluginId] = resolve;
            if (_current is { } snapshot) Publish(snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { System.Diagnostics.Trace.WriteLine(error); }
    }

    private void Publish(Snapshot snapshot)
    {
        if (_artwork.Count > 0)
            snapshot = snapshot with { Devices = snapshot.Devices.Select(device =>
                device.DeviceProvider is { } provider && _artwork.TryGetValue(provider, out var resolve)
                    ? device with { Artwork = resolve(device.Id) } : device).ToArray() };
        _current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private async Task ObserveAsync(CancellationToken token)
    {
        IReadOnlyDictionary<string, Peripherals.BatteryReading> battery = new Dictionary<string, Peripherals.BatteryReading>();
        try
        {
            await Peripherals.ObserveAsync(lab, (devices, message, available) =>
            {
                if (token.IsCancellationRequested) return;
                Publish(new(devices, message, available, battery));
            }, token, includeArtwork: true, showBattery: values => battery = values);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            if (token.IsCancellationRequested) return;
            _current ??= new([], "无法读取外设", false, battery);
            Changed?.Invoke(_current);
        }
    }

    public void Dispose()
    {
        Changed = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
