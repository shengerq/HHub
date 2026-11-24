using Hhub.PluginContracts;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hhub.Desktop.Pages;

internal static class Peripherals
{
    internal readonly record struct BatteryReading(int Percent, bool Charging);

    public static async Task ObserveAsync(PluginLab? lab, Action<ConnectedPeripheral[], string?, bool> display, CancellationToken token,
        bool includeArtwork = false, Action<IReadOnlyDictionary<string, BatteryReading>>? showBattery = null)
    {
        if (lab is null)
        {
            display(ManagedDevices.Apply([]), "未连接设备发现插件", false);
            return;
        }

        ConnectedPeripheral[] devices = [];
        string? message = null;
        bool available = false;
        bool identitiesReady = false;
        int inventoryRevision = 0;
        var details = new Dictionary<string, IReadOnlyDictionary<string, (string Name, string Connection,
            string? Artwork, string? Category, string? OverviewProvider)>>(StringComparer.Ordinal);
        var batteryReadings = new Dictionary<string, (long Captured, Dictionary<string, BatteryReading> Values)>(StringComparer.Ordinal);
        var alternateConnections = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var physicalConnections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var observedTargets = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Show()
        {
            if (available && !identitiesReady) return;
            var mapped = devices.Where(device => !alternateConnections.Any(provider => provider.Value.Contains(device.Id)
                && details.TryGetValue(provider.Key, out var targets) && targets.ContainsKey(device.Id))).Select(device =>
            {
                var matches = details.Select(provider =>
                {
                    string id = physicalConnections.GetValueOrDefault(provider.Key)?.GetValueOrDefault(device.Id) ?? device.Id;
                    return (Id: id, Provider: provider.Key, Details: provider.Value.GetValueOrDefault(id));
                }).Where(match => !string.IsNullOrWhiteSpace(match.Details.Name)).Take(2).ToArray();
                var match = matches.FirstOrDefault();
                return matches.Length == 1 ? device with
                {
                    Id = match.Id,
                    Name = match.Details.Name,
                    Connection = string.IsNullOrWhiteSpace(match.Details.Connection) ? device.Connection : match.Details.Connection,
                    Category = device.Category == "扬声器" ? device.Category : match.Details.Category ?? device.Category,
                    Artwork = match.Details.Artwork,
                    DeviceProvider = match.Provider,
                    OverviewProvider = match.Details.OverviewProvider,
                    IsPreciselyIdentified = match.Details.Category is not null,
                    HasDevicePlugin = true
                } : device with { Artwork = null, DeviceProvider = null, OverviewProvider = null, IsPreciselyIdentified = false, HasDevicePlugin = matches.Length > 0 };
            }).DistinctBy(device => device.Id, StringComparer.Ordinal).ToArray();
            if (showBattery is not null)
            {
                var percentages = new Dictionary<string, BatteryReading>(StringComparer.Ordinal);
                foreach (var device in mapped)
                    if (device.OverviewProvider is { } provider && batteryReadings.TryGetValue(provider, out var readings)
                        && readings.Values.TryGetValue(device.Id, out var reading))
                        percentages[device.Id] = reading;
                showBattery(percentages);
            }
            display(ManagedDevices.Apply(mapped), message, available);
        }

        Action changed = Show;
        ManagedDevices.Changed += changed;
        try { await Task.WhenAll(ObserveInventory(), ObserveDevicePlugins()); }
        finally { ManagedDevices.Changed -= changed; }

        async Task ObserveInventory()
        {
            try
            {
                await lab.ReadServiceAsync(new(PeripheralDiscovery.Service, PeripheralDiscovery.MajorVersion), (values, _) =>
                {
                    var next = values[PeripheralDiscovery.Connected].Deserialize<ConnectedPeripheral[]>(PluginApi.Json)
                        ?? throw new InvalidDataException("devices.inventory-invalid");
                    if (!next.Select(device => device.Id).SequenceEqual(devices.Select(device => device.Id))) inventoryRevision++;
                    devices = next;
                    message = devices.Length == 0 ? "未发现已连接的外设" : null;
                    available = true;
                    Show();
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                System.Diagnostics.Trace.WriteLine(error);
                if (!token.IsCancellationRequested)
                {
                    devices = [];
                    message = "无法读取外设";
                    available = false;
                    Show();
                }
            }
        }

        async Task ObserveDevicePlugins()
        {
            try
            {
                var (_, definitions) = await lab.DiscoverManifestsAsync().WaitAsync(token);
                var devices = definitions.Where(plugin => plugin.Definition.Kind == "device").ToArray();
                var scans = new Dictionary<string, (int Revision, long At)>(StringComparer.Ordinal);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
                while (true)
                {
                    foreach (var definition in devices)
                    {
                        token.ThrowIfCancellationRequested();
                        string id = definition.Definition.Id;
                        int revision = inventoryRevision;
                        bool liveBattery = showBattery is not null && details.GetValueOrDefault(id)?.Count > 0
                            && definition.Definition.Provides.Any(service => service.Contract == DeviceOverview.Service
                                && service.CapabilityIds.Contains(DeviceOverview.BatteryPercent));
                        if (identitiesReady && scans.TryGetValue(id, out var scan) && scan.Revision == revision
                            && System.Diagnostics.Stopwatch.GetElapsedTime(scan.At) < TimeSpan.FromSeconds(liveBattery ? 15 : 45)) continue;
                        try
                        {
                            var (store, registered) = await lab.RegisterAsync(item => item.Id == id).WaitAsync(token);
                            if (registered is not [var plugin]) continue;
                            bool finished = false;
                            var scanTask = lab.TryRunIdlePageAsync(plugin, async scanToken =>
                            {
                                await using var session = await lab.StartSessionAsync(store, plugin, scanToken, overviewOnly: true);
                                scanToken.ThrowIfCancellationRequested();
                                string data = store.DataDirectory(id);
                                var overview = DevicePluginSettings.ReadOverview(plugin.Directory, data);
                                string? overviewProvider = includeArtwork && DeviceOverview.IsProvidedBy(plugin.Definition) ? id : null;
                                details[id] = session.Targets.Where(target => target.Online && !target.Simulated)
                                    .ToDictionary(target => target.Id, target => (target.Name, target.Connection,
                                        includeArtwork ? overview.Artwork?.Invoke(target.Id) : null, overview.Category, overviewProvider),
                                        StringComparer.Ordinal);
                                var targets = details[id];
                                var capabilities = plugin.Definition.Provides.Where(service => service.Contract == DeviceOverview.Service
                                    && Version.TryParse(service.Version, out var version) && version.Major == 1)
                                    .SelectMany(service => service.CapabilityIds).ToHashSet(StringComparer.Ordinal);
                                bool readBattery = showBattery is not null && overviewProvider is not null && capabilities.Contains(DeviceOverview.BatteryPercent);
                                bool readAlternates = capabilities.Contains(DeviceOverview.AlternateConnections) && targets.Count > 1;
                                bool readPhysical = capabilities.Contains(DeviceOverview.PhysicalConnections);
                                var routes = targets.Select(target => target.Key + "\n" + target.Value.Connection).ToArray();
                                bool unchanged = observedTargets.TryGetValue(id, out var prior) && prior.SequenceEqual(routes);
                                if (!unchanged)
                                {
                                    alternateConnections.Remove(id);
                                    physicalConnections.Remove(id);
                                    batteryReadings.Remove(id);
                                }
                                var presentation = readPhysical ? ReadPhysicalPresentation(data, targets.Keys) : null;
                                if (presentation is not null)
                                {
                                    physicalConnections[id] = presentation;
                                    readPhysical = false;
                                }
                                // Publish identity before opening the passive battery channel on the first visit.
                                if (!identitiesReady && !readPhysical && !readAlternates)
                                {
                                    observedTargets[id] = routes;
                                    finished = true;
                                    return;
                                }
                                if ((!readBattery && !readAlternates && !readPhysical) || unchanged && batteryReadings.TryGetValue(id, out var previous)
                                    && System.Diagnostics.Stopwatch.GetElapsedTime(previous.Captured) < TimeSpan.FromSeconds(15))
                                { if (available) Show(); finished = true; return; }
                                var percentages = new Dictionary<string, BatteryReading>(StringComparer.Ordinal);
                                var trayRows = new Dictionary<string, string>(StringComparer.Ordinal);
                                var alternatives = new HashSet<string>(StringComparer.Ordinal);
                                var physical = new Dictionary<string, string>(StringComparer.Ordinal);
                                var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                                foreach (string target in targets.Keys)
                                {
                                    if (alternatives.Contains(target)) continue;
                                    scanToken.ThrowIfCancellationRequested();
                                    try
                                    {
                                        var response = await session.ReadAsync(target, scanToken);
                                        if (readBattery && response.Values is { } telemetry)
                                            trayRows[target] = TrayDeviceStatus.Row(target, targets[target].Name, plugin.Definition, capabilities, telemetry);
                                        if (readPhysical && response.Values?.TryGetValue(DeviceOverview.PhysicalConnections, out var connections) == true
                                            && connections.ValueKind == System.Text.Json.JsonValueKind.Array)
                                            foreach (var connection in connections.EnumerateArray())
                                                if (connection.ValueKind == System.Text.Json.JsonValueKind.String && connection.GetString() is { } physicalId
                                                    && (physicalId == target || !targets.ContainsKey(physicalId)) && !physical.TryAdd(physicalId, target))
                                                    ambiguous.Add(physicalId);
                                        if (readAlternates && response.Values?.TryGetValue(DeviceOverview.AlternateConnections, out var links) == true
                                            && links.ValueKind == System.Text.Json.JsonValueKind.Array)
                                            foreach (var link in links.EnumerateArray())
                                                if (link.ValueKind == System.Text.Json.JsonValueKind.String && link.GetString() is { } other
                                                    && other != target && targets.ContainsKey(other)) alternatives.Add(other);
                                        if (readBattery && response.Values?.TryGetValue(DeviceOverview.BatteryPercent, out var value) == true
                                            && value.ValueKind == System.Text.Json.JsonValueKind.Array && value.GetArrayLength() == 1
                                            && value[0].ValueKind == System.Text.Json.JsonValueKind.Number
                                            && value[0].TryGetInt32(out int percent) && percent is >= 0 and <= 100)
                                        {
                                            bool charging = response.Values.TryGetValue(DeviceOverview.BatteryCharging, out var state)
                                                && state.ValueKind == System.Text.Json.JsonValueKind.Array && state.GetArrayLength() == 1
                                                && state[0].ValueKind == System.Text.Json.JsonValueKind.True;
                                            percentages[target] = new(percent, charging);
                                        }
                                    }
                                    catch (Exception error) when (!scanToken.IsCancellationRequested && error is not OperationCanceledException)
                                    { System.Diagnostics.Trace.WriteLine(error); }
                                }
                                foreach (string conflict in ambiguous) physical.Remove(conflict);
                                observedTargets[id] = routes;
                                alternateConnections[id] = alternatives;
                                if (readPhysical) physicalConnections[id] = physical;
                                batteryReadings[id] = (System.Diagnostics.Stopwatch.GetTimestamp(), percentages);
                                if (!scanToken.IsCancellationRequested && readBattery)
                                    TrayBattery.PublishStatus(id, trayRows.Where(row => !alternatives.Contains(row.Key)).Select(row => row.Value));
                                if (!scanToken.IsCancellationRequested && available) Show();
                                finished = !scanToken.IsCancellationRequested;
                            }, token);
                            if (scanTask is null) continue;
                            await scanTask;
                            if (!finished) continue;
                            bool needsFirstBattery = !identitiesReady && showBattery is not null && details.GetValueOrDefault(id)?.Count > 0
                                && definition.Definition.Provides.Any(service => service.CapabilityIds.Contains(DeviceOverview.BatteryPercent));
                            if (!needsFirstBattery)
                                scans[id] = (revision, System.Diagnostics.Stopwatch.GetTimestamp());
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                        {
                            System.Diagnostics.Trace.WriteLine(error);
                        }
                        catch (Exception error)
                        {
                            System.Diagnostics.Trace.WriteLine(error);
                            details.Remove(id);
                            batteryReadings.Remove(id);
                            alternateConnections.Remove(id);
                            physicalConnections.Remove(id);
                            observedTargets.Remove(id);
                            if (!token.IsCancellationRequested && available) Show();
                        }
                    }
                    if (!identitiesReady)
                    {
                        identitiesReady = true;
                        if (!token.IsCancellationRequested && available) Show();
                    }
                    else if (!await timer.WaitForNextTickAsync(token)) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            finally
            {
                if (!identitiesReady && !token.IsCancellationRequested) { identitiesReady = true; Show(); }
            }
        }
    }

    public static async Task<DeviceOverviewRow[]> ReadOverviewAsync(PluginLab lab, ConnectedPeripheral device, CancellationToken token)
    {
        var (store, plugins) = await lab.RegisterAsync(item => item.Id == device.OverviewProvider).WaitAsync(token);
        var plugin = plugins.FirstOrDefault(candidate => DeviceOverview.IsProvidedBy(candidate.Definition));
        if (plugin is null) return [];
        DeviceOverviewRow[] rows = [];
        var overview = lab.TryRunIdlePageAsync(plugin, async overviewToken =>
        {
            await using var session = await lab.StartSessionAsync(store, plugin, overviewToken);
            if (!session.Targets.Any(target => target.Id == device.Id && target.Online && !target.Simulated)) return;
            overviewToken.ThrowIfCancellationRequested();
            var response = await session.ReadAsync(device.Id, overviewToken);
            overviewToken.ThrowIfCancellationRequested();
            if (response.Values?.TryGetValue(DeviceOverview.Parameters, out var values) == true)
                rows = PluginApi.Read<DeviceOverviewRow[]>(values.GetRawText());
        }, token);
        if (overview is not null) await overview;
        return rows;
    }

    public static string Glyph(ConnectedPeripheral device) => device.Category switch
    {
        "鼠标" => "\uE962",
        "键盘" => "\uE765",
        "扬声器" => "\uE7F5",
        "耳机" => "\uE7F6",
        "音频" => "\uE8D6",
        "手柄" => "\uE7FC",
        "摄像头" => "\uE722",
        "打印机" => "\uE749",
        "磁盘" => "\uEDA2",
        _ => "\uE772"
    };

    public static string ConnectionLabel(ConnectedPeripheral device)
    {
        string connection = device.Connection.Trim();
        if (device.Category is not ("鼠标" or "键盘")) return connection;
        string compact = string.Concat(connection.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        return compact switch
        {
            "2.4" or "2.4G" or "2.4GHZ" or "2.4G无线" or "2.4GHZ无线" => "2.4G",
            "USB" or "有线" or "USB有线" or "有线USB" or "WIRED" => "USB",
            "蓝牙" or "BLUETOOTH" or "BT" or "BLE" => "蓝牙",
            _ => connection
        };
    }

    public static FrameworkElement ConnectionIndicator(ConnectedPeripheral device)
    {
        string connection = ConnectionLabel(device);
        if (connection != "蓝牙")
            return new TextBlock { Text = connection, VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["DashboardDetailStyle"] };
        var icon = new FontIcon { Glyph = "\uE702", FontSize = 14, FontFamily = new FontFamily("Segoe Fluent Icons"),
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(icon, connection);
        ToolTipService.SetToolTip(icon, connection);
        return icon;
    }

    public static StackPanel DeviceConnectionDetails(ConnectedPeripheral device)
    {
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        details.Children.Add(new TextBlock { Text = device.Category,
            Style = (Style)Application.Current.Resources["DashboardDetailStyle"] });
        details.Children.Add(ConnectionIndicator(device));
        return details;
    }

    public static FrameworkElement DeviceThumbnail(ConnectedPeripheral device, double size)
    {
        var fallback = new FontIcon { Glyph = Glyph(device), FontSize = size * 0.72, FontFamily = new FontFamily("Segoe Fluent Icons") };
        var frame = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        frame.Children.Add(fallback);
        if (device.Artwork is not { } path) return frame;
        var image = new Image { Stretch = Stretch.Uniform };
        frame.Children.Add(image);
        image.Loaded += async (_, _) =>
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelHeight = size >= 96 ? 248 : 96 };
                await bitmap.SetSourceAsync(stream);
                if (!image.IsLoaded) return;
                image.Source = bitmap;
                fallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { System.Diagnostics.Trace.WriteLine(error); }
        };
        return frame;
    }

    private static Dictionary<string, string>? ReadPhysicalPresentation(string data, IEnumerable<string> online)
    {
        try
        {
            var file = new FileInfo(Path.Combine(data, "device-connections.json"));
            if (!file.Exists || file.Length > 64 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var values = PluginApi.Read<Dictionary<string, string[]>>(File.ReadAllText(file.FullName));
            var targets = online.ToHashSet(StringComparer.Ordinal);
            if (!targets.SetEquals(values.Keys)) return null;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (target, connections) in values)
            {
                if (connections is null || connections.Length > 16) return null;
                foreach (string id in connections)
                    if (id is not { Length: 64 } || !id.All(Uri.IsHexDigit) || id != target && targets.Contains(id)
                        || !result.TryAdd(id, target)) return null;
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { System.Diagnostics.Trace.WriteLine(error); return null; }
    }
}
