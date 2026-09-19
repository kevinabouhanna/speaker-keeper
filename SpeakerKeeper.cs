using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// These become the Win32 version resource, which is what Windows shows as the
// app's name in Task Manager. It is also the name the UAC prompt shows, which
// is why the uninstaller build gets its own title.
#if UNINSTALLER
[assembly: AssemblyTitle("Speaker Keeper Uninstaller")]
#elif INSTALLER
[assembly: AssemblyTitle("Speaker Keeper Setup")]
#else
[assembly: AssemblyTitle("Speaker Keeper")]
#endif
[assembly: AssemblyProduct("Speaker Keeper")]
[assembly: AssemblyDescription("Keeps a Bluetooth speaker awake with a silent audio stream")]
[assembly: AssemblyCompany("Kevin Abou Hanna")]
[assembly: AssemblyCopyright("Copyright (c) Kevin Abou Hanna")]
[assembly: AssemblyVersion("1.3.1.0")]
[assembly: AssemblyFileVersion("1.3.1.0")]

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorClass { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IntPtr store);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
}

// WASAPI. The app renders its own silence through these rather than looping a file
// through a media player, because a media player is visible to the user: it publishes
// a transport session, which Windows shows as a "Speaker Keeper" card with
// play/next/previous in the media flyout and then routes media keys to. See Silence.
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                   IntPtr format, ref Guid sessionGuid);
    int GetBufferSize(out uint frames);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out uint frames);
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    int GetMixFormat(out IntPtr format);
    int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr handle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient
{
    int GetBuffer(uint frames, out IntPtr data);
    int ReleaseBuffer(uint frames, int flags);
}

[StructLayout(LayoutKind.Sequential)]
struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;
    public DEVPROPKEY(string guid, uint p) { fmtid = new Guid(guid); pid = p; }
}

// Reads device-node properties through cfgmgr32. Used to find the battery level
// of whichever Bluetooth device is currently the default audio output.
//
// The trick is that the audio endpoint and the node that actually reports battery
// are different devnodes - the endpoint is SWD\MMDEVAPI\..., while the battery
// lives on the Hands-Free AG node under BTHENUM. They are tied together by a
// shared ContainerId, which is what we match on.
static class DeviceProps
{
    const uint CR_SUCCESS = 0;
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;

    static readonly DEVPROPKEY ContainerId   = new DEVPROPKEY("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    static readonly DEVPROPKEY BluetoothBatt = new DEVPROPKEY("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
    // Undocumented, but consistently present next to the battery value: FILETIME of the
    // last reading the device pushed.
    static readonly DEVPROPKEY BluetoothBattUpdated = new DEVPROPKEY("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 7);
    static readonly DEVPROPKEY FriendlyName  = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key,
        out uint propType, IntPtr buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_ListW(string filter, IntPtr buffer, uint bufferLen, uint flags);

    static byte[] GetProperty(string instanceId, DEVPROPKEY key)
    {
        uint devInst;
        if (CM_Locate_DevNodeW(out devInst, instanceId, 0) != CR_SUCCESS) return null;

        uint type, size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out type, IntPtr.Zero, ref size, 0);
        if (size == 0) return null;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0) != CR_SUCCESS)
                return null;
            var managed = new byte[size];
            Marshal.Copy(buf, managed, 0, (int)size);
            return managed;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string[] DeviceIds(string enumerator)
    {
        uint len;
        if (CM_Get_Device_ID_List_SizeW(out len, enumerator, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS || len == 0)
            return new string[0];

        IntPtr buf = Marshal.AllocHGlobal((int)len * 2);
        try
        {
            if (CM_Get_Device_ID_ListW(enumerator, buf, len, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS)
                return new string[0];
            // REG_MULTI_SZ: null-separated, double-null terminated
            return Marshal.PtrToStringUni(buf, (int)len)
                          .Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string EndpointName(string endpointId)
    {
        var raw = GetProperty("SWD" + (char)92 + "MMDEVAPI" + (char)92 + endpointId, FriendlyName);
        if (raw == null) return null;
        return System.Text.Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    /// <summary>One selectable audio output, as shown in the Settings device list.</summary>
    public class AudioDevice
    {
        public string EndpointId;      // "{0.0.0.00000000}.{guid}"
        public string Name;
        public Guid Container;         // stable per physical device - what settings key off
        public bool IsBluetooth;
        public bool Present = true;
    }

    public static bool TryContainer(string endpointId, out Guid container)
    {
        container = Guid.Empty;
        var raw = GetProperty(EndpointInstanceId(endpointId), ContainerId);
        if (raw == null || raw.Length < 16) return false;
        container = new Guid(raw);
        return true;
    }

    static string EndpointInstanceId(string endpointId)
    {
        return "SWD" + (char)92 + "MMDEVAPI" + (char)92 + endpointId;
    }

    /// <summary>
    /// Bluetooth devices by ContainerId, with the name of the physical device.
    ///
    /// One speaker publishes several nodes - A2DP, Hands-Free, AVRCP - that all share a
    /// container. The name is taken from the device root node ("BTHENUM\DEV_...", whose
    /// FriendlyName is "Xiaomi Sound Pocket") in preference to a per-profile node
    /// ("... Hands-Free AG"), so the UI shows the speaker rather than one of its profiles.
    /// </summary>
    public static Dictionary<Guid, string> BluetoothDevices()
    {
        var map = new Dictionary<Guid, string>();
        var fromRoot = new HashSet<Guid>();

        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHHFENUM" })
            foreach (var id in DeviceIds(enumerator))
            {
                var c = GetProperty(id, ContainerId);
                if (c == null || c.Length < 16) continue;
                var container = new Guid(c);

                bool isRoot = id.IndexOf((char)92 + "DEV_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (map.ContainsKey(container) && (fromRoot.Contains(container) || !isRoot)) continue;

                var nameRaw = GetProperty(id, FriendlyName);
                string name = nameRaw == null
                    ? null
                    : System.Text.Encoding.Unicode.GetString(nameRaw).TrimEnd('\0');
                if (string.IsNullOrEmpty(name)) continue;

                map[container] = name;
                if (isRoot) fromRoot.Add(container);
            }

        return map;
    }

    /// <summary>ContainerIds of everything currently attached over Bluetooth.</summary>
    public static HashSet<Guid> BluetoothContainers()
    {
        var set = new HashSet<Guid>();
        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHHFENUM" })
            foreach (var id in DeviceIds(enumerator))
            {
                var c = GetProperty(id, ContainerId);
                if (c != null && c.Length >= 16) set.Add(new Guid(c));
            }
        return set;
    }

    /// <summary>
    /// Every render (playback) endpoint currently present. Enumerated through cfgmgr32
    /// rather than IMMDeviceEnumerator so it reuses the same property plumbing as the
    /// battery lookup - render endpoints are the SWD\MMDEVAPI nodes whose id carries the
    /// {0.0.0.*} data-flow prefix ({0.0.1.*} would be capture).
    /// </summary>
    public static List<AudioDevice> RenderEndpoints()
    {
        var bt = BluetoothContainers();
        var list = new List<AudioDevice>();

        foreach (var id in DeviceIds("SWD"))
        {
            int cut = id.IndexOf("MMDEVAPI" + (char)92, StringComparison.OrdinalIgnoreCase);
            if (cut < 0) continue;

            string endpointId = id.Substring(cut + 9);
            if (!endpointId.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase)) continue;

            var dev = new AudioDevice();
            dev.EndpointId = endpointId;

            var nameRaw = GetProperty(id, FriendlyName);
            dev.Name = nameRaw == null
                ? endpointId
                : System.Text.Encoding.Unicode.GetString(nameRaw).TrimEnd('\0');

            var c = GetProperty(id, ContainerId);
            if (c != null && c.Length >= 16) dev.Container = new Guid(c);

            dev.IsBluetooth = dev.Container != Guid.Empty && bt.Contains(dev.Container);
            list.Add(dev);
        }

        return list;
    }

    /// <summary>Battery percent of the default output device, or -1 when it doesn't report one.</summary>
    public static int BatteryPercent(string endpointId)
    {
        return Battery(endpointId).Percent;
    }

    /// <summary>A battery reading plus when the device last pushed it.</summary>
    public class BatteryInfo
    {
        public int Percent = -1;
        public DateTime UpdatedAt = DateTime.MinValue;
    }

    /// <summary>
    /// Battery of the given output device.
    ///
    /// PID 7 next to the percentage is when the device last pushed a reading. Bluetooth
    /// battery is reported by the device rather than polled, so a value can sit unchanged
    /// for many minutes; the timestamp is what separates "still says 90%" from "just
    /// reported 90% again", which matters when reasoning about rate of change.
    /// </summary>
    public static BatteryInfo Battery(string endpointId)
    {
        var info = new BatteryInfo();

        var raw = GetProperty("SWD" + (char)92 + "MMDEVAPI" + (char)92 + endpointId, ContainerId);
        if (raw == null || raw.Length < 16) return info;
        var container = new Guid(raw);

        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHHFENUM" })
            foreach (var id in DeviceIds(enumerator))
            {
                var batt = GetProperty(id, BluetoothBatt);
                if (batt == null || batt.Length < 1) continue;

                var c = GetProperty(id, ContainerId);
                if (c == null || c.Length < 16 || new Guid(c) != container) continue;

                info.Percent = batt[0];

                var stamp = GetProperty(id, BluetoothBattUpdated);
                if (stamp != null && stamp.Length >= 8)
                {
                    try { info.UpdatedAt = DateTime.FromFileTime(BitConverter.ToInt64(stamp, 0)); }
                    catch { }
                }
                return info;
            }

        return info;
    }
}

/// <summary>
/// Where this build came from, and how to point a user at what changed.
///
/// Auto-updates are silent by design - a SYSTEM task swaps the binaries overnight and
/// nobody is prompted. That is only tolerable if the user can find out afterwards what
/// landed on their machine, so the version and these links are surfaced in Settings and
/// in the post-update notification.
/// </summary>
static class Project
{
    public const string Repo = "https://github.com/kevinabouhanna/speaker-keeper";
    public const string ChangelogUrl = Repo + "/blob/main/CHANGELOG.md";

    /// <summary>"1.2.0" - the three-part form used for tags and in the changelog.</summary>
    public static string ShortVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }
    }

    /// <summary>
    /// Release notes for a version. Falls back to the changelog when no version is
    /// known, which is what a locally-built exe would hit before its tag exists.
    /// </summary>
    public static string ReleaseNotesUrl(string version)
    {
        if (string.IsNullOrEmpty(version)) return ChangelogUrl;
        return Repo + "/releases/tag/v" + version;
    }

    public static void Open(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url);
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}

/// <summary>
/// Preferences, kept in HKCU so they survive the folder being moved and so the app
/// works the same if it is ever installed somewhere non-writable.
/// </summary>
static class Settings
{
    const string Key = "Software" + "\\" + "SpeakerKeeper";
    const string RunKey = "Software" + "\\" + "Microsoft" + "\\" + "Windows" + "\\" + "CurrentVersion" + "\\" + "Run";
    const string RunValue = "Speaker Keeper";

    static T Read<T>(string name, T fallback)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return fallback;
                var v = k.GetValue(name);
                if (v == null) return fallback;
                return (T)Convert.ChangeType(v, typeof(T));
            }
        }
        catch { return fallback; }
    }

    static void Write(string name, object value)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                if (k != null) k.SetValue(name, value);
        }
        catch { }
    }

    public static bool WarnLowBattery
    {
        get { return Read("WarnLowBattery", 1) != 0; }
        set { Write("WarnLowBattery", value ? 1 : 0); }
    }

    /// <summary>
    /// The version that last ran for this user, so a silent auto-update can be told
    /// apart from an ordinary launch.
    ///
    /// Per-user rather than machine-wide on purpose: the update is machine-wide, but
    /// the *notice* is per-person, so each account that signs in gets told once rather
    /// than only whoever happened to log in first.
    ///
    /// Empty on a first-ever run, which is deliberately NOT treated as an update.
    /// </summary>
    public static string LastRunVersion
    {
        get { return Read("LastRunVersion", ""); }
        set { Write("LastRunVersion", value); }
    }

    public static int LowBatteryThreshold
    {
        get
        {
            int v = Read("LowBatteryThreshold", 20);
            return v < 5 ? 5 : (v > 95 ? 95 : v);
        }
        set { Write("LowBatteryThreshold", value); }
    }

    /// <summary>
    /// Autostart via the HKCU Run key rather than a Startup shortcut - no COM needed
    /// to toggle it, and it still shows up in Task Manager's Startup tab.
    /// </summary>
    public static bool RunAtLogin
    {
        get
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (value) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Whether the SYSTEM scheduled task that installs updates exists.
    ///
    /// This is machine-wide state in HKLM, so toggling it needs elevation - see
    /// <see cref="Updater.SetAutoUpdateElevated"/>. Reading it does not.
    /// </summary>
    public static bool AutoUpdate
    {
        get
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineKey))
                {
                    if (k == null) return false;
                    var v = k.GetValue("AutoUpdate");
                    return v != null && Convert.ToInt32(v) != 0;
                }
            }
            catch { return false; }
        }
    }

    public const string MachineKey = "Software" + "\\" + "SpeakerKeeper";

    /// <summary>Where the updater looks for its release manifest. Set at install time.</summary>
    public static string UpdateUrl
    {
        get
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineKey))
                    return k == null ? null : k.GetValue("UpdateUrl") as string;
            }
            catch { return null; }
        }
    }

    /// <summary>Repoints a stale autostart entry after the folder has been moved.</summary>
    public static void RepairRunPath()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                var cur = k.GetValue(RunValue) as string;
                if (cur == null) return;
                string want = "\"" + Application.ExecutablePath + "\"";
                if (!string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))
                    k.SetValue(RunValue, want);
            }
        }
        catch { }
    }
}

/// <summary>
/// Removal logic for the machine-wide install. The uninstall list entry itself is
/// written at install time by install.ps1, since HKLM needs elevation and the app
/// deliberately runs unelevated.
/// </summary>
static class Installer
{
    // public: Setup.cs writes this same key, and the two must not drift.
    public const string UninstallKey =
        "Software" + "\\" + "Microsoft" + "\\" + "Windows" + "\\" + "CurrentVersion" + "\\" + "Uninstall" + "\\" + "SpeakerKeeper";
    const string RunKey =
        "Software" + "\\" + "Microsoft" + "\\" + "Windows" + "\\" + "CurrentVersion" + "\\" + "Run";
    const string SettingsKey = "Software" + "\\" + "SpeakerKeeper";
    const string NotifyKey = "Control Panel" + "\\" + "NotifyIconSettings";

    public static void Uninstall(bool quiet)
    {
        // Works whether this runs from SpeakerKeeper.exe or Uninstall.exe.
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string mainExe = Path.Combine(dir, "SpeakerKeeper.exe");

        if (!quiet)
        {
            var answer = MessageBox.Show(
                "Remove Speaker Keeper?\n\nThis stops the keep-alive, removes its settings and "
                    + "autostart entry, and deletes:\n" + dir,
                "Uninstall Speaker Keeper",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        // Stop any running copy so the folder isn't locked.
        try
        {
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("SpeakerKeeper"))
            {
                if (p.Id == me) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }
        catch { }

        // Drop the SYSTEM update task before anything else, so it can't fire at a
        // half-removed install.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe",
                "/Delete /F /TN \"" + Updater.TaskName + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (var p = System.Diagnostics.Process.Start(psi)) p.WaitForExit(15000);
        }
        catch { }

        TryDelete(RunKey, "Speaker Keeper");
        TryDeleteTree(SettingsKey);
        TryDeleteMachineTree(SettingsKey);          // HKLM: UpdateUrl / AutoUpdate
        TryDeleteMachineTree(UninstallKey);
        RemoveNotifyIconEntry(mainExe);

        // Updater log lives in ProgramData, outside the install folder.
        try
        {
            string pd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Speaker Keeper");
            if (Directory.Exists(pd)) Directory.Delete(pd, true);
        }
        catch { }

        // Per-user data (the log) lives outside the install folder.
        try
        {
            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Speaker Keeper");
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        catch { }

        // The exe lives inside the folder being removed, so it can't delete itself.
        // Hand the job to a detached cmd that waits for this process to exit first.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c timeout /t 3 /nobreak >nul & rd /s /q \"" + dir + "\"";
            psi.WorkingDirectory = Path.GetPathRoot(Environment.SystemDirectory);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            System.Diagnostics.Process.Start(psi);
        }
        catch { }

        if (!quiet)
            MessageBox.Show("Speaker Keeper has been removed.", "Uninstall Speaker Keeper",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void TryDelete(string key, string value)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true))
                if (k != null && k.GetValue(value) != null) k.DeleteValue(value);
        }
        catch { }
    }

    static void TryDeleteTree(string key)
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        catch { }
    }

    static void TryDeleteMachineTree(string key)
    {
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(key, false); }
        catch { }
    }

    /// <summary>
    /// Drops the tray-icon visibility preference Windows stores per executable.
    ///
    /// Windows records the path with a KNOWNFOLDERID prefix rather than a literal one -
    /// "{6D809377-...}\Speaker Keeper\SpeakerKeeper.exe" for Program Files - so comparing
    /// whole paths never matches. Compare the trailing segments instead.
    /// </summary>
    static void RemoveNotifyIconEntry(string exe)
    {
        string want = Tail(exe);

        try
        {
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NotifyKey, true))
            {
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using (var sub = root.OpenSubKey(name))
                        {
                            if (sub == null) continue;
                            var path = sub.GetValue("ExecutablePath") as string;
                            if (path == null ||
                                !string.Equals(Tail(path), want, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        root.DeleteSubKeyTree(name, false);
                        return;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>Last two path segments, e.g. "Speaker Keeper\SpeakerKeeper.exe".</summary>
    static string Tail(string path)
    {
        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return path;
        return parts[parts.Length - 2] + "\\" + parts[parts.Length - 1];
    }
}

/// <summary>
/// Turns the auto-update scheduled task on and off.
///
/// The task runs update.ps1 as SYSTEM, which is the whole point: replacing files in
/// Program Files needs privileges the tray app deliberately does not have. That is how
/// Chrome and Edge update without prompting - a privileged helper installed once, rather
/// than an elevation prompt per update.
///
/// Creating or removing the task is itself a machine-wide change, so toggling the
/// checkbox raises one UAC prompt. Everything after that is silent.
/// </summary>
static class Updater
{
    public const string TaskName = "Speaker Keeper Update";

    /// <summary>
    /// Re-launches this exe elevated to do the actual work. Returns false if the user
    /// dismissed the UAC prompt.
    /// </summary>
    public static bool SetAutoUpdateElevated(bool enable)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = Application.ExecutablePath;
            psi.Arguments = "--set-autoupdate " + (enable ? "on" : "off");
            psi.UseShellExecute = true;
            psi.Verb = "runas";              // forces the UAC prompt regardless of manifest
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

            var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(60000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;                    // cancelled at the UAC prompt
        }
    }

    /// <summary>Runs elevated, from --set-autoupdate. Creates or deletes the task.</summary>
    public static int Apply(bool enable)
    {
        return Apply(enable, Path.GetDirectoryName(Application.ExecutablePath));
    }

    /// <summary>
    /// Same, but told explicitly where the install lives.
    ///
    /// Setup needs this overload. It is already elevated so it can create the task
    /// directly, but it runs from wherever the user saved Install.exe - not from the
    /// install folder. Deriving the path from Application.ExecutablePath there would
    /// point a SYSTEM task at an update.ps1 in the Downloads folder.
    /// </summary>
    public static int Apply(bool enable, string installDir)
    {
        try
        {
            string dir = installDir;
            string script = Path.Combine(dir, "update.ps1");

            if (enable)
            {
                if (!File.Exists(script)) return 2;

                // Daily, plus once at startup. /RL HIGHEST + /RU SYSTEM is what makes
                // the update itself silent.
                string args = "/Create /F /TN \"" + TaskName + "\""
                    + " /TR \"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \\\"" + script + "\\\"\""
                    + " /SC DAILY /ST 03:00 /RU SYSTEM /RL HIGHEST";
                if (Run("schtasks.exe", args) != 0) return 3;
            }
            else
            {
                Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"");
            }

            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Settings.MachineKey))
                if (k != null)
                    k.SetValue("AutoUpdate", enable ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);

            return 0;
        }
        catch { return 1; }
    }

    static int Run(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return p.ExitCode;
        }
    }
}

/// <summary>
/// Entry point for Uninstall.exe. Built from this same source with /main:UninstallProgram,
/// so there is one copy of the removal logic rather than a script that can drift from it.
/// Its manifest requests administrator, because a machine-wide install lives in
/// Program Files and registers under HKLM.
/// </summary>
static class UninstallProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();

        bool quiet = false;
        foreach (var a in args)
            if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)) quiet = true;

        Installer.Uninstall(quiet);
    }
}

/// <summary>
/// Live view of the log file. Tails it the way `tail -f` would, rather than handing a
/// static snapshot to Notepad, so you can watch the app react to device changes as they
/// happen.
/// </summary>
class LogWindow : Form
{
    // On a long-running install the log outgrows the window; only the tail is interesting.
    const int TailBytes = 256 * 1024;

    readonly string _path;
    readonly TextBox _view;
    readonly ToggleSwitch _follow;
    readonly Note _status;
    readonly System.Windows.Forms.Timer _timer;
    long _pos;

    public LogWindow(Icon icon, string path)
    {
        _path = path;

        Text = "Speaker Keeper Log";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 460);
        MinimumSize = new Size(520, 320);
        Font = Fluent.Body;
        BackColor = Fluent.Window;
        Padding = new Padding(1, 0, 1, 0);

        // Added before the bottom bar: docking runs in reverse z-order, so the control
        // added first ends up filling whatever space the docked bars leave.
        _view = new TextBox();
        _view.Multiline = true;
        _view.ReadOnly = true;
        _view.ScrollBars = ScrollBars.Both;
        _view.WordWrap = false;
        _view.Dock = DockStyle.Fill;
        _view.BorderStyle = BorderStyle.None;
        // ReadOnly renders grey unless the colours are set explicitly, and the default
        // white would be a slab of glare in the middle of a dark window.
        _view.BackColor = Fluent.Dark ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
        _view.ForeColor = Fluent.Text;
        _view.Font = Monospace();
        Controls.Add(_view);

        // Everything below is docked rather than positioned: a Panel is still its default
        // 200px wide while its children are being added, so any layout computed from
        // bar.Width here would be wrong once docking resizes it.
        var bar = new Panel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 76;
        bar.BackColor = Fluent.Window;
        bar.Padding = new Padding(16, 8, 16, 12);

        var row = new Panel();
        row.Dock = DockStyle.Fill;
        row.BackColor = Fluent.Window;

        _follow = new ToggleSwitch();
        _follow.SetSilently(true);
        _follow.Location = new Point(0, 6);
        _follow.Toggled += (s, e) => { if (_follow.On) Poll(); };
        row.Controls.Add(_follow);

        var followLabel = new Note();
        followLabel.Primary = true;
        followLabel.Text = "Follow live";
        followLabel.SetBounds(_follow.Right + 10, 0, 120, 32);
        row.Controls.Add(followLabel);

        var notepad = new FluentButton();
        notepad.Text = "Open in Notepad";
        notepad.Size = new Size(140, 32);
        notepad.Margin = new Padding(8, 0, 0, 0);
        notepad.Click += (s, e) =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(_path);
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        };

        var close = new FluentButton();
        close.Text = "Close";
        close.Size = new Size(104, 32);
        close.Margin = new Padding(8, 0, 0, 0);
        close.Click += (s, e) => Close();

        var buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Right;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;
        buttons.AutoSize = true;
        buttons.BackColor = Fluent.Window;
        buttons.Controls.Add(close);        // right-to-left: rightmost added first
        buttons.Controls.Add(notepad);
        row.Controls.Add(buttons);

        _status = new Note();
        _status.Dock = DockStyle.Top;
        _status.Height = 20;
        _status.Text = _path;

        bar.Controls.Add(row);              // added first, so it docks last and fills
        bar.Controls.Add(_status);
        Controls.Add(bar);

        CancelButton = close;

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = 1000;
        _timer.Tick += (s, e) => Poll();
        _timer.Start();

        // The bars and the text view carry concrete colours rather than reading Fluent as
        // they paint, so they have to be re-coloured rather than merely repainted.
        Fluent.Follow(this, delegate
        {
            BackColor = Fluent.Window;
            bar.BackColor = row.BackColor = buttons.BackColor = Fluent.Window;
            _view.BackColor = Fluent.Dark ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
            _view.ForeColor = Fluent.Text;
            Fluent.Trim(this, false);
            Fluent.DarkScrollbars(_view);
        });

        Poll();
    }

    static Font Monospace()
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Lucida Console" })
        {
            try
            {
                var f = new Font(name, 9f);
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch { }
        }
        return new Font(FontFamily.GenericMonospace, 9f);
    }

    void Poll()
    {
        // Unchecking Follow freezes the view; _pos stays put so re-checking catches up.
        if (!_follow.On) return;

        try
        {
            if (!File.Exists(_path))
            {
                _status.Text = "waiting for " + _path;
                return;
            }

            // ReadWrite sharing matters: the app appends to this file while we read it.
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length < _pos) { _pos = 0; _view.Clear(); }   // truncated or rotated

                if (_pos == 0 && fs.Length > TailBytes)
                    _pos = fs.Length - TailBytes;

                if (fs.Length == _pos)
                {
                    _status.Text = _path + "   -   up to date";
                    return;
                }

                fs.Seek(_pos, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs))
                {
                    string chunk = sr.ReadToEnd();
                    _pos = fs.Length;
                    if (chunk.Length > 0)
                    {
                        _view.AppendText(chunk);     // also parks the caret at the end
                        _status.Text = _path + "   -   updated " + DateTime.Now.ToString("HH:mm:ss");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _status.Text = "read error: " + ex.Message;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fluent.Trim(this, false);
        Fluent.DarkScrollbars(_view);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _timer.Stop(); _timer.Dispose(); } catch { }
        base.OnFormClosed(e);
    }
}

// =============================================================================
// The Windows 11 look, hand-drawn.
//
// WinForms hands out Win32 common controls that have looked the same since 2001:
// square grey boxes with a hard-coded light palette. There is no theme to switch
// to, so every surface the user sees - the flyout, the settings window, the
// toggles - is painted here instead, following the Fluent palette, metrics and
// motion so the app looks like part of the system rather than a relic.
//
// Everything reads its colours from Fluent rather than holding its own, so one
// theme switch repaints the whole app.
// =============================================================================

/// <summary>Palette, type and window trim for the Fluent look.</summary>
static class Fluent
{
    const string ThemeKey = "Software" + "\\" + "Microsoft" + "\\" + "Windows" + "\\"
                          + "CurrentVersion" + "\\" + "Themes" + "\\" + "Personalize";
    const string AccentKey = "Software" + "\\" + "Microsoft" + "\\" + "Windows" + "\\"
                           + "CurrentVersion" + "\\" + "Explorer" + "\\" + "Accent";

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string id);

    // DWMWA_USE_IMMERSIVE_DARK_MODE moved from 19 to 20 in Windows 10 2004. Both are
    // tried: the wrong one is simply rejected, so there is nothing to detect.
    const int DarkModeOld = 19, DarkMode = 20, CornerPreference = 33, BorderColour = 34;
    const int RoundCorners = 2, RoundSmall = 3;

    public static bool Dark { get; private set; }
    public static Color Accent { get; private set; }

    /// <summary>Raised after a theme switch, so open windows can repaint in the new colours.</summary>
    public static event EventHandler Changed;

    // --- surfaces ---------------------------------------------------------------
    public static Color Window { get { return Dark ? Rgb(0x202020) : Rgb(0xF3F3F3); } }
    public static Color Card { get { return Dark ? Rgb(0x2B2B2B) : Rgb(0xFFFFFF); } }
    public static Color CardHover { get { return Dark ? Rgb(0x323232) : Rgb(0xF9F9F9); } }
    public static Color Stroke { get { return Dark ? Rgb(0x373737) : Rgb(0xE5E5E5); } }
    public static Color Divider { get { return Dark ? Rgb(0x303030) : Rgb(0xE9E9E9); } }
    public static Color Subtle { get { return Dark ? Rgb(0x2D2D2D) : Rgb(0xEDEDED); } }
    public static Color SubtleHover { get { return Dark ? Rgb(0x383838) : Rgb(0xE4E4E4); } }

    // --- text -------------------------------------------------------------------
    public static Color Text { get { return Dark ? Rgb(0xFFFFFF) : Rgb(0x1A1A1A); } }
    public static Color TextSecondary { get { return Dark ? Rgb(0xC8C8C8) : Rgb(0x5D5D5D); } }
    public static Color TextTertiary { get { return Dark ? Rgb(0x8B8B8B) : Rgb(0x8A8A8A); } }
    public static Color OnAccent { get { return Dark ? Rgb(0x000000) : Rgb(0xFFFFFF); } }

    // --- type -------------------------------------------------------------------
    // Fluent sizes are in pixels; WinForms wants points, and 96 DPI is 0.75 pt per px.
    // Everything scales from there through the form's own AutoScaleMode.
    public static Font Caption { get; private set; }     // 12px
    public static Font Body { get; private set; }        // 14px
    public static Font BodyStrong { get; private set; }  // 14px semibold
    public static Font Subtitle { get; private set; }    // 20px semibold
    public static Font Title { get; private set; }       // 28px semibold
    public static Font Glyphs { get; private set; }      // icon font, 16px
    public static Font GlyphsSmall { get; private set; } // icon font, 12px

    // Segoe Fluent Icons ships with Windows 11; Windows 10 has the same glyphs under the
    // older name. Codepoints used across the app: gear E713, volume E767, bluetooth E702,
    // power E7E8, download E896, warning E7BA, info E946, document E8A5, folder E8B7,
    // battery E851 (the near-empty one of the E850..E85A ramp).
    public const string IconSettings = "";
    public const string IconVolume = "";
    public const string IconBluetooth = "";
    public const string IconPower = "";
    public const string IconDownload = "";
    public const string IconWarning = "";
    public const string IconBattery = "";
    public const string IconInfo = "";
    public const string IconDocument = "";
    public const string IconFolder = "";
    public const string IconMinus = "";
    public const string IconPlus = "";

    static Fluent()
    {
        Reload();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General &&
                e.Category != Microsoft.Win32.UserPreferenceCategory.Color) return;
            bool wasDark = Dark;
            var wasAccent = Accent;
            Reload();
            if (wasDark == Dark && wasAccent == Accent) return;
            var h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Repaints a control whenever the theme changes, and lets go when it is disposed.
    ///
    /// SystemEvents raises on its own thread, not the UI one, so the repaint has to be
    /// posted across - touching a control's colours from the notification thread either
    /// does nothing visible or throws, depending on the day.
    /// </summary>
    public static void Follow(Control c, Action apply)
    {
        EventHandler h = null;
        h = delegate
        {
            if (c.IsDisposed) { Changed -= h; return; }
            if (c.IsHandleCreated && c.InvokeRequired)
            {
                try { c.BeginInvoke(new Action(delegate { Repaint(c, apply); })); }
                catch { }
                return;
            }
            Repaint(c, apply);
        };
        Changed += h;
        c.Disposed += delegate { Changed -= h; };
    }

    static void Repaint(Control c, Action apply)
    {
        if (c.IsDisposed) return;
        try { apply(); c.Invalidate(true); }
        catch { }
    }

    static void Reload()
    {
        Dark = ReadDark();
        Accent = ReadAccent(Dark);
        if (Caption != null) return;   // fonts don't change with the theme

        // "Segoe UI Variable" is the Windows 11 UI face; Windows 10 falls back to Segoe UI.
        string text = Family("Segoe UI Variable Text", "Segoe UI");
        string display = Family("Segoe UI Variable Display", text);
        string semibold = Family("Segoe UI Variable Display Semib", Family("Segoe UI Semibold", display));
        string icons = Family("Segoe Fluent Icons", Family("Segoe MDL2 Assets", "Segoe UI Symbol"));

        Caption = new Font(text, 9f);
        Body = new Font(text, 10.5f);
        BodyStrong = new Font(semibold, 10.5f);
        Subtitle = new Font(semibold, 15f);
        Title = new Font(display, 18f, FontStyle.Bold);
        Glyphs = new Font(icons, 12f);
        GlyphsSmall = new Font(icons, 9f);
    }

    /// <summary>The first of these families that is actually installed.</summary>
    static string Family(string preferred, string fallback)
    {
        try
        {
            using (var c = new System.Drawing.Text.InstalledFontCollection())
                foreach (var f in c.Families)
                    if (string.Equals(f.Name, preferred, StringComparison.OrdinalIgnoreCase))
                        return preferred;
        }
        catch { }
        return fallback;
    }

    static bool ReadDark()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ThemeKey))
            {
                if (k == null) return false;
                var v = k.GetValue("AppsUseLightTheme");
                return v != null && Convert.ToInt32(v) == 0;
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// The user's accent colour, taken from the palette Windows itself derives.
    ///
    /// The palette is eight RGBA entries running light to dark: Light3, Light2, Light1,
    /// the accent itself, then Dark1..3. Dark mode uses Light2 rather than the accent
    /// because the same blue at full strength is unreadable on a dark surface - which is
    /// why the two themes read different entries rather than sharing one colour.
    /// </summary>
    static Color ReadAccent(bool dark)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AccentKey))
            {
                var raw = k == null ? null : k.GetValue("AccentPalette") as byte[];
                if (raw != null && raw.Length >= 16)
                {
                    int i = dark ? 4 : 12;   // AccentLight2, or the accent itself
                    return Color.FromArgb(raw[i], raw[i + 1], raw[i + 2]);
                }
            }
        }
        catch { }
        return dark ? Rgb(0x4CC2FF) : Rgb(0x0078D4);
    }

    static Color Rgb(int v) { return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF); }

    /// <summary>Accent shifted toward or away from the surface, for hover and press states.</summary>
    public static Color Shade(Color c, double amount)
    {
        double t = Dark ? 1 - amount : 1 + amount;   // on dark, "darker" reads as dimmer
        return Color.FromArgb(c.A,
            (int)Math.Max(0, Math.Min(255, c.R * t)),
            (int)Math.Max(0, Math.Min(255, c.G * t)),
            (int)Math.Max(0, Math.Min(255, c.B * t)));
    }

    /// <summary>Titlebar, border and corners, for a window with a frame.</summary>
    public static void Trim(Form f, bool small)
    {
        if (!f.IsHandleCreated) return;
        int on = Dark ? 1 : 0;
        Try(f.Handle, DarkMode, on);
        Try(f.Handle, DarkModeOld, on);
        Try(f.Handle, CornerPreference, small ? RoundSmall : RoundCorners);
    }

    /// <summary>Rounded corners and a hairline border, for a window with no frame.</summary>
    public static void Borderless(Form f)
    {
        if (!f.IsHandleCreated) return;
        Try(f.Handle, CornerPreference, RoundCorners);
        // COLORREF is 0x00BBGGRR, not RGB.
        var b = Stroke;
        Try(f.Handle, BorderColour, (b.B << 16) | (b.G << 8) | b.R);
    }

    /// <summary>
    /// Asks a native control to use the dark theme's scrollbars.
    ///
    /// The scrollbars on a TextBox or a scrolling Panel are drawn by Windows, not by us,
    /// and they default to the light theme regardless of what the window around them is
    /// doing. This is the documented way to ask otherwise. It is a request, not a
    /// guarantee - where Windows declines, the control simply keeps the bars it had.
    /// </summary>
    public static void DarkScrollbars(Control c)
    {
        if (!Dark || c == null || !c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
    }

    static void Try(IntPtr h, int attr, int value)
    {
        // Every one of these is version-gated; an old build simply rejects the call and
        // the window keeps the frame it would have had anyway.
        try { DwmSetWindowAttribute(h, attr, ref value, sizeof(int)); } catch { }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill)
    {
        using (var p = Rounded(r, radius))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, p);
    }

    public static void DrawRounded(Graphics g, RectangleF r, float radius, Color fill, Color stroke)
    {
        // Inset by half a pixel so the 1px stroke lands on the pixel grid instead of
        // straddling it, which is what makes hand-drawn borders look muddy.
        var rr = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        using (var p = Rounded(rr, radius))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            using (var pen = new Pen(stroke, 1)) g.DrawPath(pen, p);
        }
    }

    public static void Quality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    /// <summary>Text drawn with GDI, which is what makes it match the rest of the shell.</summary>
    public static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
    {
        TextRenderer.DrawText(g, s, f, r, c, flags);
    }

    public static Size Measure(string s, Font f, int maxWidth)
    {
        return TextRenderer.MeasureText(s, f, new Size(maxWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
    }
}

/// <summary>
/// The Fluent pill switch.
///
/// Drawn rather than themed, and animated, because the movement is what tells you the
/// click landed - a WinForms CheckBox gives no such feedback and reads as a form field
/// rather than a setting.
/// </summary>
class ToggleSwitch : Control
{
    const int TrackW = 40, TrackH = 20, Knob = 12;

    bool _on, _hover, _down;
    float _pos;                 // 0 = off, 1 = on; the animated value
    readonly System.Windows.Forms.Timer _anim;

    public event EventHandler Toggled;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(TrackW, TrackH);
        Cursor = Cursors.Hand;
        TabStop = true;

        _anim = new System.Windows.Forms.Timer();
        _anim.Interval = 15;
        _anim.Tick += (s, e) =>
        {
            float target = _on ? 1f : 0f;
            float step = 0.16f;
            if (Math.Abs(_pos - target) <= step) { _pos = target; _anim.Stop(); }
            else _pos += _pos < target ? step : -step;
            Invalidate();
        };
    }

    /// <summary>Sets the switch without raising Toggled - for loading stored state.</summary>
    public void SetSilently(bool value)
    {
        _on = value;
        _pos = value ? 1f : 0f;
        _anim.Stop();
        Invalidate();
    }

    public bool On
    {
        get { return _on; }
        set
        {
            if (_on == value) return;
            _on = value;
            _anim.Start();
            var h = Toggled;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = _down = false;
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); Focus(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnClick(EventArgs e) { On = !On; base.OnClick(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) On = !On;
        base.OnKeyUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);

        int y = (Height - TrackH) / 2;
        var track = new RectangleF(0, y, TrackW, TrackH);
        float r = TrackH / 2f;

        Color fill, stroke, knob;
        if (!Enabled)
        {
            fill = _on ? Fluent.Subtle : Color.Transparent;
            stroke = Fluent.Stroke;
            knob = Fluent.TextTertiary;
        }
        else if (_on)
        {
            fill = _down ? Fluent.Shade(Fluent.Accent, 0.2) : _hover ? Fluent.Shade(Fluent.Accent, 0.1) : Fluent.Accent;
            stroke = fill;
            knob = Fluent.OnAccent;
        }
        else
        {
            fill = _down ? Fluent.SubtleHover : _hover ? Fluent.Subtle : Color.Transparent;
            stroke = Fluent.Dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0);
            knob = Fluent.Dark ? Color.FromArgb(210, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0);
        }

        using (var p = Fluent.Rounded(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1), r))
        {
            if (fill != Color.Transparent) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            using (var pen = new Pen(stroke, 1)) g.DrawPath(pen, p);
        }

        // The knob swells slightly while pressed, the way the system one does.
        float grow = _down ? 2f : 0f;
        float travel = TrackW - Knob - 8;
        float kx = 4 + travel * _pos - grow / 2;
        float ky = y + (TrackH - Knob) / 2f - grow / 2;
        using (var b = new SolidBrush(knob))
            g.FillEllipse(b, kx, ky, Knob + grow, Knob + grow);

        if (Focused && ShowFocusCues)
            using (var pen = new Pen(Fluent.Text, 2))
                g.DrawPath(pen, Fluent.Rounded(new RectangleF(track.X - 3, track.Y - 3, track.Width + 6, track.Height + 6), r + 3));
    }
}

/// <summary>A Fluent button: accent-filled when primary, a quiet card otherwise.</summary>
class FluentButton : Control, IButtonControl
{
    bool _hover, _down;

    // IButtonControl, so a form can still nominate one of these as its AcceptButton or
    // CancelButton - which is what makes Enter and Escape work at all.
    public DialogResult DialogResult { get; set; }
    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    public bool Primary { get; set; }
    /// <summary>No fill until hovered, for icon buttons sitting on a card.</summary>
    public bool Quiet { get; set; }
    public string Glyph { get; set; }

    public FluentButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(120, 32);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) PerformClick();
        base.OnKeyUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = _down = false;
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        var r = new RectangleF(0, 0, Width, Height);

        Color fill, stroke, fore;
        if (!Enabled)
        {
            // Without this a disabled button is indistinguishable from a live one, and
            // the wizard spends whole pages with all three of them switched off.
            fill = Fluent.Subtle;
            stroke = Quiet ? Fluent.Subtle : Fluent.Stroke;
            fore = Fluent.TextTertiary;
        }
        else if (Primary)
        {
            fill = _down ? Fluent.Shade(Fluent.Accent, 0.2) : _hover ? Fluent.Shade(Fluent.Accent, 0.1) : Fluent.Accent;
            stroke = fill;
            fore = Fluent.OnAccent;
        }
        else if (Quiet)
        {
            fill = _down ? Fluent.SubtleHover : _hover ? Fluent.Subtle : Color.Transparent;
            stroke = fill;
            fore = Fluent.Text;
        }
        else
        {
            fill = _down ? Fluent.Subtle : _hover ? Fluent.CardHover : Fluent.Card;
            stroke = Fluent.Stroke;
            fore = Fluent.Text;
        }

        if (fill == Color.Transparent && stroke == Color.Transparent)
        { /* nothing to draw */ }
        else if (stroke == fill)
            Fluent.FillRounded(g, r, 4, fill);
        else
            Fluent.DrawRounded(g, r, 4, fill, stroke);

        const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                     | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (!string.IsNullOrEmpty(Glyph) && string.IsNullOrEmpty(Text))
            Fluent.Draw(g, Glyph, Fluent.Glyphs, fore, ClientRectangle, centre);
        else
            Fluent.Draw(g, Text, Fluent.Body, fore, ClientRectangle, centre);

        if (Focused && ShowFocusCues)
            using (var pen = new Pen(Fluent.Text, 2))
                g.DrawPath(pen, Fluent.Rounded(new RectangleF(-2, -2, Width + 4, Height + 4), 6));
    }
}

/// <summary>
/// A number with minus and plus buttons.
///
/// NumericUpDown is a native control with a hard-coded white field, so in dark mode it
/// lands on the page as a glowing white rectangle. This is the same thing, painted.
/// </summary>
class NumberStepper : Control
{
    int _value = 20, _min = 5, _max = 95, _step = 5;
    int _hot;   // 0 none, 1 minus, 2 plus

    public event EventHandler ValueChanged;
    public string Suffix { get; set; }

    public NumberStepper()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(116, 32);
        Suffix = "";
    }

    public void Configure(int min, int max, int step) { _min = min; _max = max; _step = step; }

    public int Value
    {
        get { return _value; }
        set
        {
            int v = Math.Max(_min, Math.Min(_max, value));
            if (v == _value) return;
            _value = v;
            Invalidate();
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    public void SetSilently(int v)
    {
        _value = Math.Max(_min, Math.Min(_max, v));
        Invalidate();
    }

    Rectangle Minus { get { return new Rectangle(0, 0, 32, Height); } }
    Rectangle Plus { get { return new Rectangle(Width - 32, 0, 32, Height); } }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int was = _hot;
        _hot = Minus.Contains(e.Location) ? 1 : Plus.Contains(e.Location) ? 2 : 0;
        Cursor = _hot == 0 ? Cursors.Default : Cursors.Hand;
        if (was != _hot) Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hot = 0; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Minus.Contains(e.Location)) Value = _value - _step;
        else if (Plus.Contains(e.Location)) Value = _value + _step;
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        Fluent.DrawRounded(g, new RectangleF(0, 0, Width, Height), 4, Fluent.Card, Fluent.Stroke);

        const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                     | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        if (_hot != 0 && Enabled)
        {
            var hot = _hot == 1 ? Minus : Plus;
            Fluent.FillRounded(g, new RectangleF(hot.X + 2, hot.Y + 2, hot.Width - 4, hot.Height - 4), 3, Fluent.Subtle);
        }

        Color text = Enabled ? Fluent.Text : Fluent.TextTertiary;
        Color down = Enabled && _value > _min ? text : Fluent.TextTertiary;
        Color up = Enabled && _value < _max ? text : Fluent.TextTertiary;
        Fluent.Draw(g, Fluent.IconMinus, Fluent.GlyphsSmall, down, Minus, centre);
        Fluent.Draw(g, Fluent.IconPlus, Fluent.GlyphsSmall, up, Plus, centre);
        Fluent.Draw(g, _value + Suffix, Fluent.Body, text,
            new Rectangle(32, 0, Width - 64, Height), centre);
    }
}

/// <summary>
/// A text field with the Fluent frame: rounded, and an accent underline while focused.
///
/// The TextBox inside is borderless and carries the theme's colours, because a stock one
/// paints a white field with a grey 3D frame whatever the window around it is doing.
/// </summary>
class FluentTextBox : Panel
{
    public readonly TextBox Inner;

    public FluentTextBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;

        Inner = new TextBox();
        Inner.BorderStyle = BorderStyle.None;
        Inner.Font = Fluent.Body;
        Inner.BackColor = Fluent.Card;
        Inner.ForeColor = Fluent.Text;
        Inner.GotFocus += (s, e) => Invalidate();
        Inner.LostFocus += (s, e) => Invalidate();
        Controls.Add(Inner);

        Fluent.Follow(this, delegate
        {
            Inner.BackColor = Fluent.Card;
            Inner.ForeColor = Fluent.Text;
        });
    }

    public override string Text
    {
        get { return Inner.Text; }
        set { Inner.Text = value; }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Setting Height in the constructor gets here before Inner exists.
        if (Inner == null) return;

        // The TextBox sizes its own height from the font; centre whatever it decided on.
        Inner.SetBounds(11, Math.Max(1, (Height - Inner.Height) / 2),
            Math.Max(10, Width - 22), Inner.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        Fluent.DrawRounded(g, new RectangleF(0, 0, Width, Height), 4, Fluent.Card, Fluent.Stroke);
        if (!Inner.Focused) return;

        // Fluent marks the focused field with a thicker accent line along the bottom.
        using (var pen = new Pen(Fluent.Accent, 2))
            g.DrawLine(pen, 5, Height - 1, Width - 5, Height - 1);
    }
}

/// <summary>The Windows 11 progress bar: a thin accent line in a thin trough.</summary>
class FluentProgress : Control
{
    int _value;

    public FluentProgress()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 12;
    }

    public int Value
    {
        get { return _value; }
        set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        float y = (Height - 3) / 2f;
        Fluent.FillRounded(g, new RectangleF(0, y, Width, 3), 1.5f, Fluent.Stroke);
        if (_value > 0)
            Fluent.FillRounded(g, new RectangleF(0, y, Width * _value / 100f, 3), 1.5f, Fluent.Accent);
    }
}

/// <summary>
/// One row of the settings pages: a rounded card with a glyph, a title, an optional
/// line of explanation, and whatever control does the work sitting on the right.
/// </summary>
class SettingsCard : Control
{
    const int PadX = 16, PadY = 12, GlyphW = 40;

    bool _hover;
    Control _action;

    public string Glyph { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    /// <summary>Greys the text without disabling the card, for a setting that has no effect yet.</summary>
    public bool Muted { get; set; }

    public SettingsCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 68;
        Glyph = "";
        Title = "";
    }

    public void SetAction(Control c)
    {
        _action = c;
        Controls.Add(c);
        Layout2();

        // Clicking anywhere on the row flips its switch. A 40px toggle is a small target,
        // and every row in the Settings app behaves this way, so aiming for it is a habit
        // people have already unlearned.
        var toggle = c as ToggleSwitch;
        if (toggle != null)
        {
            Cursor = Cursors.Hand;
            Click += (s, e) => { if (toggle.Enabled) toggle.On = !toggle.On; };
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnSizeChanged(EventArgs e) { Layout2(); base.OnSizeChanged(e); }

    void Layout2()
    {
        if (_action == null) return;
        _action.Location = new Point(Width - PadX - _action.Width, (Height - _action.Height) / 2);
        _action.Anchor = AnchorStyles.Right | AnchorStyles.Top;
    }

    /// <summary>Tall enough for the description at this width, so nothing is clipped.</summary>
    public void FitHeight()
    {
        int textW = TextWidth();
        int h = PadY * 2 + TextRenderer.MeasureText(Title, Fluent.Body).Height;
        if (!string.IsNullOrEmpty(Description))
            h += 2 + Fluent.Measure(Description, Fluent.Caption, textW).Height;
        Height = Math.Max(52, h);
        Layout2();
    }

    int TextWidth()
    {
        int right = _action != null ? _action.Width + 12 : 0;
        return Math.Max(40, Width - PadX * 2 - GlyphW - right);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        Fluent.DrawRounded(g, new RectangleF(0, 0, Width, Height), 4,
            _hover ? Fluent.CardHover : Fluent.Card, Fluent.Stroke);

        Color title = Muted ? Fluent.TextTertiary : Fluent.Text;
        Color desc = Muted ? Fluent.TextTertiary : Fluent.TextSecondary;

        if (!string.IsNullOrEmpty(Glyph))
            Fluent.Draw(g, Glyph, Fluent.Glyphs, desc, new Rectangle(PadX, 0, GlyphW - 12, Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int x = PadX + GlyphW;
        int w = TextWidth();
        int titleH = TextRenderer.MeasureText(Title, Fluent.Body).Height;
        bool hasDesc = !string.IsNullOrEmpty(Description);
        int descH = hasDesc ? Fluent.Measure(Description, Fluent.Caption, w).Height : 0;
        int y = (Height - titleH - (hasDesc ? descH + 2 : 0)) / 2;

        Fluent.Draw(g, Title, Fluent.Body, title, new Rectangle(x, y, w, titleH),
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (hasDesc)
            Fluent.Draw(g, Description, Fluent.Caption, desc, new Rectangle(x, y + titleH + 2, w, descH),
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/// <summary>An entry in the settings window's left rail, with the Fluent selection pill.</summary>
class NavItem : Control
{
    bool _hover, _selected;

    public string Glyph { get; set; }
    public string Page { get; set; }

    public NavItem()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 36;
        Cursor = Cursors.Hand;
        Glyph = "";
    }

    public bool Selected
    {
        get { return _selected; }
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);

        var r = new RectangleF(0, 1, Width, Height - 2);
        if (_selected) Fluent.DrawRounded(g, r, 4, Fluent.Card, Fluent.Stroke);
        else if (_hover) Fluent.FillRounded(g, r, 4, Fluent.Subtle);

        if (_selected)
        {
            // The accent pill: three pixels wide, centred, and short of the full height.
            var pill = new RectangleF(1, Height / 2f - 8, 3, 16);
            Fluent.FillRounded(g, pill, 1.5f, Fluent.Accent);
        }

        Fluent.Draw(g, Glyph, Fluent.GlyphsSmall, Fluent.Text, new Rectangle(14, 0, 20, Height),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        Fluent.Draw(g, Text, Fluent.Body, Fluent.Text, new Rectangle(42, 0, Width - 50, Height),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
            | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Dark colours for the tray's right-click menu, which WinForms otherwise paints light.</summary>
class FluentMenuColours : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground { get { return Fluent.Card; } }
    public override Color MenuBorder { get { return Fluent.Stroke; } }
    public override Color MenuItemBorder { get { return Color.Transparent; } }
    public override Color MenuItemSelected { get { return Fluent.Subtle; } }
    public override Color MenuItemSelectedGradientBegin { get { return Fluent.Subtle; } }
    public override Color MenuItemSelectedGradientEnd { get { return Fluent.Subtle; } }
    public override Color ImageMarginGradientBegin { get { return Fluent.Card; } }
    public override Color ImageMarginGradientMiddle { get { return Fluent.Card; } }
    public override Color ImageMarginGradientEnd { get { return Fluent.Card; } }
    public override Color SeparatorDark { get { return Fluent.Divider; } }
    public override Color SeparatorLight { get { return Fluent.Divider; } }
}

class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    public FluentMenuRenderer() : base(new FluentMenuColours()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Fluent.Text : Fluent.TextTertiary;
        e.TextFont = Fluent.Body;
        base.OnRenderItemText(e);
    }
}

/// <summary>What the tray UI needs to know about the current output, in one read.</summary>
class OutputStatus
{
    public string Name = "No output";
    public Guid Container;
    public bool Bluetooth;
    public int Battery = -1;
    public string Charge = "";
    public bool KeepingAwake;
    public string IdleReason;
}

/// <summary>
/// The panel that opens from the tray icon.
///
/// Modelled on the system's own flyouts - the volume and brightness popovers - because
/// that is what a tray app opening on one click should feel like: a small panel anchored
/// to the notification area that closes as soon as you look away, not a dialog that
/// lands in the middle of the screen and has to be dismissed.
/// </summary>
class TrayFlyout : Form
{
    const int W = 340, Pad = 12;

    readonly ToggleSwitch _keep;
    readonly FluentButton _settings, _quit;
    OutputStatus _s = new OutputStatus();

    Rectangle _card;
    DateTime _closedAt = DateTime.MinValue;

    public event EventHandler SettingsRequested;
    public event EventHandler QuitRequested;
    public event EventHandler PolicyChanged;

    public TrayFlyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Font = Fluent.Body;
        ClientSize = new Size(W, 196);

        _keep = new ToggleSwitch();
        _keep.Toggled += (s, e) =>
        {
            if (_s.Container == Guid.Empty) return;
            DevicePolicy.SetEnabled(_s.Container, _s.Name, _keep.On);
            var h = PolicyChanged;
            if (h != null) h(this, EventArgs.Empty);
        };
        Controls.Add(_keep);

        _settings = new FluentButton();
        _settings.Quiet = true;
        _settings.Glyph = Fluent.IconSettings;
        _settings.Size = new Size(32, 32);
        _settings.Click += (s, e) =>
        {
            Hide();
            var h = SettingsRequested;
            if (h != null) h(this, EventArgs.Empty);
        };
        Controls.Add(_settings);

        _quit = new FluentButton();
        _quit.Quiet = true;
        _quit.Glyph = Fluent.IconPower;
        _quit.Size = new Size(32, 32);
        _quit.Click += (s, e) =>
        {
            var h = QuitRequested;
            if (h != null) h(this, EventArgs.Empty);
        };
        Controls.Add(_quit);

        var tips = new ToolTip();
        tips.SetToolTip(_settings, "Settings");
        tips.SetToolTip(_quit, "Quit Speaker Keeper");

        Fluent.Follow(this, ApplyTheme);
        ApplyTheme();
        LayoutPanel();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;     // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
            cp.ClassStyle |= 0x00020000;  // CS_DROPSHADOW: a flyout needs to lift off the desktop
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fluent.Borderless(this);
    }

    void ApplyTheme()
    {
        BackColor = Fluent.Card;
        Fluent.Borderless(this);
    }

    void LayoutPanel()
    {
        int y = Pad + 52;                       // below the header block
        _card = new Rectangle(Pad, y, W - Pad * 2, 52);
        _keep.Location = new Point(_card.Right - 16 - _keep.Width, _card.Top + (_card.Height - _keep.Height) / 2);

        int footerY = _card.Bottom + 12 + 12;   // divider sits midway
        _quit.Location = new Point(W - Pad - _quit.Width, footerY);
        _settings.Location = new Point(_quit.Left - 4 - _settings.Width, footerY);
        ClientSize = new Size(W, footerY + _quit.Height + Pad);
    }

    /// <summary>Re-reads everything the panel shows. Cheap enough to call on every tick.</summary>
    public void Bind(OutputStatus s)
    {
        _s = s;
        _keep.SetSilently(s.Container == Guid.Empty || DevicePolicy.IsEnabled(s.Container));
        _keep.Enabled = s.Bluetooth && s.Container != Guid.Empty;
        _keep.Visible = _keep.Enabled;
        Invalidate();
    }

    /// <summary>
    /// True when the panel was open a moment ago.
    ///
    /// Clicking the tray icon deactivates the flyout, which hides it, and the click then
    /// arrives here and would open it straight back up. Without this the icon could never
    /// close what it opened.
    /// </summary>
    public bool JustClosed { get { return (DateTime.UtcNow - _closedAt).TotalMilliseconds < 300; } }

    protected override void OnDeactivate(EventArgs e)
    {
        _closedAt = DateTime.UtcNow;
        Hide();
        base.OnDeactivate(e);
    }

    protected override bool ProcessCmdKey(ref Message m, Keys k)
    {
        if (k == Keys.Escape) { _closedAt = DateTime.UtcNow; Hide(); return true; }
        return base.ProcessCmdKey(ref m, k);
    }

    /// <summary>Puts the panel against whichever screen edge the taskbar is on.</summary>
    public void ShowNearTray()
    {
        LayoutPanel();
        var screen = Screen.FromPoint(Cursor.Position);
        Rectangle wa = screen.WorkingArea, all = screen.Bounds;
        const int M = 12;

        int x, y;
        if (wa.Top > all.Top) { x = wa.Right - Width - M; y = wa.Top + M; }              // taskbar on top
        else if (wa.Left > all.Left) { x = wa.Left + M; y = wa.Bottom - Height - M; }    // taskbar on the left
        else { x = wa.Right - Width - M; y = wa.Bottom - Height - M; }                   // bottom, or right

        Location = new Point(
            Math.Max(wa.Left + 4, Math.Min(x, wa.Right - Width - 4)),
            Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - Height - 4)));

        Show();
        Activate();
    }

    /// <summary>Closes the panel without disposing it, unlike Form.Close.</summary>
    public void Dismiss()
    {
        _closedAt = DateTime.UtcNow;
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        using (var b = new SolidBrush(Fluent.Card)) g.FillRectangle(b, ClientRectangle);

        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                   | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                   | TextFormatFlags.EndEllipsis;

        // --- header: which speaker, and what the app is doing about it ---------------
        string glyph = _s.Bluetooth ? Fluent.IconBluetooth : Fluent.IconVolume;
        Fluent.Draw(g, glyph, Fluent.Glyphs, _s.KeepingAwake ? Fluent.Accent : Fluent.TextSecondary,
            new Rectangle(Pad + 6, Pad, 24, 24), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        Fluent.Draw(g, _s.Name, Fluent.BodyStrong, Fluent.Text,
            new Rectangle(Pad + 36, Pad, W - Pad * 2 - 36, 22), left);

        // Just the state here; when there is a reason, the card below carries it, and
        // printing it in both places reads like a stutter.
        string status = _s.KeepingAwake ? "Keeping awake" : "Idle";
        if (_s.Battery >= 0)
            status += "  ·  " + _s.Battery + "%" + (string.IsNullOrEmpty(_s.Charge) ? "" : " " + _s.Charge);

        Fluent.Draw(g, status, Fluent.Caption, Fluent.TextSecondary,
            new Rectangle(Pad + 36, Pad + 22, W - Pad * 2 - 36, 18), left);

        // --- the one control worth a click -------------------------------------------
        Fluent.DrawRounded(g, _card, 4, Fluent.Subtle, Fluent.Stroke);
        bool can = _keep.Enabled;
        string cardText = can ? "Keep this speaker awake" : Sentence(_s.IdleReason);
        Fluent.Draw(g, cardText, Fluent.Body, can ? Fluent.Text : Fluent.TextTertiary,
            new Rectangle(_card.X + 16, _card.Y, _card.Width - 32 - (can ? 56 : 0), _card.Height), left);

        // --- footer -------------------------------------------------------------------
        int dy = _card.Bottom + 12;
        using (var p = new Pen(Fluent.Divider)) g.DrawLine(p, Pad, dy, W - Pad, dy);

        Fluent.Draw(g, "Speaker Keeper " + Project.ShortVersion, Fluent.Caption, Fluent.TextTertiary,
            new Rectangle(Pad + 6, _settings.Top, W - Pad * 2 - 80, _settings.Height), left);
    }

    /// <summary>The policy's own wording, capitalised so it can stand on its own line.</summary>
    static string Sentence(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return "Not a Bluetooth speaker";
        return char.ToUpperInvariant(reason[0]) + reason.Substring(1);
    }
}

/// <summary>A page heading, painted so it follows the theme like everything else.</summary>
class Heading : Control
{
    public bool Small { get; set; }

    public Heading()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 34;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Fluent.Quality(e.Graphics);
        Fluent.Draw(e.Graphics, Text, Small ? Fluent.BodyStrong : Fluent.Subtitle,
            Small ? Fluent.TextSecondary : Fluent.Text, ClientRectangle,
            TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
    }
}

/// <summary>A paragraph of explanation between cards.</summary>
class Note : Control
{
    /// <summary>Full-strength body text, for labelling a control rather than explaining one.</summary>
    public bool Primary { get; set; }

    Font Face { get { return Primary ? Fluent.Body : Fluent.Caption; } }

    public Note()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 20;
    }

    /// <summary>
    /// Grows to fit the wrapped text. Measured with the font it will actually paint with -
    /// measuring body text as caption undercounts, and the last line is clipped away.
    /// </summary>
    public void FitHeight() { Height = Fluent.Measure(Text, Face, Math.Max(40, Width)).Height + 4; }

    protected override void OnPaint(PaintEventArgs e)
    {
        Fluent.Quality(e.Graphics);
        Fluent.Draw(e.Graphics, Text, Face,
            Primary ? Fluent.Text : Fluent.TextSecondary, ClientRectangle,
            TextFormatFlags.Left | (Primary ? TextFormatFlags.VerticalCenter : TextFormatFlags.Top)
            | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// A settings page: stacks whatever is put in it, full width, and scrolls if it has to.
///
/// The width is recomputed on every layout because AutoScroll takes the scrollbar out of
/// ClientSize only once it decides one is needed, so a card sized before that would sit
/// underneath it.
/// </summary>
class StackPage : Panel
{
    public int Gap = 6;

    public StackPage()
    {
        AutoScroll = true;
        Dock = DockStyle.Fill;
        Padding = new Padding(28, 20, 28, 24);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fluent.DarkScrollbars(this);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        int w = ClientSize.Width - Padding.Horizontal;
        if (w < 80) { base.OnLayout(e); return; }

        int y = Padding.Top;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            c.Left = Padding.Left;
            c.Width = w;

            var note = c as Note;
            if (note != null) note.FitHeight();
            var card = c as SettingsCard;
            if (card != null) card.FitHeight();

            c.Top = y;
            y += c.Height + (c is Heading ? 4 : Gap);
        }
        base.OnLayout(e);
    }
}

/// <summary>
/// The settings window: a navigation rail and pages of cards, like the Settings app.
///
/// The old one was a single fixed dialog of checkboxes. This is the same set of
/// settings, grouped, so the window says what each one does instead of relying on the
/// user to infer it from a five-word label.
/// </summary>
class SettingsForm : Form
{
    public event EventHandler SettingsChanged;

    readonly Panel _rail;
    readonly Panel _host;
    readonly StackPage _general, _speakers, _about;
    readonly List<NavItem> _nav = new List<NavItem>();

    readonly ToggleSwitch _runAtLogin, _warnLow, _autoUpdate;
    readonly NumberStepper _threshold;
    readonly SettingsCard _thresholdCard;
    readonly string _logPath, _dir;
    bool _loading;

    public SettingsForm(Icon icon, string logPath, string dir)
    {
        _logPath = logPath;
        _dir = dir;

        Text = "Speaker Keeper";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Fluent.Body;
        ClientSize = new Size(860, 560);
        MinimumSize = new Size(680, 460);
        DoubleBuffered = true;

        // --- pages -------------------------------------------------------------------
        _host = new Panel();
        _host.Dock = DockStyle.Fill;
        Controls.Add(_host);

        _general = new StackPage();
        _speakers = new StackPage();
        _about = new StackPage();

        _runAtLogin = new ToggleSwitch();
        _runAtLogin.Toggled += (s, e) =>
        {
            if (_loading) return;
            Settings.RunAtLogin = _runAtLogin.On;
            Raise();
        };

        _warnLow = new ToggleSwitch();
        _warnLow.Toggled += (s, e) =>
        {
            if (_loading) return;
            Settings.WarnLowBattery = _warnLow.On;
            UpdateEnabled();
            Raise();
        };

        _threshold = new NumberStepper();
        _threshold.Configure(5, 95, 5);
        _threshold.Suffix = "%";
        _threshold.ValueChanged += (s, e) =>
        {
            if (_loading) return;
            Settings.LowBatteryThreshold = _threshold.Value;
            Raise();
        };

        _autoUpdate = new ToggleSwitch();
        _autoUpdate.Toggled += (s, e) => OnAutoUpdateToggled();

        _general.Controls.Add(Head("General"));
        _general.Controls.Add(Card(Fluent.IconPower, "Start with Windows",
            "Launches Speaker Keeper automatically when you sign in.", _runAtLogin));
        _general.Controls.Add(Card(Fluent.IconWarning, "Warn me when the battery is low",
            "Shows a notification once per discharge, not on every reading.", _warnLow));
        _thresholdCard = Card(Fluent.IconBattery, "Warn below",
            "You're told once when the speaker drops below this.", _threshold);
        _general.Controls.Add(_thresholdCard);
        _general.Controls.Add(Card(Fluent.IconDownload, "Install updates automatically",
            "Checks once a day and installs in the background. Needs administrator approval once, "
            + "because it runs as a scheduled task under the system account.", _autoUpdate));

        _speakers.Controls.Add(Head("Speakers"));
        var devNote = new Note();
        devNote.Text = "Only Bluetooth speakers are listed. Wired and USB outputs never idle off, "
                     + "and earbuds are left alone so holding them awake cannot drain them. "
                     + "A speaker that is switched off stays here so you can still configure it.";
        _speakers.Controls.Add(devNote);

        _about.Controls.Add(Head("About"));
        var version = Card(Fluent.IconInfo, "Speaker Keeper", "Version " + Project.ShortVersion, null);
        version.SetAction(Button("What's new", false,
            () => Project.Open(Project.ReleaseNotesUrl(Project.ShortVersion))));
        _about.Controls.Add(version);

        var logCard = Card(Fluent.IconDocument, "Activity log",
            "Every device change, stream start and failure, with timestamps.", null);
        logCard.SetAction(Button("View log", false, ShowLog));
        _about.Controls.Add(logCard);

        var folderCard = Card(Fluent.IconFolder, "Installation folder", _dir, null);
        folderCard.SetAction(Button("Open folder", false, () => OpenPath(_dir)));
        _about.Controls.Add(folderCard);

        var repoCard = Card(Fluent.IconVolume, "Project page",
            "Source, releases and how it works.", null);
        repoCard.SetAction(Button("Open on GitHub", false, () => Project.Open(Project.Repo)));
        _about.Controls.Add(repoCard);

        _host.Controls.Add(_general);
        _host.Controls.Add(_speakers);
        _host.Controls.Add(_about);

        // --- rail --------------------------------------------------------------------
        // Added after the pages so docking puts it to their left rather than over them.
        _rail = new Panel();
        _rail.Dock = DockStyle.Left;
        _rail.Width = 196;
        _rail.Padding = new Padding(8, 16, 8, 8);
        Controls.Add(_rail);

        AddNav(Fluent.IconSettings, "General", _general);
        AddNav(Fluent.IconVolume, "Speakers", _speakers);
        AddNav(Fluent.IconInfo, "About", _about);
        Select(0);

        Fluent.Follow(this, ApplyTheme);
        ApplyTheme();
        ReloadFromSettings();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fluent.Trim(this, false);
    }

    void ApplyTheme()
    {
        BackColor = Fluent.Window;
        _rail.BackColor = Fluent.Window;
        _host.BackColor = Fluent.Window;
        _general.BackColor = Fluent.Window;
        _speakers.BackColor = Fluent.Window;
        _about.BackColor = Fluent.Window;
        Fluent.Trim(this, false);
    }

    // --- building blocks -------------------------------------------------------------

    static Heading Head(string text)
    {
        var h = new Heading();
        h.Text = text;
        return h;
    }

    static SettingsCard Card(string glyph, string title, string description, Control action)
    {
        var c = new SettingsCard();
        c.Glyph = glyph;
        c.Title = title;
        c.Description = description;
        if (action != null) c.SetAction(action);
        return c;
    }

    static FluentButton Button(string text, bool primary, Action onClick)
    {
        var b = new FluentButton();
        b.Text = text;
        b.Primary = primary;
        b.Size = new Size(Math.Max(104, TextRenderer.MeasureText(text, Fluent.Body).Width + 32), 32);
        b.Click += (s, e) => onClick();
        return b;
    }

    void AddNav(string glyph, string text, StackPage page)
    {
        var n = new NavItem();
        n.Glyph = glyph;
        n.Text = text;
        n.Page = text;
        n.Dock = DockStyle.Top;
        n.Tag = page;
        n.Click += (s, e) => Select(_nav.IndexOf((NavItem)s));
        _nav.Add(n);
        // Docked top stacks in reverse add order, so insert each one above the last.
        _rail.Controls.Add(n);
        _rail.Controls.SetChildIndex(n, 0);
    }

    void Select(int index)
    {
        for (int i = 0; i < _nav.Count; i++)
        {
            _nav[i].Selected = i == index;
            var page = (StackPage)_nav[i].Tag;
            page.Visible = i == index;
            if (i == index) page.BringToFront();
        }
        if (index == 1) LoadDevices();
    }

    // --- settings --------------------------------------------------------------------

    void OnAutoUpdateToggled()
    {
        if (_loading) return;
        bool want = _autoUpdate.On;

        // Machine-wide: creating the SYSTEM task raises one UAC prompt. If the user
        // dismisses it, snap the switch back rather than lying about the state - and say
        // why. Silently reverting looks identical to the setting not sticking, which is
        // how someone ends up believing updates are on when the task was never created.
        if (!Updater.SetAutoUpdateElevated(want))
        {
            _loading = true;
            _autoUpdate.SetSilently(!want);
            _loading = false;
            MessageBox.Show(this,
                (want ? "Automatic updates were not turned on."
                      : "Automatic updates were not turned off.")
                + "\n\nChanging this installs or removes a scheduled task that runs as "
                + "the system account, so Windows has to ask for permission. The "
                + "permission prompt was dismissed or refused, so nothing was changed.",
                "Speaker Keeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ReloadFromSettings();
        Raise();
    }

    void UpdateEnabled()
    {
        _threshold.Enabled = _warnLow.On;
        _thresholdCard.Muted = !_warnLow.On;
        _thresholdCard.Invalidate();
    }

    /// <summary>
    /// Bluetooth outputs that are connected now, plus any seen before, so a speaker that
    /// is currently switched off can still be configured.
    /// </summary>
    void LoadDevices()
    {
        // Everything after the heading and the note is a device row from last time.
        for (int i = _speakers.Controls.Count - 1; i >= 2; i--)
        {
            var c = _speakers.Controls[i];
            _speakers.Controls.RemoveAt(i);
            c.Dispose();
        }

        var seen = new HashSet<Guid>();
        var rows = new List<SettingsCard>();
        try
        {
            // Name rows after the physical Bluetooth device, not the audio endpoint: one
            // speaker exposes both "Speakers (X)" and "Headset Earphone (X Hands-Free)".
            var btNames = DeviceProps.BluetoothDevices();

            foreach (var d in DeviceProps.RenderEndpoints())
            {
                if (!d.IsBluetooth || d.Container == Guid.Empty) continue;
                if (!seen.Add(d.Container)) continue;

                string name;
                if (!btNames.TryGetValue(d.Container, out name) || string.IsNullOrEmpty(name))
                    name = d.Name;

                DevicePolicy.Remember(d.Container, name);
                rows.Add(DeviceRow(d.Container, name, true));
            }

            foreach (var r in DevicePolicy.All())
            {
                if (!seen.Add(r.Container)) continue;
                rows.Add(DeviceRow(r.Container, r.Name, false));
            }
        }
        catch { }

        if (rows.Count == 0)
        {
            var empty = Card(Fluent.IconBluetooth, "No Bluetooth speakers found",
                "Pair a speaker and connect it, then come back here.", null);
            empty.Muted = true;
            rows.Add(empty);
        }

        foreach (var r in rows) _speakers.Controls.Add(r);
        _speakers.PerformLayout();
    }

    SettingsCard DeviceRow(Guid container, string name, bool present)
    {
        var toggle = new ToggleSwitch();
        toggle.SetSilently(DevicePolicy.IsEnabled(container));
        toggle.Toggled += (s, e) =>
        {
            DevicePolicy.SetEnabled(container, name, toggle.On);
            // Let the tray re-evaluate straight away rather than waiting for the next tick.
            BeginInvoke(new Action(Raise));
        };
        var card = Card(Fluent.IconVolume, name,
            present ? "Connected" : "Not connected right now", toggle);
        card.Muted = !present;
        return card;
    }

    LogWindow _log;

    void ShowLog()
    {
        if (_log != null && !_log.IsDisposed)
        {
            if (_log.WindowState == FormWindowState.Minimized) _log.WindowState = FormWindowState.Normal;
            _log.Activate();
            return;
        }
        _log = new LogWindow(Icon, _logPath);
        _log.Show();
    }

    static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    void Raise()
    {
        var h = SettingsChanged;
        if (h != null) h(this, EventArgs.Empty);
    }

    /// <summary>Re-reads the stored values, for when something else changed one behind our back.</summary>
    public void ReloadFromSettings()
    {
        _loading = true;
        try
        {
            _runAtLogin.SetSilently(Settings.RunAtLogin);
            _warnLow.SetSilently(Settings.WarnLowBattery);
            _threshold.SetSilently(Settings.LowBatteryThreshold);
            _autoUpdate.SetSilently(Settings.AutoUpdate);
            UpdateEnabled();
            if (_speakers.Visible) LoadDevices();
        }
        finally { _loading = false; }
    }
}

/// <summary>
/// Which speakers to keep awake, remembered per physical device.
///
/// Keyed by ContainerId rather than endpoint id: the endpoint id changes when a device
/// is re-paired, but the container is stable, so a speaker keeps its setting.
/// </summary>
static class DevicePolicy
{
    const string Key = "Software" + "\\" + "SpeakerKeeper" + "\\" + "Devices";

    public class Remembered
    {
        public Guid Container;
        public string Name;
        public bool Enabled;
    }

    static string Sub(Guid container) { return Key + "\\" + container.ToString("B"); }

    /// <summary>Bluetooth devices default to enabled; that is the whole point of the app.</summary>
    public static bool IsEnabled(Guid container)
    {
        if (container == Guid.Empty) return true;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Sub(container)))
            {
                if (k == null) return true;
                var v = k.GetValue("Enabled");
                return v == null || Convert.ToInt32(v) != 0;
            }
        }
        catch { return true; }
    }

    public static void SetEnabled(Guid container, string name, bool enabled)
    {
        if (container == Guid.Empty) return;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Sub(container)))
            {
                if (k == null) return;
                k.SetValue("Enabled", enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                if (!string.IsNullOrEmpty(name)) k.SetValue("Name", name);
            }
        }
        catch { }
    }

    /// <summary>Records a device we've seen, so it can still be listed when disconnected.</summary>
    public static void Remember(Guid container, string name)
    {
        if (container == Guid.Empty) return;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Sub(container)))
            {
                if (k == null) return;
                if (k.GetValue("Enabled") == null)
                    k.SetValue("Enabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
                if (!string.IsNullOrEmpty(name)) k.SetValue("Name", name);
            }
        }
        catch { }
    }

    public static List<Remembered> All()
    {
        var list = new List<Remembered>();
        try
        {
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
            {
                if (root == null) return list;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        Guid g;
                        if (!Guid.TryParse(name, out g)) continue;
                        using (var k = root.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            var v = k.GetValue("Enabled");
                            list.Add(new Remembered
                            {
                                Container = g,
                                Name = (k.GetValue("Name") as string) ?? g.ToString("B"),
                                Enabled = v == null || Convert.ToInt32(v) != 0,
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return list;
    }
}

/// <summary>
/// Holds a silent WASAPI render stream open on the default output. That stream is the
/// whole keep-alive: a speaker idles off when nothing is being sent to it.
///
/// Rendering the silence directly is what keeps Speaker Keeper invisible. A media
/// player publishes a transport session, so Windows puts a "Speaker Keeper" card with
/// play/next/previous in the media flyout and hands it the user's media keys - controls
/// for a track that does not exist. A raw render stream publishes nothing, and
/// AUDCLNT_SESSIONFLAGS_DISPLAY_HIDE keeps its row out of the volume mixer as well.
///
/// One dedicated MTA thread owns the stream end to end. These interfaces are
/// apartment-bound, so they must not be created on the WinForms STA thread and then
/// fed from somewhere else.
/// </summary>
static class Silence
{
    const int ShareModeShared = 0;
    const int ClsCtxAll = 23;

    // DISPLAY_HIDE keeps the row out of the volume mixer; EXPIREWHENUNOWNED asks for the
    // session to be retired once nothing holds it.
    const int SessionFlags = 0x20000000 | 0x10000000;

    // One session for the whole process, not one per stream.
    //
    // The audio engine keeps a session record alive for as long as its owning process
    // runs, whatever the stream does and whatever EXPIREWHENUNOWNED asks for. A GUID per
    // stream therefore left a dead session behind on every Bluetooth reconnect - hidden
    // and silent, but one more of them every time, all day. Reusing one GUID means the
    // first stream creates the session, which is what makes DISPLAY_HIDE stick, and every
    // later stream rejoins that same already-hidden session.
    //
    // It is generated per launch rather than hard-coded so it can never collide with a
    // session some other program created, which would be a visible one.
    static readonly Guid SessionId = Guid.NewGuid();

    const int BufferSilent = 0x2;           // AUDCLNT_BUFFERFLAGS_SILENT
    const long BufferDuration = 20000000;   // 2 seconds, in 100ns units
    const int FeedMs = 500;                 // top-up interval, well inside the buffer

    enum State { Idle, Starting, Running, Failed }

    static volatile State _state = State.Idle;
    static Thread _thread;
    static ManualResetEvent _stop;
    static DateTime _startedAt;
    static volatile string _error;

    /// <summary>Why the last stream stopped, or null if it stopped because we said so.</summary>
    public static string LastError { get { return _error; } }

    /// <summary>
    /// True while the stream is up, or still coming up. A start that never completes -
    /// a wedged audio driver - stops counting as healthy, so the caller retries instead
    /// of waiting forever on a stream that is never going to arrive.
    /// </summary>
    public static bool Active
    {
        get
        {
            var s = _state;
            if (s == State.Running) return true;
            return s == State.Starting && (DateTime.UtcNow - _startedAt).TotalSeconds < 30;
        }
    }

    public static void Start()
    {
        Stop();
        _error = null;
        _startedAt = DateTime.UtcNow;
        _state = State.Starting;
        _stop = new ManualResetEvent(false);
        _thread = new Thread(Run);
        _thread.IsBackground = true;
        _thread.Name = "Speaker Keeper silence";
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public static void Stop()
    {
        var t = _thread;
        if (t == null) { _state = State.Idle; return; }
        _thread = null;
        try { _stop.Set(); } catch { }

        // Bounded: an audio driver that refuses to stop must not take the tray app
        // down with it. The thread is a background thread, so a lost one dies at exit.
        try { if (!t.Join(4000)) Program.Log("silence thread did not stop in time"); }
        catch { }
        _state = State.Idle;
    }

    static void Run()
    {
        IntPtr fmt = IntPtr.Zero;
        object en = null, dev = null, client = null, render = null;
        try
        {
            en = new MMDeviceEnumeratorClass();
            IMMDevice endpoint;
            if (((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(0, 0, out endpoint) != 0 || endpoint == null)
            { Fail("no default output"); return; }
            dev = endpoint;

            var iid = typeof(IAudioClient).GUID;
            object obj;
            if (endpoint.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out obj) != 0 || obj == null)
            { Fail("cannot open the output"); return; }
            client = obj;
            var audio = (IAudioClient)obj;

            int hr = audio.GetMixFormat(out fmt);
            if (hr != 0) { Fail("mix format " + Hex(hr)); return; }

            // The engine's own mix format, so the stream needs no conversion and no
            // resampler - it is the cheapest thing that still counts as playback.
            var session = SessionId;
            hr = audio.Initialize(ShareModeShared, SessionFlags, BufferDuration, 0, fmt, ref session);
            if (hr != 0) { Fail("initialize " + Hex(hr)); return; }

            uint frames;
            hr = audio.GetBufferSize(out frames);
            if (hr != 0 || frames == 0) { Fail("buffer size " + Hex(hr)); return; }

            var riid = typeof(IAudioRenderClient).GUID;
            object svc;
            hr = audio.GetService(ref riid, out svc);
            if (hr != 0 || svc == null) { Fail("render client " + Hex(hr)); return; }
            render = svc;
            var buffer = (IAudioRenderClient)svc;

            if (!Push(buffer, frames)) { Fail("could not fill the buffer"); return; }

            hr = audio.Start();
            if (hr != 0) { Fail("start " + Hex(hr)); return; }

            _state = State.Running;
            Program.Log("silent stream started (hidden session, no transport controls)");

            while (!_stop.WaitOne(FeedMs))
            {
                uint pad;
                hr = audio.GetCurrentPadding(out pad);
                // The usual way out: the speaker disconnected or the endpoint was
                // reconfigured, which invalidates the stream. The caller rebuilds.
                if (hr != 0) { Fail("stream lost " + Hex(hr)); break; }
                if (frames > pad && !Push(buffer, frames - pad))
                { Fail("stream lost while writing"); break; }
            }

            try { audio.Stop(); } catch { }
        }
        catch (Exception ex) { Fail(ex.Message); }
        finally
        {
            if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);

            // Every one of these, or the session outlives the stream. EXPIREWHENUNOWNED
            // only retires a session once the last reference to it is gone, and a
            // forgotten render client is enough to keep a dead session on the device -
            // one more for every reconnect, for as long as the app runs.
            Release(render);
            Release(client);
            Release(dev);
            Release(en);

            if (_state != State.Failed) _state = State.Idle;
        }
    }

    /// <summary>
    /// Hands the engine another block of silence. AUDCLNT_BUFFERFLAGS_SILENT means the
    /// engine zeroes the buffer itself, so the mix format never has to be parsed.
    /// </summary>
    static bool Push(IAudioRenderClient render, uint frames)
    {
        IntPtr buf;
        if (render.GetBuffer(frames, out buf) != 0) return false;
        return render.ReleaseBuffer(frames, BufferSilent) == 0;
    }

    static void Release(object o)
    {
        if (o == null) return;
        try { Marshal.FinalReleaseComObject(o); } catch { }
    }

    static void Fail(string why)
    {
        _error = why;
        _state = State.Failed;
        Program.Log("silent stream failed: " + why);
    }

    static string Hex(int hr) { return "0x" + hr.ToString("X8"); }
}

static class Program
{
    // Read-only assets live next to the exe. The install folder is Program Files,
    // which standard users cannot write to, so anything we WRITE goes under
    // %LocalAppData% instead - otherwise the app breaks for non-admin users.
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Speaker Keeper");
    static readonly string LogFile = Path.Combine(DataDir, "SpeakerKeeper.log");

    static string _lastDevice = "";
    static Mutex _mutex;
    static int _ticks;

    static NotifyIcon _tray;
    static ContextMenuStrip _menu;
    static TrayFlyout _flyout;
    static SettingsForm _settings;

    // Latches so a low battery warns once per discharge, not every poll.
    static bool _lowWarned;
    static int _lastBattery = -1;

    static string _idleReason;

    // Set when this launch found a different version than the user last ran, i.e. a
    // silent auto-update happened underneath them.
    static string _updatedFrom;

    // What clicking the current balloon should do. Balloons are reused for different
    // messages, so the action is swapped per balloon rather than leaving handlers
    // attached - otherwise clicking a low-battery toast would open release notes.
    static Action _balloonClick;

    // Windows exposes no charging flag for Bluetooth audio devices.
    //
    // Only ONE direction is sound evidence: a battery percentage cannot rise without
    // external power, so a rise means it is on a charger. A FALL proves nothing about
    // the cable - a speaker held awake, or playing loudly, can draw more than the
    // charger supplies and drain while plugged in. So this never claims "unplugged";
    // it reports the battery trend and only asserts charging when it sees a rise.
    enum Charge { Unknown, Charging, Draining }
    static Charge _charge = Charge.Unknown;
    static DateTime _batteryAt = DateTime.MinValue;    // when the last change was observed
    static DateTime _batteryStamp = DateTime.MinValue; // device's own timestamp for it

    const long MaxLogBytes = 1024 * 1024;   // rotate at 1 MB, keep one previous file

    internal static void Log(string m)
    {
        try
        {
            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);

            // Without this the log grows forever on a machine that's always on.
            try
            {
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    string old = LogFile + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogFile, old);
                }
            }
            catch { }

            File.AppendAllText(LogFile,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Friendly name of the current default output, for log lines.</summary>
    static string CurrentOutputName()
    {
        try
        {
            string id = DefaultDeviceId();
            if (string.IsNullOrEmpty(id)) return "unknown";
            string name = DeviceProps.EndpointName(id);
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }
        catch { return "unknown"; }
    }

    static string DefaultDeviceId()
    {
        try
        {
            var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorClass());
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return "";
            string id;
            dev.GetId(out id);
            return id == null ? "" : id;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Decides whether the current output should be held awake.
    ///
    /// Two gates: it must be a Bluetooth device at all (keeping a monitor or a USB
    /// speaker awake achieves nothing, and holding earbuds awake actively wastes their
    /// battery), and it must not have been switched off for that particular speaker.
    /// </summary>
    static bool ShouldKeepAwake(string endpointId, out string reason)
    {
        if (string.IsNullOrEmpty(endpointId)) { reason = "no default output"; return false; }

        Guid container;
        if (!DeviceProps.TryContainer(endpointId, out container) || container == Guid.Empty)
        {
            reason = "output device not identifiable";
            return false;
        }

        if (!DeviceProps.BluetoothContainers().Contains(container))
        {
            reason = "not a Bluetooth output";
            return false;
        }

        string btName;
        DeviceProps.BluetoothDevices().TryGetValue(container, out btName);
        DevicePolicy.Remember(container, btName ?? DeviceProps.EndpointName(endpointId));

        if (!DevicePolicy.IsEnabled(container))
        {
            reason = "turned off for this speaker";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Starts, sustains or stops the silent stream to match the policy.</summary>
    static void ApplyPolicy(bool deviceChanged)
    {
        string reason;
        bool keep = ShouldKeepAwake(_lastDevice, out reason);

        if (!keep)
        {
            if (Silence.Active)
            {
                Log("stopping - " + reason);
                Silence.Stop();
            }
            else if (_idleReason != reason)
            {
                Log("idle - " + reason);
            }
            _idleReason = reason;
            return;
        }

        _idleReason = null;
        if (Silence.Active && !deviceChanged) return;

        // Read this before Start(), which clears it: a dropped Bluetooth link shows up
        // here as the reason the stream died, and that is the one line worth logging.
        string died = Silence.LastError;
        if (died != null && !deviceChanged) Log("stream lost (" + died + ") - restarting");
        Silence.Start();
    }

    static string IcoPath { get { return Path.Combine(Dir, "SpeakerKeeper.ico"); } }

    /// <summary>Exactly the notification-area size, so no frame gets rescaled.</summary>
    static Icon TrayIcon()
    {
        try
        {
            if (File.Exists(IcoPath))
                return new Icon(IcoPath, SystemInformation.SmallIconSize);
        }
        catch { }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return SystemIcons.Application; }
    }

    /// <summary>
    /// The full multi-resolution icon, for window and taskbar use.
    ///
    /// This must NOT be the tray icon: that one is a single 16x16 frame, and handing
    /// it to a Form makes the taskbar upscale 16px to 32px, which looks blurry. Passing
    /// the whole .ico lets Windows pick the frame that matches each context.
    /// </summary>
    static Icon WindowIcon()
    {
        try
        {
            if (File.Exists(IcoPath))
                return new Icon(IcoPath);
        }
        catch { }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return SystemIcons.Application; }
    }

    static void BuildTray()
    {
        var settings = new ToolStripMenuItem("Settings");
        settings.Click += (s, e) => ShowSettings();

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (s, e) => QuitApp();

        // The right-click menu is now only the two things the panel cannot be: a way in
        // when the panel is already open, and a way out. Everything the old menu showed -
        // output, battery, status - is on the panel, one click away.
        _menu = new ContextMenuStrip();
        _menu.Renderer = new FluentMenuRenderer();
        _menu.Font = Fluent.Body;
        _menu.Items.Add(settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(quit);

        _flyout = new TrayFlyout();
        _flyout.SettingsRequested += (s, e) => ShowSettings();
        _flyout.QuitRequested += (s, e) => QuitApp();
        _flyout.PolicyChanged += (s, e) =>
        {
            // Act on the switch immediately; waiting for the next tick makes a toggle
            // that takes five seconds to do anything look broken.
            ApplyPolicy(false);
            RefreshUi();
            if (_settings != null && !_settings.IsDisposed) _settings.ReloadFromSettings();
        };

        _tray = new NotifyIcon();
        _tray.Icon = TrayIcon();
        _tray.Text = "Speaker Keeper";
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = true;

        // One handler for every balloon; the action is whatever the balloon that is
        // currently showing set. Cleared on click and on close so a dismissed balloon
        // can't leave a stale action armed for the next, unrelated one.
        _tray.BalloonTipClicked += (s, e) =>
        {
            var act = _balloonClick;
            _balloonClick = null;
            if (act != null) act();
        };
        _tray.BalloonTipClosed += (s, e) => { _balloonClick = null; };

        // One click opens the panel, the way the system's own volume and brightness
        // flyouts work. A double-click gesture would mean waiting to find out whether a
        // second click is coming, and there is nothing else for it to mean here.
        //
        // Clicking the icon while the panel is open deactivates it, which hides it, and
        // the click then arrives here - so a panel that was open a moment ago counts as
        // open, otherwise the icon could never close what it opened.
        _tray.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_flyout.Visible || _flyout.JustClosed) { _flyout.Dismiss(); return; }
            RefreshUi();
            _flyout.ShowNearTray();
        };

        Application.ApplicationExit += (s, e) =>
        {
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
        };

        RefreshUi();
        var st = Status();
        Log("tray ready - " + st.Name + ", battery "
            + (st.Battery >= 0 ? st.Battery + "%" : "not reported"));

        // Only once the tray icon exists - a balloon with no icon to anchor to is
        // silently dropped by the shell.
        if (_updatedFrom != null) ShowUpdatedNotice();
    }

    /// <summary>
    /// Tells the user that the app changed under them, and offers the release notes.
    ///
    /// If notifications are turned off system-wide the shell drops this silently, which
    /// is why the same information is also shown permanently in Settings.
    /// </summary>
    static void ShowUpdatedNotice()
    {
        try
        {
            string now = Project.ShortVersion;
            _balloonClick = () => Project.Open(Project.ReleaseNotesUrl(now));
            _tray.ShowBalloonTip(
                10000,
                "Speaker Keeper updated",
                "Now version " + now + ", up from " + _updatedFrom + ". Click to see what changed.",
                ToolTipIcon.Info);
        }
        catch (Exception ex) { Log("update notice failed: " + ex.Message); }
    }

    /// <summary>
    /// Notices that the binaries changed since this user last ran the app.
    ///
    /// Auto-updates are installed by a SYSTEM task overnight with no prompt and no UI,
    /// so without this the app just silently becomes a different program. Recording the
    /// version per user and comparing on launch is enough to say so once.
    ///
    /// Deliberately does NOT claim an update when:
    ///  - this is the first run for this user - nothing to compare against
    ///  - the version is unchanged - an ordinary relaunch
    ///  - the version went BACKWARDS - a downgrade or an older build being reinstalled.
    ///    Calling that an update would be a lie, so it is only logged.
    /// </summary>
    static void DetectVersionChange()
    {
        try
        {
            string now = Project.ShortVersion;
            string before = Settings.LastRunVersion;

            // Recorded unconditionally, so a skipped notice never repeats on every launch.
            Settings.LastRunVersion = now;

            if (string.IsNullOrEmpty(before)) { Log("first run for this user, v" + now); return; }
            if (string.Equals(before, now, StringComparison.Ordinal)) return;

            Version a, b;
            if (Version.TryParse(before, out a) && Version.TryParse(now, out b) && b < a)
            {
                Log("version went backwards: " + before + " -> " + now + " (not reporting as an update)");
                return;
            }

            _updatedFrom = before;
            Log("updated since last run: " + before + " -> " + now);
        }
        catch (Exception ex) { Log("version check failed: " + ex.Message); }
    }

    static void ShowSettings()
    {
        if (_settings != null && !_settings.IsDisposed)
        {
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.ReloadFromSettings();
            _settings.BringToFront();
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(WindowIcon(), LogFile, Dir);
        _settings.SettingsChanged += (s, e) =>
        {
            ApplyPolicy(false);
            RefreshUi();
        };
        _settings.Show();
    }

    static void QuitApp()
    {
        Log("quit requested from tray");
        Silence.Stop();
        if (_flyout != null) _flyout.Dismiss();
        _tray.Visible = false;
        Application.Exit();
    }

    /// <summary>Everything the tray UI shows about the current output, read in one go.</summary>
    static OutputStatus Status()
    {
        var s = new OutputStatus();
        try
        {
            string id = DefaultDeviceId();
            string name = string.IsNullOrEmpty(id) ? null : DeviceProps.EndpointName(id);
            s.Name = string.IsNullOrEmpty(name) ? "No output" : name;

            Guid container;
            if (!string.IsNullOrEmpty(id) && DeviceProps.TryContainer(id, out container)
                && container != Guid.Empty
                && DeviceProps.BluetoothContainers().Contains(container))
            {
                s.Bluetooth = true;
                s.Container = container;

                // Show the speaker's own name rather than the endpoint's: one speaker
                // exposes both "Speakers (X)" and "Headset Earphone (X Hands-Free)".
                string bt;
                if (DeviceProps.BluetoothDevices().TryGetValue(container, out bt)
                    && !string.IsNullOrEmpty(bt)) s.Name = bt;
            }

            s.Battery = string.IsNullOrEmpty(id) ? -1 : DeviceProps.BatteryPercent(id);
            // "on charger" only when a rise was actually observed; a falling battery is
            // labelled as such rather than claimed to be unplugged.
            s.Charge = _charge == Charge.Charging ? "on charger"
                     : _charge == Charge.Draining ? "draining" : "";
            s.KeepingAwake = Silence.Active;
            s.IdleReason = _idleReason;
        }
        catch { }
        return s;
    }

    static void RefreshUi()
    {
        try
        {
            var st = Status();
            if (_flyout != null) _flyout.Bind(st);

            // NotifyIcon.Text is capped at 63 characters, so keep the tooltip terse.
            string tip = "Speaker Keeper" + (st.KeepingAwake ? "" : " - idle")
                       + (st.Battery >= 0 ? " - " + st.Battery + "%" : "");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }
        catch (Exception ex) { Log("ui refresh failed: " + ex.Message); }
    }

    /// <summary>
    /// Warns once when the speaker crosses below the threshold. The latch only resets
    /// after it climbs 5 points back above it, so a reading hovering on the boundary
    /// doesn't nag repeatedly.
    /// </summary>
    static void CheckBattery()
    {
        try
        {
            string id = DefaultDeviceId();
            if (string.IsNullOrEmpty(id)) return;

            var reading = DeviceProps.Battery(id);
            int pct = reading.Percent;
            if (pct < 0) { _lastBattery = -1; return; }

            int limit = Settings.LowBatteryThreshold;
            if (pct > limit + 5) _lowWarned = false;

            if (pct != _lastBattery)
            {
                var now = DateTime.Now;

                if (_lastBattery >= 0)
                {
                    // Rate of change, so the log shows how fast it is actually moving.
                    // A speaker draining at 2%/h is almost certainly on a charger that is
                    // just losing the race; one draining at 20%/h is not. That judgement
                    // is left to the reader rather than guessed at here.
                    string rate = "";
                    if (_batteryAt != DateTime.MinValue)
                    {
                        double hours = (now - _batteryAt).TotalHours;
                        if (hours > 0.001)
                            rate = string.Format(" ({0}{1}% in {2:N0}m, ~{3:N0}%/h)",
                                pct > _lastBattery ? "+" : "", pct - _lastBattery,
                                (now - _batteryAt).TotalMinutes,
                                Math.Abs((pct - _lastBattery) / hours));
                    }

                    if (pct > _lastBattery)
                    {
                        // The only sound inference: a battery cannot gain charge on its own.
                        if (_charge != Charge.Charging)
                            Log("on charger - battery rose " + _lastBattery + "% -> " + pct
                                + "% on " + CurrentOutputName());
                        _charge = Charge.Charging;
                    }
                    else
                    {
                        // Deliberately does NOT say "unplugged": draining while plugged in
                        // is normal for a speaker that is awake or playing loudly.
                        if (_charge != Charge.Draining)
                            Log("battery draining (cannot tell if plugged in)"
                                + " on " + CurrentOutputName());
                        _charge = Charge.Draining;
                    }

                    Log("battery " + _lastBattery + "% -> " + pct + "%" + rate);
                }
                else
                {
                    Log("battery " + pct + "% on " + CurrentOutputName());
                }

                _lastBattery = pct;
                _batteryAt = now;
                _batteryStamp = reading.UpdatedAt;
                RefreshUi();
            }

            if (!Settings.WarnLowBattery || _lowWarned || pct > limit) return;

            string name = DeviceProps.EndpointName(id);
            _lowWarned = true;
            Log("low battery warning at " + pct + "% (threshold " + limit + "%)");

            // A NotifyIcon balloon is rendered by the Windows 10/11 shell as a real
            // system toast, so this honours Do Not Disturb and the user's per-app
            // notification settings rather than drawing anything custom.
            _balloonClick = null;       // this one isn't clickable; don't inherit the last action
            _tray.ShowBalloonTip(
                10000,
                "Speaker battery low",
                (string.IsNullOrEmpty(name) ? "Your speaker" : name) + " is at " + pct + "%.",
                ToolTipIcon.Warning);
        }
        catch (Exception ex) { Log("battery check failed: " + ex.Message); }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();

        bool uninstall = false, quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)) quiet = true;

            // Invoked elevated by the Settings checkbox; does its work and exits.
            if (string.Equals(a, "--set-autoupdate", StringComparison.OrdinalIgnoreCase))
            {
                bool on = i + 1 < args.Length &&
                          string.Equals(args[i + 1], "on", StringComparison.OrdinalIgnoreCase);
                Environment.Exit(Updater.Apply(on));
                return;
            }
        }
        if (uninstall) { Installer.Uninstall(quiet); return; }

        bool created;
        // The name is historical - it predates the move off the media session - but
        // renaming it would let an old copy and a new one run side by side across an
        // upgrade, each holding its own stream open.
        _mutex = new Mutex(true, "Local" + (char)92 + "SpeakerKeeperSMTC", out created);
        if (!created) return;   // already running

        Log("--- starting v" + Application.ProductVersion
            + ", pid " + System.Diagnostics.Process.GetCurrentProcess().Id
            + ", exe " + Application.ExecutablePath);
        Settings.RepairRunPath();
        DetectVersionChange();

        // Sleep/resume is when a keep-alive most often breaks: the Bluetooth link drops
        // and the media session can come back dead. Logging both edges makes it obvious
        // from the log whether a failure followed a resume.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
        {
            Log("power mode: " + e.Mode);
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                // Give Bluetooth a moment to reconnect before asserting playback.
                var resume = new System.Windows.Forms.Timer();
                resume.Interval = 5000;
                resume.Tick += (s2, e2) =>
                {
                    resume.Stop();
                    resume.Dispose();
                    Log("post-resume check - output is " + CurrentOutputName());
                    _lastDevice = DefaultDeviceId();
                    ApplyPolicy(true);
                    RefreshUi();
                };
                resume.Start();
            }
        };

        Microsoft.Win32.SystemEvents.SessionEnding += (s, e) =>
            Log("session ending: " + e.Reason);
        _lastDevice = DefaultDeviceId();
        ApplyPolicy(true);
        BuildTray();

        var t = new System.Windows.Forms.Timer();
        t.Interval = 5000;
        t.Tick += OnTick;
        t.Start();

        Application.Run(new ApplicationContext());
    }

    static void OnTick(object s, EventArgs e)
    {
        _ticks++;
        try
        {
            string dev = DefaultDeviceId();
            if (dev != _lastDevice)
            {
                // Name the device: "changed" alone tells you nothing when you are trying
                // to work out why the speaker went to sleep.
                Log("default output changed -> " + CurrentOutputName());
                _lastDevice = dev;
                ApplyPolicy(true);
                RefreshUi();   // keep the tooltip pointing at the new device
            }
            else
            {
                ApplyPolicy(false);
            }

            // Battery moves slowly and each read walks the device tree, so poll it
            // every 2 minutes rather than on every 5-second tick.
            if (_ticks % 24 == 0)
                CheckBattery();

            if (_ticks % 120 == 0)
                Log("heartbeat " + (Silence.Active
                        ? "stream running"
                        : "idle - " + (_idleReason ?? Silence.LastError ?? "stopped")));
        }
        catch (Exception ex) { Log("tick error: " + ex.Message); }
    }
}
