using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Windows.Media.Core;
using Windows.Media.Playback;

// These become the Win32 version resource, which is what Windows shows as the
// app's name in the Volume Mixer and in Task Manager. Without them the mixer
// row is blank. It is also the name the UAC prompt shows, which is why the
// uninstaller build gets its own title.
#if UNINSTALLER
[assembly: AssemblyTitle("Speaker Keeper Uninstaller")]
#elif INSTALLER
[assembly: AssemblyTitle("Speaker Keeper Setup")]
#else
[assembly: AssemblyTitle("Speaker Keeper")]
#endif
[assembly: AssemblyProduct("Speaker Keeper")]
[assembly: AssemblyDescription("Keeps a Bluetooth speaker awake with a silent media session")]
[assembly: AssemblyCompany("Kevin Abou Hanna")]
[assembly: AssemblyCopyright("Copyright (c) Kevin Abou Hanna")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

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
    readonly CheckBox _follow;
    readonly Label _status;
    readonly System.Windows.Forms.Timer _timer;
    long _pos;

    public LogWindow(Icon icon, string path)
    {
        _path = path;

        Text = "Speaker Keeper Log";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 440);
        MinimumSize = new Size(520, 300);
        Font = SystemFonts.MessageBoxFont;

        // Added before the bottom bar: docking runs in reverse z-order, so the control
        // added first ends up filling whatever space the docked bars leave.
        _view = new TextBox();
        _view.Multiline = true;
        _view.ReadOnly = true;
        _view.ScrollBars = ScrollBars.Both;
        _view.WordWrap = false;
        _view.Dock = DockStyle.Fill;
        _view.BackColor = Color.White;      // ReadOnly would otherwise render grey
        _view.Font = Monospace();
        Controls.Add(_view);

        // Everything below is docked rather than positioned: a Panel is still its default
        // 200px wide while its children are being added, so any layout computed from
        // bar.Width here would be wrong once docking resizes it.
        var bar = new Panel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 72;
        bar.Padding = new Padding(12, 6, 12, 10);

        var row = new Panel();
        row.Dock = DockStyle.Fill;

        _follow = new CheckBox();
        _follow.Text = "Follow live";
        _follow.Checked = true;
        _follow.AutoSize = false;
        _follow.Width = 110;
        _follow.Dock = DockStyle.Left;      // CheckBox centres its own text vertically
        _follow.CheckedChanged += (s, e) => { if (_follow.Checked) Poll(); };
        row.Controls.Add(_follow);

        var notepad = new Button();
        notepad.Text = "Open in Notepad";
        notepad.Size = new Size(130, 28);
        notepad.Margin = new Padding(8, 4, 0, 0);
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

        var close = new Button();
        close.Text = "Close";
        close.Size = new Size(100, 28);
        close.Margin = new Padding(8, 4, 0, 0);
        close.Click += (s, e) => Close();

        var buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Right;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;
        buttons.AutoSize = true;
        buttons.Controls.Add(close);        // right-to-left: rightmost added first
        buttons.Controls.Add(notepad);
        row.Controls.Add(buttons);

        _status = new Label();
        _status.Dock = DockStyle.Top;
        _status.Height = 20;
        _status.AutoEllipsis = true;
        _status.ForeColor = SystemColors.GrayText;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = _path;

        bar.Controls.Add(row);              // added first, so it docks last and fills
        bar.Controls.Add(_status);
        Controls.Add(bar);

        CancelButton = close;

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = 1000;
        _timer.Tick += (s, e) => Poll();
        _timer.Start();

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
        if (!_follow.Checked) return;

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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _timer.Stop(); _timer.Dispose(); } catch { }
        base.OnFormClosed(e);
    }
}

/// <summary>The "Settings..." window: the handful of knobs that don't belong in the tray menu.</summary>
class SettingsForm : Form
{
    public event EventHandler SettingsChanged;

    readonly CheckBox _runAtLogin, _warnLow, _autoUpdate;
    readonly CheckedListBox _devices;

    /// <summary>Row in the device list; ToString is what CheckedListBox renders.</summary>
    class DeviceRow
    {
        public Guid Container;
        public string Name;
        public bool Present;
        public override string ToString()
        {
            return Present ? Name : Name + "   (not connected)";
        }
    }
    readonly NumericUpDown _threshold;
    readonly Label _thresholdLabel;
    readonly string _logPath, _dir;
    bool _loading;

    public SettingsForm(Icon icon, string logPath, string dir)
    {
        _logPath = logPath;
        _dir = dir;

        Text = "Speaker Keeper Settings";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(380, 490);
        Padding = new Padding(16);

        int y = 16;

        _runAtLogin = new CheckBox();
        _runAtLogin.Text = "Start with Windows";
        _runAtLogin.Location = new Point(16, y);
        _runAtLogin.AutoSize = true;
        _runAtLogin.CheckedChanged += (s, e) =>
        {
            if (_loading) return;
            Settings.RunAtLogin = _runAtLogin.Checked;
            Raise();
        };
        Controls.Add(_runAtLogin);
        y += 24;

        var hint = new Label();
        hint.Text = "Launches Speaker Keeper automatically when you sign in.";
        hint.Location = new Point(34, y);
        hint.AutoSize = false;
        hint.Size = new Size(330, 18);
        hint.ForeColor = SystemColors.GrayText;
        Controls.Add(hint);
        y += 32;

        _warnLow = new CheckBox();
        _warnLow.Text = "Notify me when the speaker battery is low";
        _warnLow.Location = new Point(16, y);
        _warnLow.AutoSize = true;
        _warnLow.CheckedChanged += (s, e) =>
        {
            if (_loading) return;
            Settings.WarnLowBattery = _warnLow.Checked;
            UpdateEnabled();
            Raise();
        };
        Controls.Add(_warnLow);
        y += 28;

        _thresholdLabel = new Label();
        _thresholdLabel.Text = "Warn below";
        _thresholdLabel.Location = new Point(34, y + 3);
        _thresholdLabel.AutoSize = true;
        Controls.Add(_thresholdLabel);

        _threshold = new NumericUpDown();
        _threshold.Minimum = 5;
        _threshold.Maximum = 95;
        _threshold.Increment = 5;
        _threshold.Location = new Point(114, y);
        _threshold.Width = 60;
        _threshold.ValueChanged += (s, e) =>
        {
            if (_loading) return;
            Settings.LowBatteryThreshold = (int)_threshold.Value;
            Raise();
        };
        Controls.Add(_threshold);

        var pct = new Label();
        pct.Text = "%";
        pct.Location = new Point(180, y + 3);
        pct.AutoSize = true;
        Controls.Add(pct);
        y += 40;

        _autoUpdate = new CheckBox();
        _autoUpdate.Text = "Install updates automatically";
        _autoUpdate.Location = new Point(16, y);
        _autoUpdate.AutoSize = true;
        _autoUpdate.CheckedChanged += (s, e) =>
        {
            if (_loading) return;
            bool want = _autoUpdate.Checked;
            // Machine-wide: creating the SYSTEM task raises one UAC prompt. If the user
            // dismisses it, snap the checkbox back rather than lying about the state -
            // and say why. Silently reverting looks identical to the setting not
            // sticking, which is how someone ends up believing updates are on when the
            // task was never created.
            if (!Updater.SetAutoUpdateElevated(want))
            {
                _loading = true;
                _autoUpdate.Checked = !want;
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
        };
        Controls.Add(_autoUpdate);
        y += 24;

        var updHint = new Label();
        updHint.Text = "Checks daily. Needs admin once to turn on.";
        updHint.Location = new Point(34, y);
        updHint.AutoSize = false;
        updHint.Size = new Size(330, 18);
        updHint.ForeColor = SystemColors.GrayText;
        Controls.Add(updHint);
        y += 34;

        var devLabel = new Label();
        devLabel.Text = "Keep these speakers awake:";
        devLabel.Location = new Point(16, y);
        devLabel.AutoSize = true;
        devLabel.Font = new Font(Font, FontStyle.Bold);
        Controls.Add(devLabel);
        y += 22;

        _devices = new CheckedListBox();
        _devices.Location = new Point(16, y);
        _devices.Size = new Size(348, 104);
        _devices.CheckOnClick = true;
        _devices.IntegralHeight = false;
        _devices.ItemCheck += OnDeviceChecked;
        Controls.Add(_devices);
        y += 110;

        var devHint = new Label();
        devHint.Text = "Only Bluetooth outputs are listed - wired and USB devices never sleep.";
        devHint.Location = new Point(16, y);
        devHint.AutoSize = false;
        devHint.Size = new Size(348, 30);
        devHint.ForeColor = SystemColors.GrayText;
        Controls.Add(devHint);
        y += 36;

        var openLog = new Button();
        openLog.Text = "View log";
        openLog.Location = new Point(16, y);
        openLog.Size = new Size(110, 30);
        openLog.Click += (s, e) => ShowLog();
        Controls.Add(openLog);

        var openFolder = new Button();
        openFolder.Text = "Open folder";
        openFolder.Location = new Point(134, y);
        openFolder.Size = new Size(110, 30);
        openFolder.Click += (s, e) => OpenPath(_dir);
        Controls.Add(openFolder);

        var close = new Button();
        close.Text = "Close";
        close.Location = new Point(262, y);
        close.Size = new Size(100, 30);
        close.Click += (s, e) => Close();
        Controls.Add(close);

        AcceptButton = close;
        CancelButton = close;

        // Version footer. Auto-updates are silent, so this is the one place a user can
        // always find out which build they are actually running and what went into it -
        // a toast can be missed or suppressed, this cannot.
        y += 40;

        var ver = new Label();
        ver.Text = "Version " + Project.ShortVersion;
        ver.Location = new Point(16, y);
        ver.AutoSize = true;
        ver.ForeColor = SystemColors.GrayText;
        Controls.Add(ver);

        var whatsNew = new LinkLabel();
        whatsNew.Text = "What's new";
        whatsNew.Location = new Point(16 + ver.PreferredWidth + 12, y);
        whatsNew.AutoSize = true;
        whatsNew.LinkClicked += (s, e) => Project.Open(Project.ReleaseNotesUrl(Project.ShortVersion));
        Controls.Add(whatsNew);

        ReloadFromSettings();
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

    void UpdateEnabled()
    {
        _threshold.Enabled = _warnLow.Checked;
        _thresholdLabel.Enabled = _warnLow.Checked;
    }

    void OnDeviceChecked(object sender, ItemCheckEventArgs e)
    {
        if (_loading) return;
        var row = _devices.Items[e.Index] as DeviceRow;
        if (row == null) return;
        DevicePolicy.SetEnabled(row.Container, row.Name, e.NewValue == CheckState.Checked);
        // Let the tray re-evaluate straight away rather than waiting for the next tick.
        BeginInvoke(new Action(Raise));
    }

    /// <summary>
    /// Bluetooth outputs that are connected now, plus any we've seen before so a speaker
    /// that is currently switched off can still be configured.
    /// </summary>
    void LoadDevices()
    {
        _devices.Items.Clear();

        var seen = new HashSet<Guid>();
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
                _devices.Items.Add(
                    new DeviceRow { Container = d.Container, Name = name, Present = true },
                    DevicePolicy.IsEnabled(d.Container));
            }

            foreach (var r in DevicePolicy.All())
            {
                if (!seen.Add(r.Container)) continue;
                _devices.Items.Add(
                    new DeviceRow { Container = r.Container, Name = r.Name, Present = false },
                    r.Enabled);
            }
        }
        catch { }

        if (_devices.Items.Count == 0)
            _devices.Items.Add(new DeviceRow { Name = "No Bluetooth speakers found", Present = true }, false);
    }

    /// <summary>Re-reads the stored values, for when the tray menu changed one behind our back.</summary>
    public void ReloadFromSettings()
    {
        _loading = true;
        try
        {
            _runAtLogin.Checked = Settings.RunAtLogin;
            _warnLow.Checked = Settings.WarnLowBattery;
            _threshold.Value = Settings.LowBatteryThreshold;
            _autoUpdate.Checked = Settings.AutoUpdate;
            LoadDevices();
            UpdateEnabled();
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

static class Program
{
    // Read-only assets live next to the exe. The install folder is Program Files,
    // which standard users cannot write to, so anything we WRITE goes under
    // %LocalAppData% instead - otherwise the app breaks for non-admin users.
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Wav = Path.Combine(Dir, "silent.wav");
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Speaker Keeper");
    static readonly string LogFile = Path.Combine(DataDir, "SpeakerKeeper.log");

    static MediaPlayer _player;
    static MediaSource _source;   // strong ref: without this the GC reclaims it and playback dies
    static string _lastDevice = "";
    static Mutex _mutex;
    static int _ticks;

    static NotifyIcon _tray;
    static ContextMenuStrip _menu;
    static ToolStripMenuItem _deviceItem, _batteryItem, _statusItem, _startupItem;
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

    static void Log(string m)
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

    /// <summary>Starts, sustains or stops the silent session to match the policy.</summary>
    static void ApplyPolicy(bool deviceChanged)
    {
        string reason;
        bool keep = ShouldKeepAwake(_lastDevice, out reason);

        if (!keep)
        {
            if (_player != null)
            {
                Log("stopping - " + reason);
                StopPlayer();
            }
            else if (_idleReason != reason)
            {
                Log("idle - " + reason);
            }
            _idleReason = reason;
            return;
        }

        _idleReason = null;

        if (_player == null || deviceChanged)
        {
            StartPlayer();
            return;
        }

        var st = _player.PlaybackSession.PlaybackState;
        if (st != MediaPlaybackState.Playing &&
            st != MediaPlaybackState.Opening &&
            st != MediaPlaybackState.Buffering)
        {
            Log("state=" + st + " - reasserting play");
            try { _player.Play(); } catch { }
            if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                Log("still not playing - rebuilding");
                StartPlayer();
            }
        }
    }

    static void StopPlayer()
    {
        try
        {
            if (_player != null)
            {
                try { _player.Pause(); } catch { }
                try { _player.Dispose(); } catch { }
            }
        }
        catch { }
        _player = null;
        _source = null;
    }

    static void StartPlayer()
    {
        try
        {
            if (_player != null)
            {
                try { _player.Pause(); } catch { }
                try { _player.Dispose(); } catch { }
            }

            var p = new MediaPlayer();
            _source = MediaSource.CreateFromUri(new Uri(Wav));   // absolute local path -> file URI
            p.Source = _source;
            p.IsLoopingEnabled = true;
            p.Volume = 1.0;

            // Register the media session (that is what keeps the speaker awake) but
            // advertise NO transport controls, so Windows routes the user's media keys
            // to their real player instead of to this silent track.
            // NOTE: disabling Play AND Pause makes Windows drop the media session
            // entirely - and that session is what keeps the speaker awake. So leave
            // those at default and only suppress the navigation controls, which are
            // the ones that would otherwise hijack next/previous keypresses.
            var cm = p.CommandManager;
            cm.NextBehavior.EnablingRule           = MediaCommandEnablingRule.Never;
            cm.PreviousBehavior.EnablingRule       = MediaCommandEnablingRule.Never;
            cm.FastForwardBehavior.EnablingRule    = MediaCommandEnablingRule.Never;
            cm.RewindBehavior.EnablingRule         = MediaCommandEnablingRule.Never;
            cm.PositionBehavior.EnablingRule       = MediaCommandEnablingRule.Never;
            cm.ShuffleBehavior.EnablingRule        = MediaCommandEnablingRule.Never;
            cm.AutoRepeatModeBehavior.EnablingRule = MediaCommandEnablingRule.Never;

            p.Play();
            _player = p;
            Log("player started (silent loop, no transport controls)");
        }
        catch (Exception ex) { Log("start failed: " + ex.Message); }
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
        _deviceItem = new ToolStripMenuItem("Output: -");
        _deviceItem.Font = new Font(_deviceItem.Font, FontStyle.Bold);
        _deviceItem.Click += (s, e) => RefreshMenu();

        _batteryItem = new ToolStripMenuItem("Battery: -");
        _batteryItem.Click += (s, e) => RefreshMenu();

        _statusItem = new ToolStripMenuItem("Status: -");
        _statusItem.Click += (s, e) => RefreshMenu();

        _startupItem = new ToolStripMenuItem("Start with Windows");
        _startupItem.CheckOnClick = true;
        _startupItem.Click += (s, e) =>
        {
            Settings.RunAtLogin = _startupItem.Checked;
            Log("start with Windows -> " + _startupItem.Checked);
            if (_settings != null && !_settings.IsDisposed) _settings.ReloadFromSettings();
        };

        var settings = new ToolStripMenuItem("Settings...");
        settings.Click += (s, e) => ShowSettings();

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (s, e) =>
        {
            Log("quit requested from tray");
            _tray.Visible = false;
            Application.Exit();
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_deviceItem);
        _menu.Items.Add(_batteryItem);
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(quit);
        _menu.Opening += (s, e) => RefreshMenu();

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

        // Windows convention: double-clicking a tray icon opens the app's own window,
        // while right-click gets the menu. Right-click is already handled by assigning
        // ContextMenuStrip above.
        _tray.MouseDoubleClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowSettings();
        };

        Application.ApplicationExit += (s, e) =>
        {
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
        };

        RefreshMenu();
        Log("tray ready - " + _deviceItem.Text + ", " + _batteryItem.Text);

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
        _settings.SettingsChanged += (s, e) => RefreshMenu();
        _settings.Show();
    }

    static void RefreshMenu()
    {
        try
        {
            string id = DefaultDeviceId();
            string name = string.IsNullOrEmpty(id) ? null : DeviceProps.EndpointName(id);
            _deviceItem.Text = "Output: " + (string.IsNullOrEmpty(name) ? "unknown" : name);

            int pct = string.IsNullOrEmpty(id) ? -1 : DeviceProps.BatteryPercent(id);
            // "on charger" only when a rise was actually observed; a falling battery is
            // labelled as such rather than claimed to be unplugged.
            string charge = _charge == Charge.Charging ? " (on charger)"
                          : _charge == Charge.Draining ? " (draining)" : "";
            _batteryItem.Text = pct >= 0 ? "Battery: " + pct + "%" + charge : "Battery: not reported";

            _statusItem.Text = _player != null
                ? "Status: keeping awake"
                : "Status: idle" + (string.IsNullOrEmpty(_idleReason) ? "" : " - " + _idleReason);

            _startupItem.Checked = Settings.RunAtLogin;

            // NotifyIcon.Text is capped at 63 characters, so keep the tooltip terse.
            string tip = "Speaker Keeper" + (_player == null ? " - idle" : "")
                       + (pct >= 0 ? " - " + pct + "%" : "");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }
        catch (Exception ex) { Log("menu refresh failed: " + ex.Message); }
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
                RefreshMenu();
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
                    RefreshMenu();
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
                RefreshMenu();   // keep the tooltip pointing at the new device
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
                Log("heartbeat " + (_player == null
                        ? "idle - " + _idleReason
                        : "state=" + _player.PlaybackSession.PlaybackState));
        }
        catch (Exception ex) { Log("tick error: " + ex.Message); }
    }
}
