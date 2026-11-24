#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <vector>
#include <commctrl.h>
#include <shobjidl.h>
#include <cwchar>
#include <string>
#include <algorithm>
#include <array>
#include <map>
#include <set>
#include <sstream>

namespace {
constexpr wchar_t DevelopmentWindowClass[] = L"Hhub.Rebuild.Tray";
constexpr wchar_t InstalledWindowClass[] = L"Hhub.Rebuild.Tray.Installed";
constexpr wchar_t TrayExitProperty[] = L"Hhub.Rebuild.Tray.Exiting";
constexpr wchar_t DesktopProperty[] = L"Hhub.Rebuild.Desktop";
constexpr UINT TrayCallback = WM_APP + 1;
constexpr UINT OpenWindow = WM_APP + 2;
constexpr UINT VirtualBattery = WM_APP + 3;
constexpr UINT RememberPage = WM_APP + 4;
constexpr UINT ExitWithDesktop = WM_APP + 5;
constexpr UINT RefreshEfficiency = WM_APP + 6;
constexpr UINT_PTR RetryOpen = 1;
constexpr UINT_PTR BatteryTick = 2;
constexpr UINT_PTR HoverTick = 3;
constexpr UINT_PTR StartupTick = 4;
constexpr UINT_PTR DesktopLifetimeTick = 5;
constexpr ULONG_PTR DesktopArguments = 1;
constexpr ULONG_PTR BatteryProvider = 2;
constexpr ULONG_PTR ReadDeviceBattery = 4;
constexpr ULONG_PTR DeviceStatus = 5;
constexpr ULONG_PTR GlobalVisibility = 6;
constexpr ULONG_PTR PluginVisibility = 7;
constexpr ULONG_PTR LoadVisibility = 8;
constexpr ULONG_PTR UpdateDeviceStatus = 9;
constexpr UINT ExitTray = 1;
constexpr UINT OpenMain = 2;

NOTIFYICONDATAW icon{};
UINT taskbarCreated;
PROCESS_INFORMATION desktop{};
HANDLE trayServices;
DWORD trayServicesPid;
HWND owner;
bool iconAdded;
bool shellReady;
bool delayedShowDesktop;
std::wstring desktopArguments;
int lastPage = -1;
unsigned retryCount;
HANDLE batteryPipe;
HANDLE batteryProcess;
DWORD batteryPercent = 101;
std::array<BYTE, 32> batteryTarget{};
bool batteryWithTarget;
std::wstring batteryName;
std::wstring batteryPlugin;
std::wstring batteryPath;
std::wstring batteryData;
HWND tooltip;
TOOLINFOW tool{};
bool tipVisible;
bool showBattery;
unsigned virtualBattery = 101;
ULONGLONG lastStatusRequest;
std::wstring tipText;
std::set<std::wstring> hiddenBattery;
struct StatusRow { std::wstring name, battery, variables; ULONGLONG readAt; };
std::map<std::pair<std::wstring, std::wstring>, StatusRow> statusRows;

struct InstallationProcess { HANDLE handle; DWORD pid; bool tray; };

BOOL CALLBACK CloseInstallationWindow(HWND window, LPARAM data)
{
    const auto& processes = *reinterpret_cast<const std::vector<InstallationProcess>*>(data);
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    const auto match = std::find_if(processes.begin(), processes.end(),
        [pid](const auto& process) { return process.pid == pid; });
    if (match == processes.end()) return TRUE;
    wchar_t type[96]{};
    GetClassNameW(window, type, ARRAYSIZE(type));
    if (match->tray ? (wcscmp(type, InstalledWindowClass) == 0 || wcscmp(type, DevelopmentWindowClass) == 0)
                    : (IsWindowVisible(window) || GetPropW(window, DesktopProperty)))
        PostMessageW(window, WM_CLOSE, 0, 0);
    return TRUE;
}

int CloseInstallation(const std::wstring& folder)
{
    const auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 2;
    std::vector<InstallationProcess> processes;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    for (BOOL found = Process32FirstW(snapshot, &entry); found; found = Process32NextW(snapshot, &entry)) {
        const bool tray = _wcsicmp(entry.szExeFile, L"Hhub.Tray.exe") == 0;
        if (!tray && _wcsicmp(entry.szExeFile, L"Hhub.Desktop.exe") != 0
                 && _wcsicmp(entry.szExeFile, L"Hhub.App.exe") != 0) continue;
        const auto handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, entry.th32ProcessID);
        if (!handle) continue;
        std::wstring executable(32768, L'\0');
        DWORD length = static_cast<DWORD>(executable.size());
        if (QueryFullProcessImageNameW(handle, 0, executable.data(), &length)) {
            executable.resize(length);
            if (_wcsicmp(executable.c_str(), (folder + L"\\" + entry.szExeFile).c_str()) == 0) {
                processes.push_back({handle, entry.th32ProcessID, tray});
                continue;
            }
        }
        CloseHandle(handle);
    }
    CloseHandle(snapshot);
    // Closing the tray first lets it mark its window as exiting before it closes the UI.
    for (const auto& process : processes) if (process.tray) {
        std::vector<InstallationProcess> coordinator{process};
        EnumWindows(CloseInstallationWindow, reinterpret_cast<LPARAM>(&coordinator));
        WaitForSingleObject(process.handle, 3000);
    }
    EnumWindows(CloseInstallationWindow, reinterpret_cast<LPARAM>(&processes));
    const auto deadline = GetTickCount64() + 25000;
    bool closed = true;
    for (const auto& process : processes) {
        auto now = GetTickCount64();
        if (WaitForSingleObject(process.handle, now < deadline ? static_cast<DWORD>(deadline - now) : 0) != WAIT_OBJECT_0)
            closed = false;
        CloseHandle(process.handle);
    }
    return closed ? 0 : 3;
}

void AppendArgument(std::wstring& command, const wchar_t* argument)
{
    command += L" \"";
    size_t slashes = 0;
    for (; *argument; ++argument) {
        if (*argument == L'\\') {
            ++slashes;
            continue;
        }
        if (*argument == L'"') command.append(slashes + 1, L'\\');
        command.append(slashes, L'\\');
        command += *argument;
        slashes = 0;
    }
    command.append(slashes * 2, L'\\');
    command += L'"';
}

BOOL CALLBACK FindDesktopWindow(HWND window, LPARAM result)
{
    DWORD processId;
    GetWindowThreadProcessId(window, &processId);
    if (processId != desktop.dwProcessId || !GetPropW(window, DesktopProperty)) return TRUE;
    *reinterpret_cast<HWND*>(result) = window;
    return FALSE;
}

HWND DesktopWindow()
{
    HWND result = nullptr;
    if (desktop.hProcess) EnumWindows(FindDesktopWindow, reinterpret_cast<LPARAM>(&result));
    return result;
}

void CloseProcessHandle()
{
    if (desktop.hProcess) CloseHandle(desktop.hProcess);
    desktop = {};
}

bool efficiencyEnabled;

void SetEfficiency(HANDLE process, bool enabled)
{
    if (!process || WaitForSingleObject(process, 0) != WAIT_TIMEOUT) return;
    PROCESS_POWER_THROTTLING_STATE configuration{PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        enabled ? static_cast<ULONG>(PROCESS_POWER_THROTTLING_EXECUTION_SPEED) : 0ul,
        enabled ? static_cast<ULONG>(PROCESS_POWER_THROTTLING_EXECUTION_SPEED) : 0ul};
    if (!SetProcessInformation(process, ProcessPowerThrottling, &configuration, sizeof(configuration)))
        OutputDebugStringW(L"HHub: EcoQoS configuration failed.\n");
    if (!SetPriorityClass(process, enabled ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS))
        OutputDebugStringW(L"HHub: CPU priority configuration failed.\n");
}

void UpdateEfficiency()
{
    DWORD preference = 1;
    DWORD size = sizeof(preference);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\HHub\\Desktop", L"TrayEfficiency", RRF_RT_REG_DWORD,
        nullptr, &preference, &size) != ERROR_SUCCESS) preference = 1;
    efficiencyEnabled = preference != 0;
    SetEfficiency(GetCurrentProcess(), efficiencyEnabled);
    SetEfficiency(trayServices, efficiencyEnabled);
    SetEfficiency(batteryProcess, efficiencyEnabled);
}

std::wstring ApplicationFolder()
{
    wchar_t executable[32768];
    const auto count = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    if (count == 0 || count == ARRAYSIZE(executable)) return {};
    std::wstring folder(executable, count);
    folder.resize(folder.find_last_of(L'\\') + 1);
    return folder;
}

void InitializeTrayServices()
{
    if (desktopArguments.empty()) return;
    if (trayServices) {
        if (WaitForSingleObject(trayServices, 0) == WAIT_TIMEOUT) return;
        CloseHandle(trayServices);
        trayServices = nullptr;
        trayServicesPid = 0;
    }
    const auto folder = ApplicationFolder();
    if (folder.empty()) return;
    const auto ui = folder + L"Hhub.Desktop.exe";
    auto command = L"\"" + ui + L"\" --tray-services" + desktopArguments;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(ui.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr, folder.c_str(), &startup, &process)) return;
    trayServices = process.hProcess;
    trayServicesPid = process.dwProcessId;
    SetEfficiency(trayServices, efficiencyEnabled);
    ResumeThread(process.hThread);
    CloseHandle(process.hThread);
}

void OpenDesktop()
{
    if (desktop.hProcess && WaitForSingleObject(desktop.hProcess, 0) == WAIT_TIMEOUT) {
        if (const auto window = DesktopWindow()) {
            KillTimer(owner, RetryOpen);
            if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
            else if (!IsWindowVisible(window)) ShowWindow(window, SW_SHOW);
            SetForegroundWindow(window);
        } else {
            SetTimer(owner, RetryOpen, 200, nullptr);
        }
        return;
    }
    KillTimer(owner, RetryOpen);
    CloseProcessHandle();

    const auto folder = ApplicationFolder();
    if (folder.empty()) return;
    const auto ui = folder + L"Hhub.Desktop.exe";
    auto command = L"\"" + ui + L"\" --from-tray " + std::to_wstring(lastPage) + desktopArguments;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    if (!CreateProcessW(ui.c_str(), command.data(), nullptr, nullptr, FALSE, NORMAL_PRIORITY_CLASS,
        nullptr, folder.c_str(), &startup, &desktop)) {
        MessageBoxW(owner, L"无法打开 HHub 主界面。", L"HHub", MB_OK | MB_ICONERROR);
        return;
    }
    CloseHandle(desktop.hThread);
    desktop.hThread = nullptr;
    UpdateEfficiency();
    SetTimer(owner, DesktopLifetimeTick, 1000, nullptr);
}

bool StartShell();

void RequestOpen()
{
    if (!StartShell()) return;
    retryCount = 0;
    OpenDesktop();
}

bool AddIcon()
{
    iconAdded = Shell_NotifyIconW(NIM_ADD, &icon);
    if (iconAdded) {
        icon.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &icon);
    }
    return iconAdded;
}

bool StartShell()
{
    if (shellReady) return true;
    KillTimer(owner, StartupTick);
    if (!AddIcon()) {
        MessageBoxW(owner, L"无法添加 HHub 托盘图标。", L"HHub", MB_OK | MB_ICONERROR);
        return false;
    }
    shellReady = true;
    return true;
}

bool UsesShellTooltip()
{
    return !tooltip || tipText.size() < ARRAYSIZE(icon.szTip);
}

void UpdateTooltip()
{
    const auto now = GetTickCount64();
    const auto target = [&] {
        std::wstring hash;
        if (batteryWithTarget) for (auto value : batteryTarget) {
            constexpr wchar_t hex[] = L"0123456789abcdef";
            hash += hex[value >> 4];
            hash += hex[value & 15];
        }
        return hash;
    }();
    const bool legacyVisible = showBattery && !hiddenBattery.count(batteryPlugin)
        && (batteryPercent <= 100 || (batteryPercent >= 102 && batteryPercent <= 202));
    bool legacyMerged = false;
    std::wstring text;
    const auto append = [&text](const std::wstring& name, const std::wstring& battery, const std::wstring& variables) {
        if (battery.empty() && variables.empty()) return;
        if (!text.empty()) text += L"\r\n";
        text += name;
        if (!battery.empty()) text += L" · " + battery;
        if (!variables.empty()) text += L" · " + variables;
    };
    const auto legacyBattery = [&] {
        return batteryPercent <= 100 ? std::to_wstring(batteryPercent) + L"%"
            : std::to_wstring(batteryPercent - 102) + L"%（上次）";
    };
    for (auto row = statusRows.begin(); row != statusRows.end();) {
        if (now - row->second.readAt > 45000) { row = statusRows.erase(row); continue; }
        const auto& [key, status] = *row++;
        auto battery = showBattery && !hiddenBattery.count(key.first) ? status.battery : L"";
        if (legacyVisible && key.first == batteryPlugin && key.second == target) {
            legacyMerged = true;
            if (battery.empty()) battery = legacyBattery();
        }
        append(status.name, battery, status.variables);
    }
    if (legacyVisible && !legacyMerged) append(batteryName, legacyBattery(), L"");
    if (virtualBattery <= 100) append(L"虚拟外设", std::to_wstring(virtualBattery) + L"%", L"");
    if (text.empty()) text = L"暂无设备状态";
    if (tipText == text) return;
    tipText = std::move(text);
    icon.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | (UsesShellTooltip() ? NIF_SHOWTIP : 0);
    size_t limit = ARRAYSIZE(icon.szTip) - 1;
    if (tipText.size() > limit) {
        const auto lineEnd = tipText.rfind(L'\r', limit);
        if (lineEnd != std::wstring::npos) limit = lineEnd;
        else if (tipText[limit - 1] >= 0xd800 && tipText[limit - 1] <= 0xdbff) --limit;
    }
    wcsncpy_s(icon.szTip, ARRAYSIZE(icon.szTip), tipText.c_str(), limit);
    if (iconAdded) Shell_NotifyIconW(NIM_MODIFY, &icon);
    tool.lpszText = tipText.data();
    if (tooltip) SendMessageW(tooltip, TTM_UPDATETIPTEXTW, 0, reinterpret_cast<LPARAM>(&tool));
    if (tipVisible && UsesShellTooltip()) {
        tipVisible = false;
        SendMessageW(tooltip, TTM_TRACKACTIVATE, FALSE, reinterpret_cast<LPARAM>(&tool));
    }
}

void SetVirtualBatteryTip(unsigned percent)
{
    virtualBattery = percent;
    UpdateTooltip();
}

void UpdateBatteryTip(DWORD percent)
{
    batteryPercent = percent;
    UpdateTooltip();
}

void StopBattery()
{
    KillTimer(owner, BatteryTick);
    if (batteryPipe) CloseHandle(batteryPipe);
    if (batteryProcess) {
        // A replaced read-only provider may be waiting in HID I/O.
        if (WaitForSingleObject(batteryProcess, 0) == WAIT_TIMEOUT) TerminateProcess(batteryProcess, 0);
        CloseHandle(batteryProcess);
    }
    batteryPipe = batteryProcess = nullptr;
    batteryTarget = {};
    UpdateBatteryTip(101);
}

bool StartBattery(const std::wstring& path, const std::wstring& name, const std::wstring& data, const std::wstring& plugin)
{
    if (batteryProcess && WaitForSingleObject(batteryProcess, 0) == WAIT_TIMEOUT
        && plugin == batteryPlugin && name == batteryName && path == batteryPath && data == batteryData) return true;
    StopBattery();
    SECURITY_ATTRIBUTES inherit{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    HANDLE writer = nullptr;
    if (!CreatePipe(&batteryPipe, &writer, &inherit, 0)) return false;
    if (!SetHandleInformation(batteryPipe, HANDLE_FLAG_INHERIT, 0)) {
        CloseHandle(writer);
        StopBattery();
        return false;
    }
    HANDLE nullFile = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                 &inherit, OPEN_EXISTING, 0, nullptr);
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = startup.hStdError = nullFile;
    startup.hStdOutput = writer;
    PROCESS_INFORMATION process{};
    std::wstring command;
    AppendArgument(command, path.c_str());
    command.erase(0, 1);
    command += L" " + std::to_wstring(GetCurrentProcessId());
    if (!data.empty()) AppendArgument(command, data.c_str());
    const bool started = nullFile != INVALID_HANDLE_VALUE && CreateProcessW(path.c_str(), command.data(), nullptr,
        nullptr, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, nullptr, &startup, &process);
    CloseHandle(writer);
    if (nullFile != INVALID_HANDLE_VALUE) CloseHandle(nullFile);
    if (!started) { StopBattery(); return false; }
    batteryProcess = process.hProcess;
    SetEfficiency(batteryProcess, efficiencyEnabled);
    ResumeThread(process.hThread);
    CloseHandle(process.hThread);
    batteryName = name;
    batteryPlugin = plugin;
    batteryPath = path;
    batteryData = data;
    batteryWithTarget = !data.empty();
    SetTimer(owner, BatteryTick, 1000, nullptr);
    return true;
}

void TrayMenu()
{
    const auto menu = CreatePopupMenu();
    if (!menu) return;
    AppendMenuW(menu, MF_STRING, OpenMain, L"打开 HHub");
    AppendMenuW(menu, MF_STRING, ExitTray, L"退出");
    POINT pointer{};
    GetCursorPos(&pointer);
    SetForegroundWindow(owner);
    const auto command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                        pointer.x, pointer.y, 0, owner, nullptr);
    PostMessageW(owner, WM_NULL, 0, 0);
    DestroyMenu(menu);

    if (command == OpenMain) RequestOpen();
    if (command == ExitTray) DestroyWindow(owner);
}

void RefreshStatus()
{
    const auto now = GetTickCount64();
    if (lastStatusRequest && now - lastStatusRequest < 12000) return;
    // The desktop publishes current device data itself when that page is active.
    if (!statusRows.empty() && std::all_of(statusRows.begin(), statusRows.end(), [now](const auto& row) {
        return now - row.second.readAt < 12000;
    })) return;
    lastStatusRequest = now;
    InitializeTrayServices();
}

void ShowTooltip(int x, int y)
{
    UpdateTooltip();
    RefreshStatus();
    if (UsesShellTooltip()) return;
    tipVisible = true;
    SendMessageW(tooltip, TTM_TRACKPOSITION, 0, MAKELPARAM(x, y));
    SendMessageW(tooltip, TTM_TRACKACTIVATE, TRUE, reinterpret_cast<LPARAM>(&tool));
    SetTimer(owner, HoverTick, 1000, nullptr);
}

void HideTooltip()
{
    tipVisible = false;
    KillTimer(owner, HoverTick);
    SendMessageW(tooltip, TTM_TRACKACTIVATE, FALSE, reinterpret_cast<LPARAM>(&tool));
}

bool ReceiveStatus(std::wistringstream& lines, bool replace)
{
    std::wstring plugin, line;
    if (!std::getline(lines, plugin) || plugin.empty()) return false;
    if (replace) for (auto row = statusRows.begin(); row != statusRows.end();)
        row = row->first.first == plugin ? statusRows.erase(row) : std::next(row);
    for (unsigned i = 0; i < 24 && std::getline(lines, line); ++i) {
        std::wistringstream fields(line);
        std::wstring target, name, battery, variables;
        if (!std::getline(fields, target, L'\t') || target.empty()) continue;
        auto key = std::make_pair(plugin, target);
        if (!std::getline(fields, name, L'\t') || name.empty()) { statusRows.erase(key); continue; }
        std::getline(fields, battery, L'\t');
        std::getline(fields, variables);
        if (statusRows.size() < 96 || statusRows.count(key))
            statusRows[key] = {name, battery, variables, GetTickCount64()};
    }
    return true;
}

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (taskbarCreated && message == taskbarCreated && shellReady) AddIcon();
    if (message == RefreshEfficiency) { UpdateEfficiency(); return 0; }
    if (message == OpenWindow) {
        RequestOpen();
        return 0;
    }
    if (message == WM_TIMER && wparam == RetryOpen) {
        if (++retryCount <= 40) OpenDesktop();
        else KillTimer(owner, RetryOpen);
        return 0;
    }
    if (message == WM_TIMER && wparam == StartupTick) {
        if (!StartShell()) DestroyWindow(window);
        else if (delayedShowDesktop) OpenDesktop();
        else InitializeTrayServices();
        return 0;
    }
    if (message == WM_TIMER && wparam == DesktopLifetimeTick) {
        if (!desktop.hProcess || WaitForSingleObject(desktop.hProcess, 0) != WAIT_TIMEOUT) {
            KillTimer(owner, DesktopLifetimeTick);
            CloseProcessHandle();
            UpdateEfficiency();
        }
        return 0;
    }
    if (message == WM_TIMER && wparam == HoverTick) {
        UpdateTooltip();
        RefreshStatus();
        return 0;
    }
    if (message == WM_TIMER && wparam == BatteryTick) {
        DWORD available = 0;
        struct Sample { DWORD value; std::array<BYTE, 32> target; } sample{101, {}};
        static_assert(sizeof(Sample) == 36);
        const DWORD size = batteryWithTarget ? sizeof(sample) : sizeof(sample.value);
        bool haveReading = false;
        for (int i = 0; i < 16; ++i) {
            if (!PeekNamedPipe(batteryPipe, nullptr, 0, nullptr, &available, nullptr)) {
                StopBattery();
                return 0;
            }
            if (available < size) break;
            DWORD bytes = 0;
            if (!ReadFile(batteryPipe, &sample, size, &bytes, nullptr) || bytes != size) {
                StopBattery();
                return 0;
            }
            haveReading = true;
        }
        if (haveReading) {
            batteryTarget = sample.target;
            UpdateBatteryTip(sample.value <= (batteryWithTarget ? 203 : 100) ? sample.value : 101);
        }
        return 0;
    }
    if (message == RememberPage && wparam == desktop.dwProcessId && lparam >= 0 && lparam <= 3) {
        lastPage = static_cast<int>(lparam);
        return 0;
    }
    if (message == ExitWithDesktop && wparam != 0 && wparam == desktop.dwProcessId) {
        DestroyWindow(window);
        return 0;
    }
    if (message == WM_COPYDATA) {
        const auto data = reinterpret_cast<const COPYDATASTRUCT*>(lparam);
        if (data && data->dwData == ReadDeviceBattery) {
            if (!batteryWithTarget || batteryPercent == 101 || !data->lpData || data->cbData != batteryTarget.size()
                || memcmp(data->lpData, batteryTarget.data(), batteryTarget.size()) != 0) return 0;
            return batteryPercent + 1;
        }
        if (!data || !data->lpData
            || data->cbData == 0 || data->cbData % sizeof(wchar_t) != 0
            || data->cbData > 32767 * sizeof(wchar_t)) return FALSE;
        const auto args = static_cast<const wchar_t*>(data->lpData);
        const auto length = data->cbData / sizeof(wchar_t);
        if (args[length - 1] != L'\0') return FALSE;
        if (data->dwData == BatteryProvider || (data->dwData >= DeviceStatus && data->dwData <= UpdateDeviceStatus)) {
            DWORD sender = 0;
            GetWindowThreadProcessId(reinterpret_cast<HWND>(wparam), &sender);
            const bool fromDesktop = desktop.hProcess && sender == desktop.dwProcessId
                && WaitForSingleObject(desktop.hProcess, 0) == WAIT_TIMEOUT;
            const bool fromTrayServices = trayServices && sender == trayServicesPid
                && WaitForSingleObject(trayServices, 0) == WAIT_TIMEOUT;
            if (!sender || (!fromDesktop && !fromTrayServices)) return FALSE;
            const std::wstring payload(args, length - 1);
            std::wistringstream lines(payload);
            if (data->dwData == BatteryProvider) {
                std::wstring path, folder, plugin, name;
                if (!std::getline(lines, path) || path.empty() || !std::getline(lines, folder)
                    || !std::getline(lines, plugin) || plugin.empty() || !std::getline(lines, name) || name.empty()) return FALSE;
                return StartBattery(path, name, folder, plugin);
            }
            if (data->dwData == DeviceStatus || data->dwData == UpdateDeviceStatus) {
                if (!ReceiveStatus(lines, data->dwData == DeviceStatus)) return FALSE;
            } else {
                if (payload.empty() || (payload[0] != L'0' && payload[0] != L'1')) return FALSE;
                const bool enabled = payload[0] == L'1';
                if (data->dwData == PluginVisibility) {
                    auto id = payload.substr(1);
                    if (enabled) hiddenBattery.erase(id);
                    else hiddenBattery.insert(id);
                } else {
                    showBattery = enabled;
                    if (data->dwData == LoadVisibility) {
                        hiddenBattery.clear();
                        std::wstring id;
                        std::getline(lines, id);
                        while (std::getline(lines, id)) if (!id.empty()) hiddenBattery.insert(id);
                    }
                }
            }
            UpdateTooltip();
            return TRUE;
        }
        if (data->dwData != DesktopArguments) return FALSE;
        desktopArguments.assign(args, length - 1);
        return TRUE;
    }
    if (message == VirtualBattery && wparam <= 100) SetVirtualBatteryTip(static_cast<unsigned>(wparam));
    if (message == TrayCallback) {
        const auto event = LOWORD(lparam);
        if (event == WM_MOUSEMOVE) {
            UpdateTooltip();
            RefreshStatus();
        }
        if (event == NIN_POPUPOPEN) ShowTooltip(static_cast<short>(LOWORD(wparam)), static_cast<short>(HIWORD(wparam)));
        if (event == NIN_POPUPCLOSE) HideTooltip();
        if (event == WM_CONTEXTMENU) { HideTooltip(); TrayMenu(); }
        if (event == NIN_SELECT || event == NIN_KEYSELECT) { HideTooltip(); RequestOpen(); }
        return 0;
    }
    if (message == WM_DESTROY) {
        SetPropW(window, TrayExitProperty, reinterpret_cast<HANDLE>(1));
        HideTooltip();
        if (tooltip) DestroyWindow(tooltip);
        StopBattery();
        if (iconAdded) Shell_NotifyIconW(NIM_DELETE, &icon);
        if (const auto ui = DesktopWindow()) PostMessageW(ui, WM_CLOSE, 0, 0);
        CloseProcessHandle();
        if (trayServices) CloseHandle(trayServices);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    int count = 0;
    const auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (arguments && count == 3 && wcscmp(arguments[1], L"--close-installation") == 0) {
        std::wstring folder(arguments[2]);
        LocalFree(arguments);
        return CloseInstallation(folder);
    }
    bool showDesktop = true;
    bool startup = false;
    unsigned startupDelay = 15;
    unsigned battery = 101;
    for (int index = 1; arguments && index < count; ++index) {
        if (wcscmp(arguments[index], L"--tray") == 0) showDesktop = false;
        else if (wcscmp(arguments[index], L"--startup") == 0) startup = true;
        else if (wcscmp(arguments[index], L"--startup-delay") == 0 && index + 1 < count) {
            const auto input = arguments[++index];
            wchar_t* end = nullptr;
            const auto value = wcstol(input, &end, 10);
            if (end != input && *end == L'\0') {
                if (value == 0 || value == 5) startupDelay = 5;
                else if (value == 10) startupDelay = 10;
                else if (value == 15 || value == 30 || value == 60) startupDelay = 15;
            }
        }
        else if (wcscmp(arguments[index], L"--virtual-battery") == 0 && index + 1 < count) {
            wchar_t* end = nullptr;
            const auto value = wcstol(arguments[++index], &end, 10);
            if (end != arguments[index] && *end == L'\0' && value >= 0 && value <= 100)
                battery = static_cast<unsigned>(value);
        } else AppendArgument(desktopArguments, arguments[index]);
    }
    LocalFree(arguments);

    const auto folder = ApplicationFolder();
    if (folder.empty()) return 1;
    const auto manifestAttributes = GetFileAttributesW((folder + L"hhub-installed.marker").c_str());
    const bool installed = manifestAttributes != INVALID_FILE_ATTRIBUTES && !(manifestAttributes & FILE_ATTRIBUTE_DIRECTORY);
    const auto className = installed ? InstalledWindowClass : DevelopmentWindowClass;
    if (installed && desktopArguments.empty()) AppendArgument(desktopArguments, L"--startup-profile");

    // Hold the coordinator's mutex for its lifetime. Installed shortcuts must reach the installed copy.
    const auto mutexName = std::wstring(L"Local\\") + className;
    const auto mutex = CreateMutexW(nullptr, FALSE, mutexName.c_str());
    if (!mutex) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (startup) { CloseHandle(mutex); return 0; }
        HWND existing = nullptr;
        for (int attempt = 0; !existing && attempt < 25; ++attempt) {
            existing = FindWindowW(className, nullptr);
            if (!existing) Sleep(40);
        }
        if (existing) {
            if (!desktopArguments.empty()) {
                COPYDATASTRUCT data{DesktopArguments,
                    static_cast<DWORD>((desktopArguments.size() + 1) * sizeof(wchar_t)), desktopArguments.data()};
                SendMessageTimeoutW(existing, WM_COPYDATA, 0, reinterpret_cast<LPARAM>(&data),
                                    SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, nullptr);
            }
            if (battery <= 100) PostMessageW(existing, VirtualBattery, battery, 0);
            if (showDesktop) {
                DWORD processId = 0;
                GetWindowThreadProcessId(existing, &processId);
                AllowSetForegroundWindow(processId);
                PostMessageW(existing, OpenWindow, 0, 0);
            }
        }
        CloseHandle(mutex);
        return existing ? 0 : 1;
    }

    SetCurrentProcessExplicitAppUserModelID(L"HHub.Desktop");
    const auto jobName = L"Local\\Hhub.Runtime." + std::to_wstring(GetCurrentProcessId());
    HANDLE runtimeJob = CreateJobObjectW(nullptr, jobName.c_str());
    if (runtimeJob && AssignProcessToJobObject(runtimeJob, GetCurrentProcess()))
        SetEnvironmentVariableW(L"HHUB_RUNTIME_JOB", jobName.c_str());
    else
        SetEnvironmentVariableW(L"HHUB_RUNTIME_JOB", nullptr);

    taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = className;
    if (!RegisterClassW(&windowClass)) return 1;
    owner = CreateWindowExW(WS_EX_TOOLWINDOW, className, L"", WS_POPUP,
                            0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!owner) return 1;

    icon.cbSize = sizeof(icon);
    icon.hWnd = owner;
    icon.uID = 1;
    icon.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
    icon.uCallbackMessage = TrayCallback;
    icon.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1));
    if (!icon.hIcon) icon.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_WIN95_CLASSES};
    InitCommonControlsEx(&controls);
    tooltip = CreateWindowExW(WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, TOOLTIPS_CLASSW,
        nullptr, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX, 0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    tool.cbSize = sizeof(tool);
    tool.uFlags = TTF_TRACK;
    tool.hwnd = owner;
    tool.uId = 1;
    tool.lpszText = const_cast<wchar_t*>(L"暂无设备状态");
    if (tooltip && !SendMessageW(tooltip, TTM_ADDTOOLW, 0, reinterpret_cast<LPARAM>(&tool))) {
        DestroyWindow(tooltip);
        tooltip = nullptr;
    }
    if (tooltip) SendMessageW(tooltip, TTM_SETMAXTIPWIDTH, 0, 480);
    SetVirtualBatteryTip(battery);

    UpdateEfficiency();
    delayedShowDesktop = showDesktop;
    if (startup && startupDelay && SetTimer(owner, StartupTick, startupDelay * 1000, nullptr)) {
        // The native coordinator remains responsive to manual launches during the delay.
    } else if (!StartShell()) {
        DestroyWindow(owner);
        return 1;
    } else if (showDesktop) OpenDesktop();
    else InitializeTrayServices();

    MSG message{};
    BOOL status;
    while ((status = GetMessageW(&message, nullptr, 0, 0)) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    CloseHandle(mutex);
    if (runtimeJob) CloseHandle(runtimeJob);
    return status < 0 ? 1 : static_cast<int>(message.wParam);
}
