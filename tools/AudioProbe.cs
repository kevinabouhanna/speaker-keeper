// AudioProbe - a bench tool for finding out why a speaker switched itself off.
//
// Not part of the app, and not shipped. It exists because the same question kept being
// re-asked from scratch: when a speaker went quiet, was anything actually playing to it,
// and had something opened its microphone? Speaker Keeper's own log could not say.
// See docs/INVESTIGATION-auto-off.md for what it has been used to establish.
//
//   AudioProbe watch [logfile]                  one line per change, a summary a minute
//   AudioProbe play  <output> <signal> [secs]   send a test signal to one output
//
// <output> is any part of the output's name, e.g. "2- Xiaomi". Signals:
//   silence              digital zero, what Speaker Keeper 1.5.1 sends
//   noise:<dB>           white noise at that peak level, e.g. noise:-50
//   sine:<Hz>:<dB>       a steady tone, e.g. sine:20:-30
//   burst:<every>:<dB>   one second of noise every <every> seconds, silence between
//
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:AudioProbe.exe AudioProbe.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorClass { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY { public Guid fmtid; public int pid; public PROPERTYKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }

// 24 bytes covers the x64 layout; only vt and the first pointer are read.
[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long def, out long min);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr h);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, int flags);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int i, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
    [PreserveSig] int GetGroupingParam(out Guid g);
    [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
    [PreserveSig] int GetChannelCount(out int n);
    [PreserveSig] int SetMasterVolumeLevel(float db, IntPtr ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float v, IntPtr ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
    [PreserveSig] int SetChannelVolumeLevel(uint c, float db, IntPtr ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint c, float v, IntPtr ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint c, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint c, out float v);
    [PreserveSig] int SetMute(bool mute, IntPtr ctx);
    [PreserveSig] int GetMute(out bool mute);
}

class Endpoint
{
    public string Id, Name, Enumerator;
    public int Flow;          // 0 render, 1 capture
    public float Volume = -1; public bool Muted;
    public float Peak;        // whole endpoint, 0..1
    public string Format = "";
    public List<Session> Sessions = new List<Session>();
}

class Session
{
    public uint Pid; public string Process; public int State; public float Peak; public string Instance;
}

static class Audio
{
    const int Render = 0, Capture = 1, Active = 1, ClsctxAll = 23;
    static Guid IID_Client = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static Guid IID_Sessions = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    static Guid IID_Meter = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    static Guid IID_Volume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
    static PROPERTYKEY FriendlyName = new PROPERTYKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    static PROPERTYKEY EnumeratorName = new PROPERTYKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 24);

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    public static IMMDeviceEnumerator NewEnumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorClass(); }

    static string Prop(IMMDevice d, PROPERTYKEY key)
    {
        IPropertyStore store;
        if (d.OpenPropertyStore(0, out store) != 0 || store == null) return null;
        try
        {
            PROPVARIANT v;
            if (store.GetValue(ref key, out v) != 0) return null;
            try { return v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    public static string Id(IMMDevice d) { string id; return d.GetId(out id) == 0 ? id : null; }
    public static string Name(IMMDevice d) { return Prop(d, FriendlyName) ?? Id(d); }

    public static List<Endpoint> Snapshot(IMMDeviceEnumerator en, bool withFormat)
    {
        var list = new List<Endpoint>();
        foreach (int flow in new[] { Render, Capture })
        {
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(flow, Active, out col) != 0) continue;
            uint n; col.GetCount(out n);
            for (uint i = 0; i < n; i++)
            {
                IMMDevice d;
                if (col.Item(i, out d) != 0) continue;
                try { list.Add(Describe(d, flow, withFormat)); }
                catch { }
                finally { Marshal.ReleaseComObject(d); }
            }
            Marshal.ReleaseComObject(col);
        }
        return list;
    }

    static Endpoint Describe(IMMDevice d, int flow, bool withFormat)
    {
        var e = new Endpoint { Id = Id(d), Name = Name(d), Enumerator = Prop(d, EnumeratorName) ?? "", Flow = flow };
        object o;

        if (d.Activate(ref IID_Meter, ClsctxAll, IntPtr.Zero, out o) == 0)
        {
            float p; if (((IAudioMeterInformation)o).GetPeakValue(out p) == 0) e.Peak = p;
            Marshal.ReleaseComObject(o);
        }
        if (d.Activate(ref IID_Volume, ClsctxAll, IntPtr.Zero, out o) == 0)
        {
            var v = (IAudioEndpointVolume)o; float s; bool m;
            if (v.GetMasterVolumeLevelScalar(out s) == 0) e.Volume = s;
            if (v.GetMute(out m) == 0) e.Muted = m;
            Marshal.ReleaseComObject(o);
        }
        if (withFormat && d.Activate(ref IID_Client, ClsctxAll, IntPtr.Zero, out o) == 0)
        {
            IntPtr f;
            if (((IAudioClient)o).GetMixFormat(out f) == 0)
            {
                e.Format = (uint)Marshal.ReadInt32(f, 4) + "Hz/" + (ushort)Marshal.ReadInt16(f, 2) + "ch";
                Marshal.FreeCoTaskMem(f);
            }
            Marshal.ReleaseComObject(o);
        }
        if (d.Activate(ref IID_Sessions, ClsctxAll, IntPtr.Zero, out o) == 0)
        {
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator se;
            if (mgr.GetSessionEnumerator(out se) == 0)
            {
                int c; se.GetCount(out c);
                for (int i = 0; i < c; i++)
                {
                    IAudioSessionControl2 s;
                    if (se.GetSession(i, out s) != 0) continue;
                    var x = new Session();
                    s.GetState(out x.State);
                    s.GetProcessId(out x.Pid);
                    IntPtr inst;
                    if (s.GetSessionInstanceIdentifier(out inst) == 0) { x.Instance = Marshal.PtrToStringUni(inst); Marshal.FreeCoTaskMem(inst); }
                    x.Process = ProcessName(x.Pid, s.IsSystemSoundsSession() == 0);
                    var meter = s as IAudioMeterInformation;
                    if (meter != null) { float p; if (meter.GetPeakValue(out p) == 0) x.Peak = p; }
                    if (x.State != 2) e.Sessions.Add(x);   // 2 = expired
                    Marshal.ReleaseComObject(s);
                }
                Marshal.ReleaseComObject(se);
            }
            Marshal.ReleaseComObject(o);
        }
        return e;
    }

    static readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();
    static string ProcessName(uint pid, bool system)
    {
        if (system) return "system sounds";
        string n;
        if (_names.TryGetValue(pid, out n)) return n;
        try { n = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
        catch { n = "pid " + pid; }
        _names[pid] = n;
        return n;
    }

    public static string DefaultName(IMMDeviceEnumerator en, int flow, int role)
    {
        IMMDevice d;
        if (en.GetDefaultAudioEndpoint(flow, role, out d) != 0 || d == null) return "none";
        try { return Name(d); } finally { Marshal.ReleaseComObject(d); }
    }

    public static string Db(float peak)
    {
        if (peak <= 0) return "silent";
        return Math.Round(20 * Math.Log10(peak)) + " dB";
    }
}

static class Probe
{
    static TextWriter _log;

    static void Log(string s)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s;
        Console.WriteLine(line);
        if (_log != null) { _log.WriteLine(line); _log.Flush(); }
    }

    [MTAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "watch") return Watch(args.Length > 1 ? args[1] : null);
        if (args.Length >= 3 && args[0] == "play") return Play(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 0);
        if (args.Length >= 2 && args[0] == "record") return Record(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
        Console.WriteLine("usage: AudioProbe watch [logfile] | play <output> <signal> [secs] | record <microphone> [secs]");
        return 2;
    }

    // ---- watch ---------------------------------------------------------------

    class Minute { public float MaxPeak; public int SoundSecs; public DateTime LastSound = DateTime.MinValue; public DateTime Since = DateTime.Now; public HashSet<string> Players = new HashSet<string>(); }

    static int Watch(string file)
    {
        if (file != null) _log = new StreamWriter(file, true);
        var en = Audio.NewEnumerator();
        var prev = new Dictionary<string, string>();
        var minutes = new Dictionary<string, Minute>();
        DateTime nextSummary = DateTime.Now.AddSeconds(60), nextFormat = DateTime.MinValue;
        var formats = new Dictionary<string, string>();
        Log("--- watch started");

        while (true)
        {
            bool fmt = DateTime.Now >= nextFormat;
            if (fmt) nextFormat = DateTime.Now.AddSeconds(10);
            List<Endpoint> eps;
            try { eps = Audio.Snapshot(en, fmt); }
            catch (Exception ex) { Log("snapshot failed: " + ex.Message); Thread.Sleep(1000); continue; }

            var now = new Dictionary<string, string>();
            now["default output"] = Audio.DefaultName(en, 0, 0);
            now["default output (calls)"] = Audio.DefaultName(en, 0, 2);
            now["default microphone"] = Audio.DefaultName(en, 1, 0);
            now["default microphone (calls)"] = Audio.DefaultName(en, 1, 2);

            foreach (var e in eps)
            {
                string kind = e.Flow == 0 ? "output" : "microphone";
                if (fmt && e.Format != "") formats[e.Id] = e.Format;
                string f; formats.TryGetValue(e.Id, out f);
                now[kind + " " + e.Name] = "present" + (f != null ? " " + f : "") + " [" + e.Enumerator + "]"
                    + (e.Flow == 0 ? " vol " + Math.Round(e.Volume * 100) + "%" + (e.Muted ? " MUTED" : "") : "");

                foreach (var s in e.Sessions)
                {
                    // Active sessions only: an inactive one is an app that has the output
                    // open but is not sending anything, which is almost every app.
                    if (s.State != 1) continue;
                    string verb = e.Flow == 0 ? "playing to" : "RECORDING from";
                    now[s.Process + " " + verb + " " + e.Name + " #" + (s.Instance ?? "").GetHashCode()] = "active";
                }

                if (e.Flow == 0)
                {
                    Minute m;
                    if (!minutes.TryGetValue(e.Name, out m)) minutes[e.Name] = m = new Minute();
                    if (e.Peak > m.MaxPeak) m.MaxPeak = e.Peak;
                    if (e.Peak > 0) { m.SoundSecs++; m.LastSound = DateTime.Now; }
                    foreach (var s in e.Sessions)
                        if (s.State == 1) m.Players.Add(s.Process + (s.Peak > 0 ? "" : "(silent)"));
                }
            }

            foreach (var kv in now)
            {
                string old;
                if (!prev.TryGetValue(kv.Key, out old)) Log("+ " + kv.Key + (kv.Value == "active" ? "" : ": " + kv.Value));
                else if (old != kv.Value) Log("~ " + kv.Key + ": " + old + " -> " + kv.Value);
            }
            foreach (var kv in prev)
                if (!now.ContainsKey(kv.Key)) Log("- " + kv.Key + (kv.Value == "active" ? " (stopped)" : " GONE"));
            prev = now;

            if (DateTime.Now >= nextSummary)
            {
                nextSummary = DateTime.Now.AddSeconds(60);
                foreach (var kv in minutes)
                {
                    if (!now.ContainsKey("output " + kv.Key)) continue;
                    var m = kv.Value;
                    Log("  minute " + kv.Key + ": loudest " + Audio.Db(m.MaxPeak) + ", sound in " + m.SoundSecs + "s of 60"
                        + ", apps " + (m.Players.Count == 0 ? "none" : string.Join(",", new List<string>(m.Players).ToArray())));
                }
                minutes.Clear();
            }
            Thread.Sleep(1000);
        }
    }

    // ---- play ----------------------------------------------------------------

    static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
    static Guid IID_Client = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static Guid IID_Render = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    static int Play(string match, string signal, int seconds)
    {
        var en = Audio.NewEnumerator();
        IMMDeviceCollection col; en.EnumAudioEndpoints(0, 1, out col);
        uint n; col.GetCount(out n);
        IMMDevice dev = null; string name = null;
        for (uint i = 0; i < n && dev == null; i++)
        {
            IMMDevice d; col.Item(i, out d);
            var nm = Audio.Name(d);
            if (nm.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) { dev = d; name = nm; }
            else Marshal.ReleaseComObject(d);
        }
        if (dev == null) { Log("no connected output matches '" + match + "'"); return 1; }

        object o; dev.Activate(ref IID_Client, 23, IntPtr.Zero, out o);
        var client = (IAudioClient)o;
        IntPtr fmt; client.GetMixFormat(out fmt);
        int tag = (ushort)Marshal.ReadInt16(fmt, 0), ch = (ushort)Marshal.ReadInt16(fmt, 2);
        int rate = Marshal.ReadInt32(fmt, 4), align = (ushort)Marshal.ReadInt16(fmt, 12), bits = (ushort)Marshal.ReadInt16(fmt, 14);
        bool isFloat = tag == 3;
        if (tag == 0xFFFE) { var g = new byte[16]; Marshal.Copy(new IntPtr(fmt.ToInt64() + 24), g, 0, 16); isFloat = new Guid(g) == SubtypeFloat; }
        if (!isFloat || bits != 32) { Log("mix format is not 32-bit float; this tool only writes float"); return 1; }

        int hr = client.Initialize(0, 0, 10000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Log("Initialize failed 0x" + hr.ToString("X8")); return 1; }
        uint bufFrames; client.GetBufferSize(out bufFrames);
        client.GetService(ref IID_Render, out o);
        var render = (IAudioRenderClient)o;

        var gen = Signal.Parse(signal, rate);
        Log("playing " + signal + " to " + name + " (" + rate + "Hz/" + ch + "ch)" + (seconds > 0 ? " for " + seconds + "s" : " until stopped"));
        var started = DateTime.Now; var nextNote = started.AddMinutes(1);
        client.Start();
        var samples = new float[bufFrames * ch];

        while (seconds <= 0 || (DateTime.Now - started).TotalSeconds < seconds)
        {
            uint pad;
            hr = client.GetCurrentPadding(out pad);
            if (hr != 0) { Log("output lost 0x" + hr.ToString("X8") + " after " + Elapsed(started)); return 3; }
            uint free = bufFrames - pad;
            if (free > 0)
            {
                IntPtr buf;
                hr = render.GetBuffer(free, out buf);
                if (hr != 0) { Log("output lost 0x" + hr.ToString("X8") + " after " + Elapsed(started)); return 3; }
                for (uint f = 0; f < free; f++) { float v = gen.Next(); for (int c = 0; c < ch; c++) samples[f * ch + c] = v; }
                Marshal.Copy(samples, 0, buf, (int)(free * ch));
                render.ReleaseBuffer(free, 0);
            }
            if (DateTime.Now >= nextNote) { nextNote = nextNote.AddMinutes(1); Log("  still playing, " + Elapsed(started)); }
            Thread.Sleep(50);
        }
        client.Stop();
        Log("finished after " + Elapsed(started) + ", output still connected");
        return 0;
    }

    // ---- record --------------------------------------------------------------

    // Opens a microphone and discards what it hears, which is all a game's voice chat
    // does to a Bluetooth speaker until someone talks: opening a speaker's microphone is
    // what moves it from its music profile to its hands-free one.
    static int Record(string match, int seconds)
    {
        var en = Audio.NewEnumerator();
        IMMDeviceCollection col; en.EnumAudioEndpoints(1, 1, out col);
        uint n; col.GetCount(out n);
        IMMDevice dev = null; string name = null;
        for (uint i = 0; i < n && dev == null; i++)
        {
            IMMDevice d; col.Item(i, out d);
            var nm = Audio.Name(d);
            if (nm.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) { dev = d; name = nm; }
            else Marshal.ReleaseComObject(d);
        }
        if (dev == null) { Log("no connected microphone matches '" + match + "'"); return 1; }

        object o; dev.Activate(ref IID_Client, 23, IntPtr.Zero, out o);
        var client = (IAudioClient)o;
        IntPtr fmt; client.GetMixFormat(out fmt);
        int hr = client.Initialize(0, 0, 10000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Log("Initialize failed 0x" + hr.ToString("X8")); return 1; }
        client.GetService(ref IID_Capture, out o);
        var capture = (IAudioCaptureClient)o;

        Log("recording from " + name + (seconds > 0 ? " for " + seconds + "s" : " until stopped"));
        var started = DateTime.Now; var nextNote = started.AddMinutes(1);
        client.Start();
        while (seconds <= 0 || (DateTime.Now - started).TotalSeconds < seconds)
        {
            uint packet;
            hr = capture.GetNextPacketSize(out packet);
            if (hr != 0) { Log("microphone lost 0x" + hr.ToString("X8") + " after " + Elapsed(started)); return 3; }
            while (packet > 0)
            {
                IntPtr data; uint frames; int flags; ulong pos, qpc;
                if (capture.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0) break;
                capture.ReleaseBuffer(frames);
                if (capture.GetNextPacketSize(out packet) != 0) break;
            }
            if (DateTime.Now >= nextNote) { nextNote = nextNote.AddMinutes(1); Log("  still recording, " + Elapsed(started)); }
            Thread.Sleep(50);
        }
        client.Stop();
        Log("finished after " + Elapsed(started) + ", microphone still connected");
        return 0;
    }

    static Guid IID_Capture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    static string Elapsed(DateTime since)
    {
        var t = DateTime.Now - since;
        return (int)t.TotalMinutes + "m" + t.Seconds.ToString("00") + "s";
    }
}

class Signal
{
    string _kind; double _amp, _step, _phase; int _rate, _every, _n; uint _r = 0x9E3779B9;

    public static Signal Parse(string s, int rate)
    {
        var p = s.Split(':');
        var g = new Signal { _kind = p[0], _rate = rate };
        if (p[0] == "noise") g._amp = Amp(p[1]);
        else if (p[0] == "sine") { g._step = 2 * Math.PI * double.Parse(p[1]) / rate; g._amp = Amp(p[2]); }
        else if (p[0] == "burst") { g._every = int.Parse(p[1]) * rate; g._amp = Amp(p[2]); }
        else if (p[0] != "silence") throw new ArgumentException("unknown signal " + s);
        return g;
    }

    static double Amp(string db) { return Math.Pow(10, double.Parse(db) / 20); }

    float Noise() { _r = _r * 1664525u + 1013904223u; return (float)(_amp * ((_r >> 8) / 8388608.0 - 1)); }

    public float Next()
    {
        switch (_kind)
        {
            case "noise": return Noise();
            case "sine": _phase += _step; return (float)(_amp * Math.Sin(_phase));
            case "burst": int at = _n++ % _every; return at < _rate ? Noise() : 0f;
            default: return 0f;
        }
    }
}
