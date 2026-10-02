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
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorClass { }

// [PreserveSig] on every method, without exception.
//
// Without it the CLR treats the declared int as a [retval] and turns a failing
// HRESULT into an exception, which makes every "if (hr != 0)" below dead code and
// reduces a diagnosable fault to whatever text the exception happens to carry - a
// Bluetooth endpoint being torn down logged as a bare "Not implemented", with no
// code and no clue which call produced it.
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig]
    int OpenPropertyStore(int access, out IntPtr store);
    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig]
    int GetState(out int state);
}

// WASAPI. The app renders its own silence through these rather than looping a file
// through a media player, because a media player is visible to the user: it publishes
// a transport session, which Windows shows as a "Speaker Keeper" card with
// play/next/previous in the media flyout and then routes media keys to. See Silence.
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                   IntPtr format, ref Guid sessionGuid);
    [PreserveSig]
    int GetBufferSize(out uint frames);
    [PreserveSig]
    int GetStreamLatency(out long latency);
    [PreserveSig]
    int GetCurrentPadding(out uint frames);
    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig]
    int GetMixFormat(out IntPtr format);
    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig]
    int Start();
    [PreserveSig]
    int Stop();
    [PreserveSig]
    int Reset();
    [PreserveSig]
    int SetEventHandle(IntPtr handle);
    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig]
    int ReleaseBuffer(uint frames, int flags);
}

// What every output and microphone is doing, for the log. See Activity. The enumerator
// above hands its collection back as a raw pointer, which is wrapped as this one.
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);
    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
    [PreserveSig]
    int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);
    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl2 session);
}

// IAudioSessionControl's methods first, in order: this is a vtable, not a list of
// the calls that happen to be used.
[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out int state);
    [PreserveSig]
    int GetDisplayName(out IntPtr name);
    [PreserveSig]
    int SetDisplayName(IntPtr name, IntPtr context);
    [PreserveSig]
    int GetIconPath(out IntPtr path);
    [PreserveSig]
    int SetIconPath(IntPtr path, IntPtr context);
    [PreserveSig]
    int GetGroupingParam(out Guid grouping);
    [PreserveSig]
    int SetGroupingParam(IntPtr grouping, IntPtr context);
    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr events);
    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr events);
    [PreserveSig]
    int GetSessionIdentifier(out IntPtr id);
    [PreserveSig]
    int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig]
    int GetProcessId(out uint pid);
    [PreserveSig]
    int IsSystemSoundsSession();
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    [PreserveSig]
    int GetPeakValue(out float peak);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig]
    int GetChannelCount(out int count);
    [PreserveSig]
    int SetMasterVolumeLevel(float db, IntPtr context);
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, IntPtr context);
    [PreserveSig]
    int GetMasterVolumeLevel(out float db);
    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float db, IntPtr context);
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float db);
    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig]
    int SetMute(bool mute, IntPtr context);
    [PreserveSig]
    int GetMute(out bool mute);
}

// Setting a default device. Undocumented, but it is what the Sound control panel itself
// calls, it has kept this shape since Windows 7, and every tool that switches outputs
// relies on it. Used only to move Windows' calls microphone off a speaker; see Calls.
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
class PolicyConfigClient { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    // Ten methods this app never calls, declared only to hold their vtable slots.
    [PreserveSig]
    int GetMixFormat();
    [PreserveSig]
    int GetDeviceFormat();
    [PreserveSig]
    int ResetDeviceFormat();
    [PreserveSig]
    int SetDeviceFormat();
    [PreserveSig]
    int GetProcessingPeriod();
    [PreserveSig]
    int SetProcessingPeriod();
    [PreserveSig]
    int GetShareMode();
    [PreserveSig]
    int SetShareMode();
    [PreserveSig]
    int GetPropertyValue();
    [PreserveSig]
    int SetPropertyValue();
    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
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
    // The devnode an endpoint hangs off. For a Bluetooth output that is either the A2DP
    // sink or the Hands-Free node, which is how the two are told apart - see IsA2dp.
    static readonly DEVPROPKEY Parent        = new DEVPROPKEY("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);
    // Bluetooth Class of Device: what the device says it is. Used instead of the endpoint
    // name because the name is localised and this is three bits of a number.
    static readonly DEVPROPKEY ClassOfDevice = new DEVPROPKEY("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 10);
    // Used to find the radios themselves. A Bluetooth-class devnode on the USB or PCI bus
    // is an adapter; everything paired to it lives on BTHENUM and friends instead.
    static readonly DEVPROPKEY DeviceClass   = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 9);
    static readonly DEVPROPKEY DeviceDesc    = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    static readonly DEVPROPKEY ProblemCode   = new DEVPROPKEY("4340a6c5-93fa-4706-972c-7b648008a5a7", 3);

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

    /// <summary>
    /// Whether an endpoint, output or microphone, belongs to a Bluetooth device.
    ///
    /// One property read rather than a full Read(), because Activity asks it of every
    /// endpoint that appears, microphones included, and Read() only knows outputs. The
    /// devnode behind a Bluetooth endpoint is always on one of the BTH* enumerators:
    /// BTHENUM for music, BTHHFENUM for the hands-free channel and its microphone.
    /// </summary>
    public static bool IsBluetoothEndpoint(string endpointId)
    {
        var parent = StringProperty(EndpointInstanceId(endpointId), Parent);
        return parent != null && parent.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A speaker's call channel: hung off BTHHFENUM, the Hands-Free profile. Its
    /// microphone always is, and so is the mono call output that some speakers publish
    /// beside their stereo one. Playing to that output is call mode just as surely as
    /// opening the microphone.
    /// </summary>
    public static bool IsHandsFreeEndpoint(string endpointId)
    {
        var parent = StringProperty(EndpointInstanceId(endpointId), Parent);
        return parent != null && parent.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every paired device's Hands-Free node: the part of a Bluetooth audio device that
    /// gives it a microphone and call mode. {0000111E} is the Hands-Free service.
    /// Disabled ones are included; they are still present, only switched off.
    /// </summary>
    public static List<string> HandsFreeNodes()
    {
        var list = new List<string>();
        foreach (var id in DeviceIds("BTHENUM"))
        {
            if (!id.StartsWith("BTHENUM" + (char)92 + "{0000111E", StringComparison.OrdinalIgnoreCase)) continue;
            if (GetProperty(id, ContainerId) == null) continue;   // not present: unpaired
            list.Add(id);
        }
        return list;
    }

    /// <summary>The device's CM_PROB_* code, 0 when it is working. 22 is disabled.</summary>
    public static uint ProblemOf(string instanceId)
    {
        var p = GetProperty(instanceId, ProblemCode);
        return p != null && p.Length >= 4 ? BitConverter.ToUInt32(p, 0) : 0;
    }

    public static bool ContainerOf(string instanceId, out Guid container)
    {
        container = Guid.Empty;
        var raw = GetProperty(instanceId, ContainerId);
        if (raw == null || raw.Length < 16) return false;
        container = new Guid(raw);
        return true;
    }

    /// <summary>One selectable audio output, as shown in the Settings device list.</summary>
    public class AudioDevice
    {
        public string EndpointId;      // "{0.0.0.00000000}.{guid}"
        public string Name;
        public Guid Container;         // stable per physical device - what settings key off
        public bool IsBluetooth;
        public bool Present = true;

        /// <summary>
        /// Which of a speaker's two channels this endpoint is.
        ///
        /// One Bluetooth speaker publishes two render endpoints: the A2DP sink, which is
        /// stereo and is what "Speakers (X)" means, and the Hands-Free one, which is the
        /// mono call channel. Holding a stream open on the Hands-Free endpoint would put
        /// the speaker into call mode and make music sound like a phone, so that one is
        /// never kept awake. They are told apart by their parent devnode rather than by
        /// name, because the names are localised.
        ///
        /// Unknown is the case that matters for hardware nobody here has: a Bluetooth
        /// stack that hangs its endpoints off something else entirely. Refusing to act on
        /// those would leave the app doing nothing at all and reporting the wrong reason,
        /// so an Unknown endpoint is used when its speaker publishes no A2DP one. Only
        /// HandsFree is ever ruled out outright, because that one does harm.
        /// </summary>
        public Channel Kind;

        public bool IsA2dp { get { return Kind == Channel.A2dp; } }

        /// <summary>The output Windows is currently playing through. Set by the policy.</summary>
        public bool IsDefault;
    }

    public enum Channel { Unknown, A2dp, HandsFree }

    /// <summary>A Bluetooth radio: the adapter itself, not anything paired to it.</summary>
    public class Radio
    {
        public string Name;
        public uint Problem;    // CM_PROB_*, 0 when the device is working

        /// <summary>CM_PROB_DISABLED: switched off by hand, not broken.</summary>
        public bool Disabled { get { return Problem == 22; } }
    }

    /// <summary>A paired Bluetooth device, and what it says it is.</summary>
    public class BluetoothDevice
    {
        public string Name;
        public uint ClassOfDevice;

        /// <summary>The radio address, as it appears in the device id. Used to tell two
        /// of the same model apart, since they arrive with the same name.</summary>
        public string Address;

        /// <summary>A headset, a speaker, anything whose job is sound. Not a mouse.</summary>
        public bool IsAudio { get { return ((ClassOfDevice >> 8) & 0x1F) == 4; } }

        /// <summary>
        /// Says outright that it is a loudspeaker. Stricter than IsSpeaker, on purpose.
        ///
        /// IsSpeaker decides what to keep awake, where guessing "speaker" for a device
        /// that says nothing is the safe mistake. This decides whose microphone to take
        /// away, where the safe mistake is the other one: a headset, a car kit or a
        /// conference speakerphone exists to be talked into. So only a device that names
        /// itself a loudspeaker (5), portable audio (7), hi-fi (10) or a TV with speakers
        /// (15) qualifies, and nothing that reports no class at all.
        /// </summary>
        public bool IsLoudspeaker
        {
            get
            {
                if (!IsAudio) return false;
                switch ((ClassOfDevice >> 2) & 0x3F)
                {
                    case 5: case 7: case 10: case 15: return true;
                    default: return false;
                }
            }
        }

        /// <summary>
        /// Whether this is something worth holding awake.
        ///
        /// Earbuds and headsets SHOULD power off when they are put away, so keeping them
        /// awake would flatten them in a drawer. The Class of Device says which is which:
        /// major class 4 is audio, and its minor class separates a loudspeaker from a pair
        /// of headphones. A device that reports nothing is treated as a speaker, because
        /// the app's whole job is to keep speakers awake and refusing to act on a silent
        /// device would be the worse failure.
        /// </summary>
        public bool IsSpeaker
        {
            get
            {
                if (ClassOfDevice == 0) return true;
                uint major = (ClassOfDevice >> 8) & 0x1F;
                if (major != 4) return true;
                switch ((ClassOfDevice >> 2) & 0x3F)
                {
                    case 1:  // wearable headset
                    case 2:  // hands-free
                    case 4:  // microphone
                    case 6:  // headphones
                        return false;
                    default:
                        return true;
                }
            }
        }
    }

    /// <summary>
    /// Every output and every paired Bluetooth device, read in one pass.
    ///
    /// Read as a set rather than queried one property at a time because the policy runs
    /// every five seconds and each lookup walks the device tree: asking three separate
    /// questions meant three walks a tick, and the answers could disagree with each other
    /// halfway through a speaker connecting.
    /// </summary>
    public class Snapshot
    {
        public List<AudioDevice> Outputs = new List<AudioDevice>();
        public Dictionary<Guid, BluetoothDevice> Bluetooth = new Dictionary<Guid, BluetoothDevice>();

        public BluetoothDevice Of(Guid container)
        {
            BluetoothDevice d;
            return Bluetooth.TryGetValue(container, out d) ? d : null;
        }

        /// <summary>
        /// Whether this is the endpoint to hold for its speaker.
        ///
        /// A2DP wins wherever a speaker publishes one. Where none does, an endpoint we
        /// could not classify is used rather than none at all: on a Bluetooth stack this
        /// code has never seen, refusing to act would leave the app silently doing
        /// nothing and reporting a reason that is not true. Hands-free is never used, on
        /// any stack, because that one does harm rather than nothing.
        /// </summary>
        public bool IsPlayable(AudioDevice d)
        {
            if (d.Kind == Channel.HandsFree) return false;
            if (d.Kind == Channel.A2dp) return true;
            foreach (var o in Outputs)
                if (o.Container == d.Container && o.Kind == Channel.A2dp) return false;
            return true;
        }

        /// <summary>The speaker's own name, rather than the endpoint's.</summary>
        public string NameOf(AudioDevice d)
        {
            var bt = Of(d.Container);
            return bt != null && !string.IsNullOrEmpty(bt.Name) ? bt.Name : d.Name;
        }
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
    /// Makes two of the same model tell themselves apart.
    ///
    /// A stereo pair is two identical speakers, so both arrive calling themselves the same
    /// thing, and a settings page with two rows reading "Xiaomi Sound Pocket" and a switch
    /// each is useless: there is no way to know which switch is which speaker. The last
    /// four digits of the radio address are printed after the name, and only when there is
    /// a clash, so the ordinary case stays clean.
    /// </summary>
    static void Disambiguate(Dictionary<Guid, BluetoothDevice> devices)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices.Values)
        {
            if (string.IsNullOrEmpty(d.Name)) continue;
            int n;
            counts[d.Name] = counts.TryGetValue(d.Name, out n) ? n + 1 : 1;
        }

        foreach (var d in devices.Values)
        {
            int n;
            if (string.IsNullOrEmpty(d.Name) || !counts.TryGetValue(d.Name, out n) || n < 2) continue;
            if (string.IsNullOrEmpty(d.Address) || d.Address.Length < 4) continue;
            d.Name += " (" + d.Address.Substring(d.Address.Length - 4) + ")";
        }
    }

    static string StringProperty(string instanceId, DEVPROPKEY key)
    {
        var raw = GetProperty(instanceId, key);
        return raw == null ? null : System.Text.Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    /// <summary>
    /// Every present render endpoint, and every Bluetooth device behind them.
    ///
    /// Enumerated through cfgmgr32 rather than IMMDeviceEnumerator so it reuses the same
    /// property plumbing as the battery lookup - render endpoints are the SWD\MMDEVAPI
    /// nodes whose id carries the {0.0.0.*} data-flow prefix ({0.0.1.*} would be capture).
    ///
    /// Devices that are paired but not connected drop out on their own: CM_Locate_DevNodeW
    /// is asked for present devnodes only, so every property of an absent one reads back
    /// null and it never reaches the list.
    /// </summary>
    public static Snapshot Read()
    {
        var snap = new Snapshot();
        var fromRoot = new HashSet<Guid>();

        // One speaker publishes several nodes - A2DP, Hands-Free, AVRCP - that all share a
        // container. The name and the Class of Device are taken from the device root node
        // ("BTHENUM\DEV_...", whose FriendlyName is "Xiaomi Sound Pocket") in preference to
        // a per-profile node ("... Hands-Free AG"), so the UI shows the speaker rather than
        // one of its profiles, and the class describes the speaker rather than the profile.
        foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHHFENUM" })
            foreach (var id in DeviceIds(enumerator))
            {
                var c = GetProperty(id, ContainerId);
                if (c == null || c.Length < 16) continue;
                var container = new Guid(c);

                bool isRoot = id.IndexOf((char)92 + "DEV_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (fromRoot.Contains(container) && !isRoot) continue;

                BluetoothDevice dev;
                if (!snap.Bluetooth.TryGetValue(container, out dev))
                    snap.Bluetooth[container] = dev = new BluetoothDevice();

                var name = StringProperty(id, FriendlyName);
                if (!string.IsNullOrEmpty(name) && (isRoot || string.IsNullOrEmpty(dev.Name)))
                    dev.Name = name;

                var cod = GetProperty(id, ClassOfDevice);
                if (cod != null && cod.Length >= 4)
                {
                    uint v = BitConverter.ToUInt32(cod, 0);
                    if (v != 0 && (isRoot || dev.ClassOfDevice == 0)) dev.ClassOfDevice = v;
                }

                if (isRoot && string.IsNullOrEmpty(dev.Address))
                {
                    int at = id.IndexOf((char)92 + "DEV_", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        int end = id.IndexOf((char)92, at + 5);
                        dev.Address = end < 0 ? id.Substring(at + 5) : id.Substring(at + 5, end - at - 5);
                    }
                }

                if (isRoot) fromRoot.Add(container);
            }

        Disambiguate(snap.Bluetooth);

        foreach (var id in DeviceIds("SWD"))
        {
            int cut = id.IndexOf("MMDEVAPI" + (char)92, StringComparison.OrdinalIgnoreCase);
            if (cut < 0) continue;

            string endpointId = id.Substring(cut + 9);
            if (!endpointId.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase)) continue;

            var dev = new AudioDevice();
            dev.EndpointId = endpointId;
            dev.Name = StringProperty(id, FriendlyName) ?? endpointId;

            var c = GetProperty(id, ContainerId);
            if (c != null && c.Length >= 16) dev.Container = new Guid(c);

            dev.IsBluetooth = dev.Container != Guid.Empty && snap.Bluetooth.ContainsKey(dev.Container);

            var parent = StringProperty(id, Parent);
            dev.Kind =
                parent == null ? Channel.Unknown
              : parent.IndexOf("{0000110b", StringComparison.OrdinalIgnoreCase) >= 0 ? Channel.A2dp
              : parent.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ? Channel.HandsFree
              : Channel.Unknown;

            snap.Outputs.Add(dev);
        }

        return snap;
    }

    /// <summary>
    /// The Bluetooth adapters attached to this PC right now.
    ///
    /// Windows drives exactly one of them, whatever is plugged in, and says so in the
    /// system log when it finds a second. Two adapters is therefore not a spare: it is a
    /// coin toss over which one your speaker ends up on, settled at boot. Worth knowing
    /// about, because a speaker that drops every few minutes on one of them behaves
    /// perfectly on the other, and nothing in the audio stack says why.
    ///
    /// Adapters that are unplugged do not appear: GetProperty resolves present devnodes
    /// only. One with a driver problem does, because it is attached, just not working.
    /// </summary>
    public static List<Radio> Radios()
    {
        var list = new List<Radio>();
        foreach (var bus in new[] { "USB", "PCI" })
            foreach (var id in DeviceIds(bus))
            {
                if (!string.Equals(StringProperty(id, DeviceClass), "Bluetooth",
                                   StringComparison.OrdinalIgnoreCase)) continue;

                var r = new Radio();
                r.Name = StringProperty(id, FriendlyName)
                      ?? StringProperty(id, DeviceDesc)
                      ?? "Bluetooth adapter";

                var p = GetProperty(id, ProblemCode);
                if (p != null && p.Length >= 4) r.Problem = BitConverter.ToUInt32(p, 0);

                list.Add(r);
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
    /// Move Windows' calls microphone off any speaker this app keeps awake. On unless
    /// turned off: a speaker in call mode switches itself off, and nothing this app sends
    /// can reach it there, so keeping it out of call mode is part of keeping it on.
    /// Per-user and needs no elevation, unlike Microphones, because it disables nothing.
    /// </summary>
    public static bool KeepSpeakersOutOfCalls
    {
        get { return Read("KeepSpeakersOutOfCalls", 1) != 0; }
        set { Write("KeepSpeakersOutOfCalls", value ? 1 : 0); }
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

        // Same for the microphone task, and give back every speaker microphone it took.
        // Uninstalling has to leave the speakers as the app found them; this must run
        // before the HKLM key goes, because that key is the list of what to give back.
        try
        {
            Microphones.RemoveTask();
            Microphones.RestoreAll();
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

    internal static int Run(string exe, string args)
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
/// Optionally takes the microphone away from Bluetooth speakers. Off unless asked for.
///
/// Most Bluetooth speakers have a microphone too, through their Hands-Free profile, and
/// an app opening it moves the speaker into call mode: mono, call quality, and on some
/// speakers a different idea of when to switch themselves off. Windows also makes that
/// microphone its Default Communication Device whenever the speaker connects. That is
/// the one Teams, Discord and game voice chat open, and Settings does not show it, so a
/// user can check their default microphone, see their webcam, and be right, while every
/// call goes through the speaker. See docs/INVESTIGATION-auto-off.md, F2.
///
/// Switching the Hands-Free profile off is the only fix that sticks. The role comes back
/// on every reconnect and the profile on every re-pair, so this is a SYSTEM task, like
/// the updater, that runs whenever Windows sets up a device and at startup, and switches
/// the profile off again. Only loudspeakers lose it: earbuds, headsets, car kits and
/// conference speakerphones exist to be talked into and are never touched.
///
/// Off by default, because other people's speakers are other people's business.
/// </summary>
static class Microphones
{
    public const string TaskName = "Speaker Keeper Microphones";
    const string FlagValue = "SpeakerMicrophonesOff";

    // The nodes this app switched off, by instance id. Turning the setting off restores
    // exactly these, and never a device the user disabled by hand for their own reasons.
    const string TurnedOffKey = Settings.MachineKey + "\\" + "MicrophonesTurnedOff";

    // Exit codes for --speaker-microphones, read by Settings.
    public const int Done = 0, Failed = 1, NotKeptOff = 4, Refused = -1;

    /// <summary>Whether speaker microphones should be off. Readable without elevation.</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Settings.MachineKey))
                    return k != null && Convert.ToInt32(k.GetValue(FlagValue, 0)) == 1;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Re-launches this exe elevated to make the change, the same way the auto-update
    /// switch does. Returns its exit code, or Refused if the prompt was dismissed.
    /// </summary>
    public static int SetElevated(bool off)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = Application.ExecutablePath;
            psi.Arguments = "--speaker-microphones " + (off ? "off" : "on");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

            var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return Refused;
            p.WaitForExit(90000);
            return p.ExitCode;
        }
        catch { return Refused; }
    }

    /// <summary>Runs elevated, from --speaker-microphones.</summary>
    public static int Apply(bool off)
    {
        return Apply(off, Path.GetDirectoryName(Application.ExecutablePath));
    }

    /// <summary>
    /// Records the choice, applies it now, and creates or removes the task that keeps it
    /// applied. Told where the install lives for the same reason Updater.Apply is: Setup
    /// runs from wherever Install.exe was saved.
    /// </summary>
    public static int Apply(bool off, string installDir)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Settings.MachineKey))
                if (k != null) k.SetValue(FlagValue, off ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);

            if (!off)
            {
                RemoveTask();
                Restore();
                Note("speaker microphones are back on");
                return Done;
            }

            Note("speaker microphones set to off");
            Enforce(TimeSpan.Zero);
            return CreateTask(installDir) ? Done : NotKeptOff;
        }
        catch (Exception ex) { Note("could not change speaker microphones: " + ex.Message); return Failed; }
    }

    /// <summary>Runs as SYSTEM, from the task.</summary>
    public static int EnforceFromTask()
    {
        if (!Enabled) return Done;
        // The task fires on the first device Windows sets up during a pairing, and the
        // Hands-Free node is not always that one, so watch for half a minute rather than
        // looking once and missing it.
        Enforce(TimeSpan.FromSeconds(30));
        return Done;
    }

    class Node
    {
        public string Id, Name;
        public bool Disabled, Loudspeaker;
    }

    static List<Node> Nodes()
    {
        var snap = DeviceProps.Read();
        var list = new List<Node>();
        foreach (var id in DeviceProps.HandsFreeNodes())
        {
            var n = new Node { Id = id, Name = id };
            n.Disabled = DeviceProps.ProblemOf(id) == 22;
            Guid c;
            var bt = DeviceProps.ContainerOf(id, out c) ? snap.Of(c) : null;
            if (bt != null)
            {
                if (!string.IsNullOrEmpty(bt.Name)) n.Name = bt.Name;
                n.Loudspeaker = bt.IsLoudspeaker;
            }
            list.Add(n);
        }
        return list;
    }

    /// <summary>Switches off the Hands-Free node of every paired loudspeaker that still has one.</summary>
    static void Enforce(TimeSpan watchFor)
    {
        var until = DateTime.UtcNow + watchFor;
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            foreach (var n in Nodes())
            {
                if (n.Disabled || !n.Loudspeaker || failed.Contains(n.Id)) continue;
                int err;
                if (SetDeviceEnabled(n.Id, false, out err))
                {
                    Remember(n);
                    Note("microphone off on " + n.Name);
                }
                else
                {
                    failed.Add(n.Id);   // once is enough to say so
                    Note("could not turn off the microphone on " + n.Name + ": error 0x" + err.ToString("X"));
                }
            }
            if (DateTime.UtcNow >= until) return;
            Thread.Sleep(3000);
        }
    }

    /// <summary>For the uninstaller: everything back as it was, task and setting included.</summary>
    public static void RestoreAll()
    {
        Restore();
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Settings.MachineKey, true))
                if (k != null && k.GetValue(FlagValue) != null) k.DeleteValue(FlagValue);
        }
        catch { }
    }

    /// <summary>Switches back on exactly the nodes this app switched off, then forgets them.</summary>
    static void Restore()
    {
        var ids = new List<string>();
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(TurnedOffKey))
                if (k != null) ids.AddRange(k.GetValueNames());
        }
        catch { }

        foreach (var id in ids)
        {
            int err;
            // 0xE000020B: no such device. It was unpaired since, and has nothing to restore.
            if (SetDeviceEnabled(id, true, out err) || err == unchecked((int)0xE000020B))
                Note("microphone back on: " + id);
            else
                Note("could not turn the microphone back on: " + id + ", error 0x" + err.ToString("X"));
        }

        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(TurnedOffKey, false); } catch { }
    }

    static void Remember(Node n)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(TurnedOffKey))
                if (k != null) k.SetValue(n.Id, n.Name);
        }
        catch { }
    }

    static bool CreateTask(string installDir)
    {
        string exe = Path.GetFullPath(Path.Combine(installDir, "SpeakerKeeper.exe"));

        // A SYSTEM task runs whatever is at this path. Pointing it anywhere a standard
        // user can write would hand SYSTEM to anyone able to replace one file, so it only
        // ever points into Program Files, which is admin-only. A copy run from anywhere
        // else still applies the setting once; it just cannot keep it applied.
        if (!IsProtected(exe) || !File.Exists(exe))
        {
            Note("not keeping microphones off automatically: " + exe + " is not an installed copy");
            return false;
        }

        string xml = TaskXml(exe);
        string tmp = Path.Combine(Path.GetTempPath(), "SpeakerKeeperMicrophones.xml");
        try
        {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            int rc = Updater.Run("schtasks.exe", "/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"");
            if (rc != 0) Note("could not create the task: schtasks exit " + rc);
            return rc == 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static void RemoveTask()
    {
        try { Updater.Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\""); } catch { }
    }

    static bool IsProtected(string path)
    {
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(f);
            if (string.IsNullOrEmpty(root)) continue;
            if (path.StartsWith(root.TrimEnd((char)92) + (char)92, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The task, as XML because schtasks' own switches allow one trigger and this needs two.
    ///
    /// Kernel-PnP/Configuration event 400 is "device configured", which Windows writes for
    /// every node a pairing creates, the Hands-Free one included. It is on by default.
    /// The boot trigger catches anything paired while the task did not exist.
    /// </summary>
    static string TaskXml(string exe)
    {
        string query = "<QueryList><Query Id=\"0\" Path=\"Microsoft-Windows-Kernel-PnP/Configuration\">"
                     + "<Select Path=\"Microsoft-Windows-Kernel-PnP/Configuration\">*[System[(EventID=400)]]</Select>"
                     + "</Query></QueryList>";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
             + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
             + "  <RegistrationInfo><Description>Keeps Bluetooth speakers from being used as microphones."
             + " Created by Speaker Keeper when Settings &gt; Speakers &gt; Turn off speaker microphones is on.</Description></RegistrationInfo>\r\n"
             + "  <Triggers>\r\n"
             + "    <BootTrigger><Enabled>true</Enabled></BootTrigger>\r\n"
             + "    <EventTrigger><Enabled>true</Enabled><Subscription>"
             + System.Security.SecurityElement.Escape(query) + "</Subscription></EventTrigger>\r\n"
             + "  </Triggers>\r\n"
             + "  <Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n"
             + "  <Settings>\r\n"
             // A pairing writes a burst of events. One run watches for 30 seconds, which
             // covers the burst, so the rest are dropped rather than queued.
             + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
             + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
             + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
             + "    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>\r\n"
             + "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n"
             + "    <Enabled>true</Enabled>\r\n"
             + "  </Settings>\r\n"
             + "  <Actions Context=\"System\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe)
             + "</Command><Arguments>--enforce-speaker-microphones</Arguments></Exec></Actions>\r\n"
             + "</Task>\r\n";
    }

    // SetupAPI's property-change request: what Device Manager's Disable and Enable do,
    // and what Disable-PnpDevice calls underneath. Global scope, so it persists.
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    // SP_CLASSINSTALL_HEADER (cbSize, InstallFunction) inlined at the front.
    [StructLayout(LayoutKind.Sequential)]
    struct SP_PROPCHANGE_PARAMS { public int cbSize; public int InstallFunction; public int StateChange; public int Scope; public int HwProfile; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr parent, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, int size);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    const int DIF_PROPERTYCHANGE = 0x12, DICS_ENABLE = 1, DICS_DISABLE = 2, DICS_FLAG_GLOBAL = 1;

    static bool SetDeviceEnabled(string instanceId, bool enable, out int error)
    {
        error = 0;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) { error = Marshal.GetLastWin32Error(); return false; }
        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
            { error = Marshal.GetLastWin32Error(); return false; }

            var p = new SP_PROPCHANGE_PARAMS();
            p.cbSize = 8;
            p.InstallFunction = DIF_PROPERTYCHANGE;
            p.StateChange = enable ? DICS_ENABLE : DICS_DISABLE;
            p.Scope = DICS_FLAG_GLOBAL;
            if (!SetupDiSetClassInstallParams(set, ref data, ref p, Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS))))
            { error = Marshal.GetLastWin32Error(); return false; }

            if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data))
            { error = Marshal.GetLastWin32Error(); return false; }
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>
    /// Writes to %ProgramData%\Speaker Keeper\microphones.log, next to the updater's log,
    /// because the task runs as SYSTEM and has no user's log to write to. When a user
    /// made the change from Settings it goes in their log as well.
    /// </summary>
    static void Note(string m)
    {
        try
        {
            if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
                Program.Log("microphones: " + m);
        }
        catch { }
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Speaker Keeper");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "microphones.log");
            if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine);
        }
        catch { }
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
    public const string IconMicrophone = "";

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
    public bool IsSpeaker = true;
    public int Battery = -1;
    public string Charge = "";
    public bool KeepingAwake;
    public int AlsoHeld;
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
        _keep.SetSilently(s.Container == Guid.Empty || DevicePolicy.IsEnabled(s.Container, s.IsSpeaker));
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
        // The panel names the output you are listening through, so a speaker held awake in
        // the background has to be mentioned or it is invisible until you switch to it.
        // "Idle" on its own would also be wrong while the app is holding something else.
        string status = _s.KeepingAwake ? "Keeping awake"
                      : _s.AlsoHeld > 0 ? "Keeping " + _s.AlsoHeld
                                          + (_s.AlsoHeld == 1 ? " other awake" : " others awake")
                      : "Idle";
        if (_s.KeepingAwake && _s.AlsoHeld > 0)
            status += " (and " + _s.AlsoHeld + " more)";
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

    readonly ToggleSwitch _runAtLogin, _warnLow, _autoUpdate, _mics, _outOfCalls;
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

        _outOfCalls = new ToggleSwitch();
        _outOfCalls.Toggled += (s, e) =>
        {
            if (_loading) return;
            Settings.KeepSpeakersOutOfCalls = _outOfCalls.On;
            Raise();
        };
        _speakers.Controls.Add(Card(Fluent.IconVolume, "Keep speakers out of calls",
            "Windows hands a speaker's microphone to meetings, calls and voice chat (Google Meet, "
            + "Zoom, Teams, Discord, games), which puts the speaker in call mode, where it switches "
            + "itself off. This moves them to your other microphone. You can still pick a speaker's "
            + "microphone in an app.", _outOfCalls));

        _mics = new ToggleSwitch();
        _mics.Toggled += (s, e) => OnMicrophonesToggled();
        _speakers.Controls.Add(Card(Fluent.IconMicrophone, "Turn off speaker microphones",
            "The sure way: no app can use a speaker as a microphone at all, even when picked by "
            + "hand, so meetings and games can never put it in call mode. Your other microphones "
            + "are used instead. Earbuds and headsets keep theirs. Needs administrator approval once.", _mics));

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

    /// <summary>For a notification about a speaker, which should land on the page that can fix it.</summary>
    public void ShowSpeakers() { Select(1); }

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

    void OnMicrophonesToggled()
    {
        if (_loading) return;
        bool off = _mics.On;
        int rc = Microphones.SetElevated(off);

        if (rc == Microphones.Done) { ReloadFromSettings(); return; }

        if (rc == Microphones.NotKeptOff)
        {
            // The setting took, and the microphones are off right now, but no task could be
            // created to keep them off. Say so rather than leave a switch that quietly stops
            // working the next time a speaker is re-paired.
            ReloadFromSettings();
            MessageBox.Show(this,
                "Speaker microphones are off now.\n\nThey will not be switched off again by "
                + "themselves after a speaker is re-paired, because this copy of Speaker Keeper "
                + "is not the one installed in Program Files. Install it with Install.exe and "
                + "turn this on again for that.",
                "Speaker Keeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _loading = true;
        _mics.SetSilently(!off);
        _loading = false;
        MessageBox.Show(this,
            (off ? "Speaker microphones were not turned off." : "Speaker microphones were not turned back on.")
            + "\n\n" + (rc == Microphones.Refused
                ? "This changes a Windows device setting and a scheduled task that runs as the "
                  + "system account, so Windows has to ask for permission. The permission prompt "
                  + "was dismissed or refused, so nothing was changed."
                : "Something went wrong (error " + rc + "). The log has the details."),
            "Speaker Keeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        // Everything after the heading, the note and the two microphone switches is a
        // device row from last time.
        for (int i = _speakers.Controls.Count - 1; i >= 4; i--)
        {
            var c = _speakers.Controls[i];
            _speakers.Controls.RemoveAt(i);
            c.Dispose();
        }

        var seen = new HashSet<Guid>();
        var rows = new List<SettingsCard>();
        int notices = 0;    // cards that are not a speaker, so the empty state still shows
        try
        {
            // Anything the machine is doing that stops the app working goes first, before
            // the switches, because a user looking at this page while their speaker keeps
            // dying is here to find out why and not to toggle anything.
            string advice = Health.Advice();
            if (advice != null)
            {
                rows.Add(Card(Fluent.IconWarning, "Your speaker keeps switching off", advice, null));
                notices++;
            }

            var adapters = Health.Adapters();
            if (adapters.Count > 1)
            {
                var names = new List<string>();
                foreach (var a in adapters)
                    names.Add(a.Name + (a.Problem != 0 ? " (driver problem " + a.Problem + ")" : ""));

                var card = Card(Fluent.IconBluetooth, "This PC has " + adapters.Count + " Bluetooth adapters",
                    string.Join("  ·  ", names.ToArray())
                    + ". Windows uses one of them at a time, chosen when the PC starts.", null);
                card.Muted = true;
                rows.Add(card);
                notices++;
            }

            // Rows are named after the physical Bluetooth device, not the audio endpoint:
            // one speaker exposes both "Speakers (X)" and "Headset Earphone (X Hands-Free)",
            // and only the first of those is a thing the user chose to own.
            var snap = DeviceProps.Read();

            foreach (var d in snap.Outputs)
            {
                if (!d.IsBluetooth || !snap.IsPlayable(d) || d.Container == Guid.Empty) continue;
                if (!seen.Add(d.Container)) continue;

                var bt = snap.Of(d.Container);
                string name = snap.NameOf(d);

                DevicePolicy.Remember(d.Container, name, bt == null ? 0 : bt.ClassOfDevice);
                rows.Add(DeviceRow(d.Container, name, true, bt == null || bt.IsSpeaker));
            }

            // A device that is paired but not connected still publishes its root node, so
            // its Class of Device is readable here even though it has no endpoint. Worth
            // storing: without it a pair of earbuds remembered by an older version would
            // sit in this list switched on until the next time they were connected.
            foreach (var r in DevicePolicy.All())
            {
                if (!seen.Add(r.Container)) continue;

                var bt = snap.Of(r.Container);
                if (bt != null && bt.ClassOfDevice != 0)
                {
                    DevicePolicy.Remember(r.Container, r.Name, bt.ClassOfDevice);
                    rows.Add(DeviceRow(r.Container, r.Name, false, bt.IsSpeaker));
                    continue;
                }

                rows.Add(DeviceRow(r.Container, r.Name, false, r.IsSpeaker));
            }

            // Paired but never yet connected while the app was running. Listing them means
            // a second speaker can be switched on before it is plugged in for the first
            // time, rather than appearing only once it is too late to be useful.
            foreach (var kv in snap.Bluetooth)
            {
                if (!kv.Value.IsAudio || string.IsNullOrEmpty(kv.Value.Name)) continue;
                if (!seen.Add(kv.Key)) continue;
                rows.Add(DeviceRow(kv.Key, kv.Value.Name, false, kv.Value.IsSpeaker));
            }
        }
        catch { }

        if (rows.Count == notices)
        {
            var empty = Card(Fluent.IconBluetooth, "No Bluetooth speakers found",
                "Pair a speaker and connect it, then come back here.", null);
            empty.Muted = true;
            rows.Add(empty);
        }

        foreach (var r in rows) _speakers.Controls.Add(r);
        _speakers.PerformLayout();
    }

    SettingsCard DeviceRow(Guid container, string name, bool present, bool isSpeaker)
    {
        var toggle = new ToggleSwitch();
        toggle.SetSilently(DevicePolicy.IsEnabled(container, isSpeaker));
        toggle.Toggled += (s, e) =>
        {
            DevicePolicy.SetEnabled(container, name, toggle.On);
            // Let the tray re-evaluate straight away rather than waiting for the next tick.
            BeginInvoke(new Action(Raise));
        };

        // Earbuds are off by default and say why, since a row that is simply off looks
        // like something the user did. The switch still works: a speaker that reports
        // itself as headphones can be turned on and stays on.
        string note = present ? "Connected" : "Not connected right now";
        if (!isSpeaker) note += "  ·  Earbuds, left to sleep unless you turn this on";

        var card = Card(Fluent.IconVolume, name, note, toggle);
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
            _mics.SetSilently(Microphones.Enabled);
            _outOfCalls.SetSilently(Settings.KeepSpeakersOutOfCalls);
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
        public bool IsSpeaker = true;
    }

    static string Sub(Guid container) { return Key + "\\" + container.ToString("B"); }

    /// <summary>
    /// Whether this device should be kept awake.
    ///
    /// Unset means "whatever suits this kind of device": a speaker defaults to on, since
    /// that is the whole point of the app, and earbuds default to off, since they are
    /// supposed to sleep when they are put away. A stored value always wins, so a user who
    /// turns a device on has said something the classification cannot overrule - which
    /// matters for the speaker that reports itself as headphones.
    /// </summary>
    public static bool IsEnabled(Guid container, bool byDefault)
    {
        if (container == Guid.Empty) return byDefault;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Sub(container)))
            {
                return Resolve(k, byDefault);
            }
        }
        catch { return byDefault; }
    }

    /// <summary>
    /// The stored answer, or the default for this kind of device.
    ///
    /// Versions up to 1.3.2 wrote Enabled=1 the first time they saw any device, so a 1 on
    /// its own does not mean the user asked for anything - and on a pair of earbuds it is
    /// exactly the setting this version is trying to stop applying. Only a value written
    /// by the switch carries the Chosen marker, and only that is allowed to override the
    /// default. A 0 is honoured either way: nothing ever wrote one but the switch.
    /// </summary>
    static bool Resolve(Microsoft.Win32.RegistryKey k, bool byDefault)
    {
        if (k == null) return byDefault;
        var v = k.GetValue("Enabled");
        if (v == null) return byDefault;
        if (Convert.ToInt32(v) == 0) return false;
        return k.GetValue("Chosen") != null || byDefault;
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
                // Marks this as the user's answer rather than one an old version wrote
                // on its own. See Resolve.
                k.SetValue("Chosen", 1, Microsoft.Win32.RegistryValueKind.DWord);
                if (!string.IsNullOrEmpty(name)) k.SetValue("Name", name);
            }
        }
        catch { }
    }

    /// <summary>
    /// Records a device we've seen, so it can still be listed when disconnected.
    ///
    /// The name only. Deliberately does not write Enabled: leaving it unset is what lets
    /// IsEnabled fall back to what suits the kind of device, and an earlier version that
    /// wrote 1 here froze that answer in the registry the first time a device appeared.
    /// </summary>
    public static void Remember(Guid container, string name, uint classOfDevice)
    {
        if (container == Guid.Empty || string.IsNullOrEmpty(name)) return;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Sub(container)))
            {
                if (k == null) return;
                k.SetValue("Name", name);
                // Kept so a device that is listed while disconnected still shows the right
                // default: the Class of Device is only readable while it is attached.
                if (classOfDevice != 0)
                    k.SetValue("Class", unchecked((int)classOfDevice),
                               Microsoft.Win32.RegistryValueKind.DWord);
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
                            var c = k.GetValue("Class");
                            bool speaker = c == null ||
                                new DeviceProps.BluetoothDevice
                                { ClassOfDevice = unchecked((uint)Convert.ToInt32(c)) }.IsSpeaker;

                            list.Add(new Remembered
                            {
                                Container = g,
                                Name = (k.GetValue("Name") as string) ?? g.ToString("B"),
                                Enabled = Resolve(k, speaker),
                                IsSpeaker = speaker,
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
class Silence
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

    volatile State _state = State.Idle;
    Thread _thread;
    ManualResetEvent _stop;
    DateTime _startedAt;
    DateTime _upAt;      // when the stream actually came up, for "held for" on a drop
    volatile string _error;
    volatile int _hr;

    readonly string _endpointId;
    readonly Guid _container;

    /// <summary>What to call this speaker in the log. Only ever used for that.</summary>
    public string Label;

    // How long to wait before trying this speaker again, and when that wait is up.
    // A stream that fails the instant it is opened - another app holding the endpoint
    // exclusively, a driver that will never accept it - would otherwise be rebuilt every
    // five seconds for as long as the app runs, writing a line each time.
    int _backoffSeconds;
    DateTime _retryAt = DateTime.MinValue;

    const int BackoffFirst = 5;
    const int BackoffMax = 300;

    public Silence(string endpointId, Guid container, string label)
    {
        _endpointId = endpointId;
        _container = container;
        Label = label;
    }

    public string EndpointId { get { return _endpointId; } }
    public Guid Container { get { return _container; } }

    /// <summary>False while a failed stream is serving out its backoff.</summary>
    public bool ReadyToRetry { get { return DateTime.UtcNow >= _retryAt; } }

    /// <summary>Seconds until the next attempt, for the log line that says so.</summary>
    public int RetryIn
    {
        get { return Math.Max(0, (int)Math.Round((_retryAt - DateTime.UtcNow).TotalSeconds)); }
    }

    /// <summary>Why the last stream stopped, or null if it stopped because we said so.</summary>
    public string LastError { get { return _error; } }

    /// <summary>
    /// True while the stream is up, or still coming up. A start that never completes -
    /// a wedged audio driver - stops counting as healthy, so the caller retries instead
    /// of waiting forever on a stream that is never going to arrive.
    /// </summary>
    public bool Active
    {
        get
        {
            var s = _state;
            if (s == State.Running) return true;
            return s == State.Starting && (DateTime.UtcNow - _startedAt).TotalSeconds < 30;
        }
    }

    public void Start()
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

    public void Stop()
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

    void Run()
    {
        IntPtr fmt = IntPtr.Zero;
        object en = null, dev = null, client = null, render = null;
        try
        {
            en = new MMDeviceEnumeratorClass();
            IMMDevice endpoint;

            // By id, not "the default output": the app holds every speaker the user asked
            // it to, and only one of them can be the default. Opening the default here
            // would also race the policy - the default can change between the decision to
            // hold a speaker and this thread getting as far as opening it.
            int opened = ((IMMDeviceEnumerator)en).GetDevice(_endpointId, out endpoint);
            if (opened != 0 || endpoint == null) { Fail("open device " + Hex(opened), opened); return; }
            dev = endpoint;

            var iid = typeof(IAudioClient).GUID;
            object obj;
            if (endpoint.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out obj) != 0 || obj == null)
            { Fail("cannot open the output"); return; }
            client = obj;
            var audio = (IAudioClient)obj;

            int hr = audio.GetMixFormat(out fmt);
            if (hr != 0) { Fail("mix format " + Hex(hr), hr); return; }

            // The engine's own mix format, so the stream needs no conversion and no
            // resampler - it is the cheapest thing that still counts as playback.
            var session = SessionId;
            hr = audio.Initialize(ShareModeShared, SessionFlags, BufferDuration, 0, fmt, ref session);
            if (hr != 0) { Fail("initialize " + Hex(hr), hr); return; }

            uint frames;
            hr = audio.GetBufferSize(out frames);
            if (hr != 0 || frames == 0) { Fail("buffer size " + Hex(hr), hr); return; }

            var riid = typeof(IAudioRenderClient).GUID;
            object svc;
            hr = audio.GetService(ref riid, out svc);
            if (hr != 0 || svc == null) { Fail("render client " + Hex(hr), hr); return; }
            render = svc;
            var buffer = (IAudioRenderClient)svc;

            if (!Push(buffer, frames)) { Fail("could not fill the buffer"); return; }

            hr = audio.Start();
            if (hr != 0) { Fail("start " + Hex(hr), hr); return; }

            _state = State.Running;
            // A stream that actually came up clears the debt from previous failures.
            _backoffSeconds = 0;
            _retryAt = DateTime.MinValue;
            _upAt = DateTime.UtcNow;
            Program.Log("keeping " + Label + " awake");

            while (!_stop.WaitOne(FeedMs))
            {
                uint pad;
                hr = audio.GetCurrentPadding(out pad);
                // The usual way out: the speaker disconnected or the endpoint was
                // reconfigured, which invalidates the stream. The caller rebuilds.
                if (hr != 0) { Fail("stream lost " + Hex(hr), hr); break; }
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

    // ReleaseComObject, never FinalReleaseComObject. Windows hands every caller in a process
    // the same device enumerator, so the CLR hands every caller the same wrapper for it,
    // and a final release here tore the enumerator out from under Activity and from under
    // any other speaker's stream that was starting at that moment. One release per
    // reference taken still frees the client and render objects this stream owns alone.
    static void Release(object o)
    {
        if (o == null) return;
        try { Marshal.ReleaseComObject(o); } catch { }
    }

    void Fail(string why) { Fail(why, 0); }

    void Fail(string why, int hr)
    {
        bool wasUp = _state == State.Running;
        _hr = hr;
        _error = why;
        _state = State.Failed;

        _backoffSeconds = _backoffSeconds == 0
            ? BackoffFirst
            : Math.Min(_backoffSeconds * 2, BackoffMax);
        _retryAt = DateTime.UtcNow.AddSeconds(_backoffSeconds);

        // How long it was held is the first thing to look at: a speaker that keeps going
        // at the same number of minutes has a timer in it, and that is not a Bluetooth fault.
        Program.Log(Label + ": " + why + (wasUp ? ", held for " + HeldFor : ""));

        // Only a stream that was actually up counts as the speaker dropping. One that
        // never started failed for some other reason, and calling that a disconnection
        // would put the wrong advice in front of the user.
        if (wasUp && Disconnected) Health.RecordDrop(_container, Label);
    }

    /// <summary>
    /// The HRESULT, and what it means when there is a plain way to say it.
    ///
    /// The code is always printed. A speaker dropping its Bluetooth link and a driver
    /// refusing the stream both end a stream, look identical in a log that says only
    /// "failed", and want completely different things done about them.
    /// </summary>
    static string Hex(int hr)
    {
        string code = "0x" + hr.ToString("X8");
        switch (unchecked((uint)hr))
        {
            // Values from audioclient.h. Getting one of these wrong is worse than
            // printing the bare number, so they are spelled out in full here.
            case 0x88890001: return code + " (AUDCLNT_E_NOT_INITIALIZED)";
            case 0x88890003: return code + " (AUDCLNT_E_WRONG_ENDPOINT_TYPE)";
            case 0x88890004: return code + " (the speaker disconnected)";
            case 0x88890008: return code + " (the output refused the format)";
            case 0x8889000A: return code + " (another app has the output in exclusive mode)";
            case 0x8889000E: return code + " (exclusive mode is not allowed on this output)";
            case 0x8889000F: return code + " (Windows could not create the endpoint)";
            case 0x88890010: return code + " (the Windows audio service is not running)";
            case 0x80070005: return code + " (access denied)";
            case 0x80004001: return code + " (not implemented by the driver)";
            default: return code;
        }
    }

    /// <summary>
    /// True when the stream ended because the speaker went away, rather than because
    /// something about the output refused it. The two want opposite things said about
    /// them, so this reads the HRESULT rather than the sentence built from it.
    /// </summary>
    public bool Disconnected { get { return _hr == unchecked((int)0x88890004); } }

    /// <summary>How long the stream has been up, as "14m55s".</summary>
    public string HeldFor
    {
        get
        {
            var t = DateTime.UtcNow - _upAt;
            return t.TotalHours >= 1
                ? (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m"
                : (int)t.TotalMinutes + "m" + t.Seconds.ToString("00") + "s";
        }
    }
}

/// <summary>
/// Watches for the things that stop this app working which this app cannot fix.
///
/// A speaker that drops its Bluetooth link every few minutes looks exactly like a speaker
/// idling off: it goes quiet, and the app that was supposed to stop that gets the blame.
/// The difference is only visible from here, because this is the one process holding a
/// stream open and watching it die. So when the pattern is unmistakable, say so, and name
/// the thing on the machine most likely to be causing it.
///
/// Nothing here is acted on automatically. Removing a Bluetooth adapter can take a mouse
/// or a keyboard with it, and that is not a decision a tray app gets to make.
/// </summary>
static class Health
{
    /// <summary>
    /// What counts as "this keeps happening".
    ///
    /// Two rules rather than one. Three drops in an hour is obviously broken, but so is a
    /// speaker that drops every couple of hours all evening, and a single one-hour window
    /// never sees the second: it only ever holds one of them. A speaker switched off by
    /// hand once or twice trips neither.
    /// </summary>
    struct Rule { public int Drops; public TimeSpan Within; }

    static readonly Rule[] Rules =
    {
        new Rule { Drops = 3, Within = TimeSpan.FromHours(1) },
        new Rule { Drops = 4, Within = TimeSpan.FromHours(6) },
    };

    static readonly TimeSpan Window = TimeSpan.FromHours(6);   // longest rule; what we keep

    class Flapping { public string Name; public List<DateTime> At = new List<DateTime>(); }

    /// <summary>A reading taken under the lock, safe to walk afterwards.</summary>
    class Reading { public Guid Container; public string Name; public int Count; public TimeSpan Within; }

    static readonly Dictionary<Guid, Flapping> _drops = new Dictionary<Guid, Flapping>();
    static readonly object _lock = new object();

    /// <summary>Set once the user has been told, so the notification cannot nag.</summary>
    public static bool Announced;

    /// <summary>Called from the stream thread, so everything here is locked.</summary>
    public static void RecordDrop(Guid container, string name)
    {
        if (container == Guid.Empty) return;
        lock (_lock)
        {
            Flapping f;
            if (!_drops.TryGetValue(container, out f)) _drops[container] = f = new Flapping();
            f.Name = name;
            f.At.Add(DateTime.UtcNow);

            var cutoff = DateTime.UtcNow - Window;
            f.At.RemoveAll(delegate (DateTime t) { return t < cutoff; });
        }
    }

    /// <summary>The worst offender in the last hour, or null when nothing is wrong.</summary>
    static Reading Worst()
    {
        lock (_lock)
        {
            Reading worst = null;
            foreach (var kv in _drops)
                foreach (var rule in Rules)
                {
                    var f = kv.Value;
                    var cutoff = DateTime.UtcNow - rule.Within;
                    int n = f.At.FindAll(delegate (DateTime t) { return t >= cutoff; }).Count;
                    if (n < rule.Drops) continue;
                    if (worst == null || n > worst.Count)
                        worst = new Reading { Container = kv.Key, Name = f.Name, Count = n, Within = rule.Within };
                }
            return worst;
        }
    }

    /// <summary>
    /// What to tell the user, or null when there is nothing to tell them.
    ///
    /// Silent unless a speaker is actually dropping. Plenty of machines have two Bluetooth
    /// adapters and work perfectly well, and warning those users about a problem they do
    /// not have would make every later warning easier to ignore.
    /// </summary>
    public static string Advice()
    {
        var worst = Worst();
        if (worst == null) return null;

        string times = worst.Count + " times in the last "
                       + (worst.Within.TotalHours <= 1 ? "hour" : worst.Within.TotalHours + " hours");

        // What the last drop looked like decides what to say, because the three causes
        // want three different things done. This used to go straight to the Bluetooth
        // connection and the adapters, which sent the user this was written for off to
        // remove hardware while the real cause was a microphone being opened by a game.
        var last = Activity.LastDeparture(worst.Container);
        if (last != null && last.InCall)
            return worst.Name + " has switched off " + times + ", the last time while "
                   + last.CallBy + " was using it as a microphone. A speaker used as a microphone"
                   + " switches itself off after about 15 minutes, whatever is playing. Turn on"
                   + " Turn off speaker microphones above and no app can do that to it again.";

        if (last != null && last.TimerMinutes > 0)
            return worst.Name + " has switched off " + times + ", the last time "
                   + last.TimerMinutes + " minutes after it last played anything. That is the"
                   + " speaker's own auto-off timer, not the Bluetooth connection.";

        var lines = new List<string>();
        lines.Add(worst.Name + " has disconnected " + times + ". Speaker Keeper reconnects it"
                  + " each time, but the drops are the Bluetooth connection itself, not the app.");

        var radios = Adapters();
        var broken = radios.FindAll(delegate (DeviceProps.Radio r) { return r.Problem != 0; });

        if (radios.Count > 1)
            lines.Add("This PC has " + radios.Count + " Bluetooth adapters attached and"
                      + " Windows only ever uses one of them, chosen at startup. Removing"
                      + " the one you are not using is the usual fix.");

        foreach (var r in broken)
            lines.Add(r.Name + " has a driver problem (code " + r.Problem
                      + ") and is not being used.");

        return string.Join(" ", lines.ToArray());
    }

    /// <summary>
    /// The adapters Windows could use, for the advice and the line in Settings.
    ///
    /// A disabled one is left out. It is code 22, which reads like a fault, but it is
    /// someone having switched it off in Device Manager, and Windows will not pick it at
    /// startup, so it cannot be the adapter a speaker is dropping on. Counting it told a
    /// user who had already fixed a two-adapter problem to go and fix it again.
    /// </summary>
    public static List<DeviceProps.Radio> Adapters()
    {
        try { return DeviceProps.Radios().FindAll(delegate (DeviceProps.Radio r) { return !r.Disabled; }); }
        catch { return new List<DeviceProps.Radio>(); }
    }
}

/// <summary>
/// What every output and microphone is doing, written to the log as it changes.
///
/// The log used to record only what this app did. That left the one question every
/// "it still switches off" report is about unanswerable from the log: when the speaker
/// went, was anything actually playing to it, and had something opened its microphone?
/// Both matter more than anything the app does. Voice chat opening a speaker's
/// microphone moves the speaker onto its hands-free profile, call mode, and a speaker
/// whose auto-off listens for music can then switch itself off with a game still
/// audible through it. See docs/INVESTIGATION-auto-off.md for how this was found.
///
/// Sampled once a second on its own thread, never the UI one: the audio service can
/// stall, and a stalled tray icon is worse than a missed sample. Nothing here changes
/// anything. It only watches, so a failure costs a log line and nothing else.
/// </summary>
static class Activity
{
    const int SampleMs = 1000;

    // An app has to have been playing, or stopped, for this long before it is logged.
    // A browser flips its session on every pause, and a line per pause would bury the
    // lines that matter.
    const int SettleSeconds = 10;

    // A microphone is logged sooner. It opening is the event most likely to explain a
    // drop, and it is rare enough that there is no chatter to filter out.
    const int MicSettleSeconds = 2;

    // Volume is logged once the slider has stopped moving, not at every step of a drag.
    const int VolumeSettleSeconds = 3;

    const int SummaryMinutes = 5;
    const int FormatEverySeconds = 15;

    // -70 dB. Quieter than this counts as silence: some apps keep a stream running with a
    // trace of noise in it, and calling that sound would hide the answer being looked for.
    const float Audible = 0.0003f;

    // How close to a round number of minutes a drop has to land to be called a timer.
    // The speakers this was found on went at 14m54s to 15m20s, every time.
    const int TimerSlackSeconds = 30;

    class Endpoint
    {
        public string Id, Name;
        public bool Capture, Bluetooth;
        public bool HandsFree;   // a speaker's call channel: its microphone, or its mono call output
        public Guid Container;
        public IMMDevice Device;
        public IAudioEndpointVolume Volume;
        public IAudioSessionManager2 Sessions;
        public string Format;

        // False for anything already connected when the app started: then Since is when
        // watching began, and "connected for" would be a guess dressed up as a fact.
        public bool SinceIsConnect;
        public DateTime Since;

        public float Level = -1, PendingLevel = -1;
        public bool Muted, PendingMuted;
        public DateTime PendingAt;

        public readonly Dictionary<string, App> Apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);

        // Outputs only: what the speaker has actually been hearing, apart from us.
        public DateTime LastSound = DateTime.MinValue, LastMusicSound = DateTime.MinValue;
        public string LastSoundFrom;
        public int SoundSeconds;
        public float Loudest;
        public readonly HashSet<string> Heard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One process on one endpoint. Several sessions of one app count as one.</summary>
    class App
    {
        public bool Present, ActiveNow;   // this sample
        public bool Seen;                 // settled-in-progress state
        public DateTime Changed;          // when Seen last flipped
        public bool Logged;               // what the log last said
    }

    /// <summary>A speaker with its microphone open, which is call mode.</summary>
    class Call
    {
        public DateTime Since;
        public DateTime Ended = DateTime.MaxValue;
        public readonly List<string> By = new List<string>();
        public string Was = "";
    }

    /// <summary>How a speaker's last disconnection looked. Read by Health.</summary>
    public class Departure
    {
        public DateTime At;
        public string Name;
        public bool InCall;
        public string CallBy;
        public int TimerMinutes;   // 0 unless it went a round number of minutes after its last sound
    }

    static Thread _thread;
    static int _ownPid;
    static readonly Dictionary<string, Endpoint> _eps = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> _roles = new Dictionary<string, string>();
    static readonly Dictionary<Guid, Call> _calls = new Dictionary<Guid, Call>();
    static readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();

    static readonly object _lock = new object();
    static readonly Dictionary<Guid, Departure> _departures = new Dictionary<Guid, Departure>();

    public static Departure LastDeparture(Guid container)
    {
        lock (_lock)
        {
            Departure d;
            return _departures.TryGetValue(container, out d) ? d : null;
        }
    }

    public static void Start()
    {
        if (_thread != null) return;
        _ownPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        _thread = new Thread(Run);
        _thread.IsBackground = true;
        _thread.Name = "Speaker Keeper activity";
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    static void Run()
    {
        IMMDeviceEnumerator en = null;
        bool first = true;
        var nextSummary = DateTime.Now.AddMinutes(SummaryMinutes);
        var nextFormat = DateTime.MinValue;
        var quietUntil = DateTime.MinValue;

        while (true)
        {
            try
            {
                if (en == null) en = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();

                bool formats = DateTime.Now >= nextFormat;
                if (formats) nextFormat = DateTime.Now.AddSeconds(FormatEverySeconds);

                Sample(en, first, formats);
                first = false;

                if (DateTime.Now >= nextSummary)
                {
                    nextSummary = DateTime.Now.AddMinutes(SummaryMinutes);
                    Summarise();
                }
            }
            catch (Exception ex)
            {
                // At most once every ten minutes: a broken audio service would otherwise
                // put this in the log every second until it came back.
                if (DateTime.Now >= quietUntil)
                {
                    Program.Log("activity: sample failed: " + ex.GetType().Name + ": " + ex.Message + Where(ex));
                    quietUntil = DateTime.Now.AddMinutes(10);
                }
                foreach (var e in _eps.Values) Close(e);
                _eps.Clear();
                Release(en);
                en = null;

                // Everything is about to be opened again. Those endpoints did not just
                // connect, so the next pass must say "present", not "connected", or the
                // next disconnection would be timed from this reset.
                first = true;
            }
            Thread.Sleep(SampleMs);
        }
    }

    static void Sample(IMMDeviceEnumerator en, bool first, bool formats)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (int flow in new[] { 0, 1 })
        {
            IntPtr raw;
            if (en.EnumAudioEndpoints(flow, 1, out raw) != 0 || raw == IntPtr.Zero) continue;
            var col = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            try
            {
                uint n;
                if (col.GetCount(out n) != 0) continue;
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    string id;
                    if (d.GetId(out id) != 0 || id == null) { Release(d); continue; }
                    seen.Add(id);
                    if (_eps.ContainsKey(id)) { Release(d); continue; }
                    _eps[id] = Open(d, id, flow == 1, first);
                }
            }
            finally { Release(col); }
        }

        // Microphones before outputs. A speaker powering off takes its microphone first,
        // a second ahead of its output, and the output's farewell line needs to know the
        // microphone was open when it went.
        foreach (bool capture in new[] { true, false })
            foreach (var id in new List<string>(_eps.Keys))
            {
                var e = _eps[id];
                if (e.Capture != capture || seen.Contains(id)) continue;
                Gone(e);
                Close(e);
                _eps.Remove(id);
            }

        foreach (var e in _eps.Values)
        {
            try { Watch(e, formats); }
            catch { }   // one endpoint going away mid-read must not cost the others their sample
        }

        // Before Role, so a speaker Windows has just made the calls microphone is moved
        // back within the second, and the brief assignment never reaches the log as a
        // settled change. Calls writes its own line saying what it did.
        Calls(en);

        Role(en, 0, 2, "default output for calls", first);
        Role(en, 1, 0, "default microphone", first);
        Role(en, 1, 2, "default microphone for calls", first);
    }

    // --- keeping speakers out of calls -------------------------------------------------
    //
    // A speaker in call mode switches itself off about 15 minutes in, game audible or not,
    // and nothing this app sends can reach it there: Windows suspends the music channel the
    // silence travels on for as long as an app holds the microphone (INVESTIGATION F2).
    // Windows makes a speaker's microphone its calls microphone every time it connects, and
    // that is what voice chat opens. So the calls role is moved straight back off it.
    // Nothing is disabled; a user who picks the speaker's microphone in an app still gets
    // it, and is told what it will do (Notice). Microphones is the stronger, opt-in version.

    // Recent moves per role, so a fight with something that keeps setting the speaker back
    // (another audio tool, a vendor utility) ends in one log line and a stand-down rather
    // than two programs flipping the default every second.
    static readonly Dictionary<int, List<DateTime>> _moves = new Dictionary<int, List<DateTime>>();
    static readonly Dictionary<int, DateTime> _standDown = new Dictionary<int, DateTime>();
    static readonly HashSet<string> _nowhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    const int MaxMovesPerMinute = 5;

    static void Calls(IMMDeviceEnumerator en)
    {
        if (!Settings.KeepSpeakersOutOfCalls) return;

        // Both roles of each. Teams, Discord and games open the calls microphone, but a
        // browser meeting (Google Meet) opens the ordinary default one, and Windows hands
        // either to a speaker when it connects.
        var roles = new[] { new[] { 1, 2 }, new[] { 1, 0 }, new[] { 0, 2 }, new[] { 0, 0 } };
        var ids = new string[roles.Length];
        bool any = false;
        for (int i = 0; i < roles.Length; i++)
        {
            ids[i] = DefaultId(en, roles[i][0], roles[i][1]);
            any |= IsBluetooth(ids[i]);
        }
        if (!any) return;   // the ordinary case, decided without a device walk

        var snap = DeviceProps.Read();
        for (int i = 0; i < roles.Length; i++)
            if (IsBluetooth(ids[i])) Steer(en, snap, roles[i][0], roles[i][1], ids[i]);
    }

    static void Steer(IMMDeviceEnumerator en, DeviceProps.Snapshot snap, int flow, int role, string id)
    {
        Endpoint e;
        if (!_eps.TryGetValue(id, out e) || !Guarded(snap, e.Container)) return;

        // For the output only the hands-free channel matters. A speaker's stereo output
        // being the calls output is harmless: playing to it needs no microphone. Speakers
        // that publish a separate hands-free output are the ones where voice chat would
        // otherwise pick that and flip the speaker into call mode just by playing.
        if (flow == 0 && !IsHandsFreeOutput(snap, id)) return;

        int key = flow * 10 + role;
        DateTime until;
        if (_standDown.TryGetValue(key, out until) && DateTime.Now < until) return;

        string what = (role == 2 ? "calls " : "default ") + (flow == 1 ? "microphone" : "output");
        string target = Replacement(en, snap, flow, role, id, e.Container);
        if (target == null)
        {
            if (_nowhere.Add(id))
                Program.Log("Windows' " + what + " is " + e.Name + " and there is nothing else to move it"
                            + " to; voice chat will put that speaker in call mode");
            return;
        }

        List<DateTime> moves;
        if (!_moves.TryGetValue(key, out moves)) _moves[key] = moves = new List<DateTime>();
        moves.RemoveAll(delegate (DateTime t) { return (DateTime.Now - t).TotalSeconds > 60; });
        if (moves.Count >= MaxMovesPerMinute)
        {
            _standDown[key] = DateTime.Now.AddMinutes(10);
            moves.Clear();
            Program.Log("something keeps setting Windows' " + what + " back to " + e.Name
                        + "; leaving it alone for 10 minutes");
            return;
        }
        moves.Add(DateTime.Now);

        int hr = SetDefault(target, role);
        string to = DeviceProps.EndpointName(target) ?? target;
        Program.Log(hr == 0
            ? "moved Windows' " + what + " from " + e.Name + " to " + to + ", so voice chat cannot put"
              + " the speaker in call mode (Settings > Speakers > Keep speakers out of calls)"
            : "could not move Windows' " + what + " off " + e.Name + ": 0x" + hr.ToString("X8"));
    }

    /// <summary>A loudspeaker this app keeps awake. Earbuds and switched-off speakers are left be.</summary>
    static bool Guarded(DeviceProps.Snapshot snap, Guid container)
    {
        if (container == Guid.Empty) return false;
        var bt = snap.Of(container);
        return bt != null && bt.IsLoudspeaker && DevicePolicy.IsEnabled(container, bt.IsSpeaker);
    }

    static bool IsGuardedEndpoint(DeviceProps.Snapshot snap, string id)
    {
        Endpoint e;
        return id != null && _eps.TryGetValue(id, out e) && e.Bluetooth && Guarded(snap, e.Container);
    }

    static bool IsHandsFreeOutput(DeviceProps.Snapshot snap, string id)
    {
        foreach (var o in snap.Outputs)
            if (string.Equals(o.EndpointId, id, StringComparison.OrdinalIgnoreCase))
                return o.Kind == DeviceProps.Channel.HandsFree;
        return false;
    }

    /// <summary>
    /// Where the calls role goes instead. For the microphone, the user's ordinary default
    /// microphone, which is the one they chose; failing that any other microphone, wired
    /// ones first. For the output, the ordinary default output, or the same speaker's
    /// stereo output, which is the speaker without its call mode.
    /// </summary>
    static string Replacement(IMMDeviceEnumerator en, DeviceProps.Snapshot snap, int flow, int role, string id, Guid container)
    {
        string usual = DefaultId(en, flow, role == 2 ? 0 : 2);
        if (flow == 1)
        {
            if (usual != null && usual != id && !IsGuardedEndpoint(snap, usual)) return usual;
            string bluetooth = null;
            foreach (var e in _eps.Values)
            {
                if (!e.Capture || e.Id == id || IsGuardedEndpoint(snap, e.Id)) continue;
                if (!e.Bluetooth) return e.Id;
                if (bluetooth == null) bluetooth = e.Id;
            }
            return bluetooth;
        }

        if (usual != null && usual != id && !IsHandsFreeOutput(snap, usual)) return usual;
        foreach (var o in snap.Outputs)
            if (o.Container == container && o.Kind == DeviceProps.Channel.A2dp) return o.EndpointId;
        return null;
    }

    /// <summary>
    /// The ordinary default (console) moves the multimedia role with it, as the Sound
    /// control panel does, so the two never disagree about which microphone is "the" one.
    /// </summary>
    static int SetDefault(string id, int role)
    {
        object o = null;
        try
        {
            o = new PolicyConfigClient();
            var pc = (IPolicyConfig)o;
            int hr = pc.SetDefaultEndpoint(id, role);
            if (hr == 0 && role == 0) pc.SetDefaultEndpoint(id, 1);
            return hr;
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
        finally { Release(o); }
    }

    static bool IsBluetooth(string id)
    {
        Endpoint e;
        return id != null && _eps.TryGetValue(id, out e) && e.Bluetooth;
    }

    static string DefaultId(IMMDeviceEnumerator en, int flow, int role)
    {
        IMMDevice d;
        if (en.GetDefaultAudioEndpoint(flow, role, out d) != 0 || d == null) return null;
        try
        {
            string id;
            return d.GetId(out id) == 0 ? id : null;
        }
        finally { Release(d); }
    }

    // --- telling the user ----------------------------------------------------------------

    /// <summary>A notification for the tray to show. Built here, shown on the UI thread.</summary>
    public class Notice { public string Title, Text; }

    static Notice _notice;
    static readonly Dictionary<Guid, DateTime> _noticed = new Dictionary<Guid, DateTime>();

    /// <summary>Called from the tray's tick, which owns the notification icon.</summary>
    public static Notice TakeNotice()
    {
        lock (_lock)
        {
            var n = _notice;
            _notice = null;
            return n;
        }
    }

    /// <summary>
    /// An app has opened a kept-awake speaker's microphone anyway, so it is in call mode
    /// and will switch itself off. Said once per speaker per half hour: a voice channel
    /// rejoined five times an evening is not five pieces of news.
    /// </summary>
    static void NoticeCall(Endpoint mic, string app)
    {
        var snap = DeviceProps.Read();
        if (!Guarded(snap, mic.Container)) return;

        DateTime last;
        if (_noticed.TryGetValue(mic.Container, out last) && (DateTime.Now - last).TotalMinutes < 30) return;
        _noticed[mic.Container] = DateTime.Now;

        var bt = snap.Of(mic.Container);
        string speaker = bt != null && !string.IsNullOrEmpty(bt.Name) ? bt.Name : mic.Name;

        // Plain words, short enough to be read whole. The first version said "call mode",
        // which means nothing to the person it is for, and ran past what the notification
        // shows, so the part that said what to do was the part cut off.
        string title = FriendlyName(app) + " is using your speaker as a microphone";
        if (title.Length > 63) title = "An app is using your speaker as a microphone";

        lock (_lock)
            _notice = new Notice
            {
                Title = title,
                Text = speaker + " will switch itself off in about 15 minutes while this lasts."
                     + " Click to stop apps doing this."
            };
    }

    /// <summary>
    /// What the app calls itself, "Google Chrome" rather than "chrome.exe", from the
    /// description in its own exe. Falls back to the process name for anything it cannot
    /// read, such as an elevated process.
    /// </summary>
    static string FriendlyName(string process)
    {
        string bare = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? process.Substring(0, process.Length - 4) : process;
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(bare))
                using (p)
                {
                    // Trimmed before the check: an exe can carry a description of one space,
                    // and that made a notification read " is using your speaker".
                    var d = (p.MainModule.FileVersionInfo.FileDescription ?? "").Trim();
                    if (d.Length > 0) return d;
                }
        }
        catch { }
        return bare;
    }

    static Endpoint Open(IMMDevice d, string id, bool capture, bool first)
    {
        var e = new Endpoint();
        e.Id = id;
        e.Device = d;
        e.Capture = capture;
        e.Since = DateTime.Now;
        e.SinceIsConnect = !first;
        e.Name = DeviceProps.EndpointName(id) ?? id;
        e.Bluetooth = DeviceProps.IsBluetoothEndpoint(id);
        e.HandsFree = DeviceProps.IsHandsFreeEndpoint(id);
        Guid c;
        if (DeviceProps.TryContainer(id, out c)) e.Container = c;

        object o;
        var iid = typeof(IAudioSessionManager2).GUID;
        if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) e.Sessions = o as IAudioSessionManager2;
        if (!capture)
        {
            iid = typeof(IAudioEndpointVolume).GUID;
            if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) e.Volume = o as IAudioEndpointVolume;
            ReadVolume(e, out e.Level, out e.Muted);
        }
        e.Format = FormatOf(d);

        if (e.Bluetooth)
            Program.Log((first ? "present: " : "connected: ") + Describe(e));
        return e;
    }

    static string Describe(Endpoint e)
    {
        string s = e.Name + (e.Format != null ? ", " + e.Format : "");
        if (!e.Capture && e.Level >= 0) s += ", volume " + Percent(e.Level) + (e.Muted ? " (muted)" : "");
        return s;
    }

    static void Watch(Endpoint e, bool formats)
    {
        var now = DateTime.Now;

        if (e.Bluetooth && !e.Capture) WatchVolume(e, now);

        if (formats && e.Bluetooth)
        {
            // A speaker changing format is it changing profile: stereo music one moment,
            // 16 kHz mono calls the next.
            var f = FormatOf(e.Device);
            if (f != null && f != e.Format)
            {
                Program.Log(e.Name + ": format " + (e.Format ?? "unknown") + " -> " + f);
                e.Format = f;
            }
        }

        foreach (var a in e.Apps.Values) { a.Present = false; a.ActiveNow = false; }

        float loudest = 0;
        string loudestFrom = null;
        IAudioSessionEnumerator list;
        if (e.Sessions != null && e.Sessions.GetSessionEnumerator(out list) == 0 && list != null)
        {
            try
            {
                int n;
                list.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 s;
                    if (list.GetSession(i, out s) != 0 || s == null) continue;
                    try
                    {
                        uint pid;
                        s.GetProcessId(out pid);
                        if (pid == _ownPid) continue;   // our own silence is not what anyone is asking about

                        int state;
                        s.GetState(out state);
                        if (state == 2) continue;       // expired

                        string name = s.IsSystemSoundsSession() == 0 ? "Windows sounds" : ProcessName(pid);
                        App a;
                        if (!e.Apps.TryGetValue(name, out a)) e.Apps[name] = a = new App { Changed = now };
                        a.Present = true;
                        if (state == 1) a.ActiveNow = true;

                        if (!e.Capture && state == 1)
                        {
                            var meter = s as IAudioMeterInformation;
                            float peak;
                            if (meter != null && meter.GetPeakValue(out peak) == 0 && peak > loudest)
                            {
                                loudest = peak;
                                loudestFrom = name;
                            }
                        }
                    }
                    finally { Release(s); }
                }
            }
            finally { Release(list); }
        }

        foreach (var kv in new List<KeyValuePair<string, App>>(e.Apps))
        {
            var a = kv.Value;
            if (a.ActiveNow != a.Seen) { a.Seen = a.ActiveNow; a.Changed = now; }

            int settle = e.Capture ? MicSettleSeconds : SettleSeconds;
            if (a.Seen != a.Logged && (now - a.Changed).TotalSeconds >= settle)
            {
                a.Logged = a.Seen;
                if (e.Capture) MicChanged(e, kv.Key, a);
                else
                {
                    bool call = e.HandsFree && e.Bluetooth && e.Container != Guid.Empty;
                    Program.Log(kv.Key + (a.Logged ? " playing to " : " stopped playing to ") + e.Name
                                + " (" + (a.Logged ? "from " : "at ") + a.Changed.ToString("HH:mm:ss") + ")"
                                + (a.Logged && call ? " - that is the speaker's call channel, so it is in call mode" : ""));
                    if (call) CallChanged(e, kv.Key, a.Logged, a.Changed);
                }
            }

            if (!a.Present && !a.Seen && !a.Logged) e.Apps.Remove(kv.Key);
        }

        if (e.Capture) return;

        if (loudest > e.Loudest) e.Loudest = loudest;
        if (loudest >= Audible)
        {
            e.SoundSeconds += SampleMs / 1000;
            e.LastSound = now;
            e.LastSoundFrom = loudestFrom;
            e.Heard.Add(loudestFrom);
            if (CallOf(e.Container, now) == null) e.LastMusicSound = now;
        }
    }

    static void WatchVolume(Endpoint e, DateTime now)
    {
        float level;
        bool muted;
        if (!ReadVolume(e, out level, out muted)) return;
        if (Math.Abs(level - e.Level) < 0.005f && muted == e.Muted) return;

        if (Math.Abs(level - e.PendingLevel) >= 0.005f || muted != e.PendingMuted)
        {
            e.PendingLevel = level;
            e.PendingMuted = muted;
            e.PendingAt = now;
            return;
        }
        if ((now - e.PendingAt).TotalSeconds < VolumeSettleSeconds) return;

        Program.Log("volume " + e.Name + ": " + Percent(e.Level) + (e.Muted ? " muted" : "")
                    + " -> " + Percent(level) + (muted ? " muted" : ""));
        e.Level = level;
        e.Muted = muted;
    }

    static bool ReadVolume(Endpoint e, out float level, out bool muted)
    {
        level = -1;
        muted = false;
        if (e.Volume == null) return false;
        return e.Volume.GetMasterVolumeLevelScalar(out level) == 0 && e.Volume.GetMute(out muted) == 0;
    }

    static void MicChanged(Endpoint e, string app, App a)
    {
        bool speaker = e.Bluetooth && e.Container != Guid.Empty;
        Program.Log(app + (a.Logged ? " opened the microphone " : " closed the microphone ") + e.Name
                    + (a.Logged && speaker ? " - that speaker is in call mode for as long as it stays open" : ""));
        if (speaker) CallChanged(e, app, a.Logged, a.Changed);
    }

    /// <summary>
    /// An app started or stopped using a speaker's call channel: its microphone, or the
    /// hands-free output some speakers publish beside their stereo one. Either puts the
    /// speaker in call mode. An app using both is listed once per channel, so closing the
    /// microphone while it still plays to the hands-free output does not end the call.
    /// </summary>
    static void CallChanged(Endpoint e, string app, bool open, DateTime at)
    {
        Call call;
        bool active = _calls.TryGetValue(e.Container, out call) && call.Ended == DateTime.MaxValue;
        if (open)
        {
            if (!active) _calls[e.Container] = call = new Call { Since = at };
            call.By.Add(app);
            NoticeCall(e, app);
            return;
        }

        if (!active) return;
        call.By.Remove(app);
        if (call.By.Count > 0) return;
        call.Ended = DateTime.Now;
        Program.Log(e.Name + ": back out of call mode after " + Dur(call.Ended - call.Since));
    }

    /// <summary>Who is holding a call open, each app once.</summary>
    static string Who(Call c)
    {
        var seen = new List<string>();
        foreach (var a in c.By) if (!seen.Contains(a)) seen.Add(a);
        return seen.Count > 0 ? string.Join(", ", seen.ToArray()) : c.Was;
    }

    /// <summary>The call a speaker is in, or was in until a moment ago.</summary>
    static Call CallOf(Guid container, DateTime now)
    {
        Call c;
        if (container == Guid.Empty || !_calls.TryGetValue(container, out c)) return null;
        if (c.Ended == DateTime.MaxValue) return c;
        // Ended by the microphone vanishing a second ahead of the output: the output's
        // departure still happened in call mode.
        return c.Was.Length > 0 && (now - c.Ended).TotalSeconds < 10 ? c : null;
    }

    static void Gone(Endpoint e)
    {
        var now = DateTime.Now;
        if (!e.Bluetooth) return;

        // So the next time it connects with nowhere to move its calls role, that is said again.
        _nowhere.Remove(e.Id);

        if (e.Capture)
        {
            Call c;
            if (e.Container != Guid.Empty && _calls.TryGetValue(e.Container, out c) && c.Ended == DateTime.MaxValue)
            {
                c.Was = Who(c);
                c.Ended = now;
                Program.Log("disconnected: " + e.Name + ", while " + c.Was + " had it open");
            }
            else Program.Log("disconnected: " + e.Name);
            return;
        }

        var parts = new List<string>();
        parts.Add("disconnected: " + e.Name + (e.SinceIsConnect
            ? " after " + Dur(now - e.Since) + " connected"
            : ", connected before Speaker Keeper started, watched for " + Dur(now - e.Since)));

        parts.Add(e.LastSound == DateTime.MinValue
            ? "nothing but Speaker Keeper played to it" + (e.SinceIsConnect ? "" : " since then")
            : "last sound " + Dur(now - e.LastSound) + " before, from " + e.LastSoundFrom);

        var call = CallOf(e.Container, now);
        if (call != null)
        {
            string by = Who(call);
            parts.Add("in call mode since " + call.Since.ToString("HH:mm:ss") + " (" + by + " using its call channel)");
            if (e.LastMusicSound != e.LastSound)
                parts.Add(e.LastMusicSound == DateTime.MinValue
                    ? "no sound in music mode at all"
                    : "last sound in music mode " + Dur(now - e.LastMusicSound) + " before");
        }

        // Measured from the last sound the speaker heard as music, or from connecting.
        // Only meaningful when the connection time is real.
        int timer = 0;
        var from = e.LastMusicSound > e.Since ? e.LastMusicSound : e.Since;
        if (e.SinceIsConnect || e.LastMusicSound > e.Since)
        {
            var quiet = now - from;
            foreach (int m in new[] { 5, 10, 15, 20, 30, 60 })
                if (Math.Abs(quiet.TotalSeconds - m * 60) <= TimerSlackSeconds) { timer = m; break; }
            if (timer > 0)
                parts.Add("that is " + Dur(quiet) + " after its last music-mode sound, which is the"
                          + " speaker's own " + timer + "-minute auto-off, not a lost connection");
        }

        Program.Log(string.Join("; ", parts.ToArray()));

        lock (_lock)
        {
            if (e.Container != Guid.Empty)
                _departures[e.Container] = new Departure
                {
                    At = now, Name = e.Name, InCall = call != null,
                    CallBy = call == null ? null : Who(call),
                    TimerMinutes = timer
                };
        }
    }

    static void Summarise()
    {
        var now = DateTime.Now;
        foreach (var e in _eps.Values)
        {
            if (e.Capture || !e.Bluetooth) continue;

            string what = e.SoundSeconds == 0
                ? "silent apart from Speaker Keeper"
                : "sound for " + e.SoundSeconds + "s, loudest " + Db(e.Loudest)
                  + ", from " + string.Join(", ", new List<string>(e.Heard).ToArray());

            var call = CallOf(e.Container, now);
            string mode = call != null
                ? "call mode, " + Who(call) + " using its call channel"
                : "music mode";

            Program.Log("activity " + e.Name + ", last " + SummaryMinutes + " min: " + what + "; "
                        + mode + (e.Format != null ? ", " + e.Format : "")
                        + (e.Level >= 0 ? ", volume " + Percent(e.Level) + (e.Muted ? " muted" : "") : ""));

            e.SoundSeconds = 0;
            e.Loudest = 0;
            e.Heard.Clear();
        }

        // Process ids get reused. Forgetting the names now and then keeps a new process
        // from being logged under a dead one's name.
        _names.Clear();
    }

    const string SameAsDefault = "(same as the default output)";
    const int RoleSettleSeconds = 3;
    static readonly Dictionary<string, KeyValuePair<string, DateTime>> _pendingRoles =
        new Dictionary<string, KeyValuePair<string, DateTime>>();

    static void Role(IMMDeviceEnumerator en, int flow, int role, string label, bool first)
    {
        string id;
        string name = DefaultName(en, flow, role, out id);

        // The output for calls is the default output on nearly every PC, and logging it
        // then only doubles every "default output changed" line. It earns a line only
        // when the two differ.
        if (flow == 0 && role == 2)
        {
            string ignored;
            if (name == DefaultName(en, 0, 0, out ignored)) name = SameAsDefault;
        }

        string was;
        bool known = _roles.TryGetValue(label, out was);
        if (known && was == name) { _pendingRoles.Remove(label); return; }

        // A speaker disconnecting moves every role, not all within the same second, and a
        // monitor waking and sleeping again flaps them. Waiting for the value to hold
        // keeps one change from being logged as two.
        if (!first)
        {
            KeyValuePair<string, DateTime> p;
            if (!_pendingRoles.TryGetValue(label, out p) || p.Key != name)
            {
                _pendingRoles[label] = new KeyValuePair<string, DateTime>(name, DateTime.Now);
                return;
            }
            if ((DateTime.Now - p.Value).TotalSeconds < RoleSettleSeconds) return;
            _pendingRoles.Remove(label);
        }

        _roles[label] = name;
        if (name == SameAsDefault)
        {
            if (known) Program.Log(label + " follows the default output again");
            return;
        }

        string line = label + (first ? " is " : " -> ") + name;

        // The one to warn about. Discord, Teams and game voice chat open the microphone
        // for calls, not the ordinary default, and Settings does not show it: it is the
        // "Default Communication Device" in the old Sound control panel. So a user can
        // look, see their webcam as the default microphone, and be right, while the
        // speaker's microphone is the one their games are opening.
        if (flow == 1 && role == 2 && id != null && DeviceProps.IsBluetoothEndpoint(id))
            line += " - this is Windows' Default Communication Device, separate from the default"
                  + " microphone; voice chat and calls open it, which puts that speaker in call mode";

        Program.Log(line);
    }

    static string DefaultName(IMMDeviceEnumerator en, int flow, int role, out string id)
    {
        id = null;
        IMMDevice d;
        if (en.GetDefaultAudioEndpoint(flow, role, out d) != 0 || d == null) return "none";
        try
        {
            if (d.GetId(out id) != 0 || id == null) return "none";
            return DeviceProps.EndpointName(id) ?? id;
        }
        finally { Release(d); }
    }

    static string FormatOf(IMMDevice d)
    {
        object o;
        var iid = typeof(IAudioClient).GUID;
        if (d == null || d.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) return null;
        try
        {
            IntPtr f;
            if (((IAudioClient)o).GetMixFormat(out f) != 0 || f == IntPtr.Zero) return null;
            try
            {
                int ch = (ushort)Marshal.ReadInt16(f, 2);
                int rate = Marshal.ReadInt32(f, 4);
                return rate + " Hz " + (ch == 1 ? "mono" : ch == 2 ? "stereo" : ch + " channels");
            }
            finally { Marshal.FreeCoTaskMem(f); }
        }
        finally { Release(o); }
    }

    static string ProcessName(uint pid)
    {
        string n;
        if (_names.TryGetValue(pid, out n)) return n;
        try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) n = p.ProcessName + ".exe"; }
        catch { n = "process " + pid; }
        _names[pid] = n;
        return n;
    }

    static void Close(Endpoint e)
    {
        Release(e.Sessions);
        Release(e.Volume);
        Release(e.Device);
        e.Sessions = null;
        e.Volume = null;
        e.Device = null;
    }

    // One reference at a time, for the reason Silence.Release gives: the enumerator is
    // shared across the process, and a final release kills it for every other holder.
    static void Release(object o)
    {
        if (o == null) return;
        try { Marshal.ReleaseComObject(o); } catch { }
    }

    /// <summary>
    /// The method in this class that a failure came out of. A COM error's message says
    /// what went wrong but never where, and once seen after a resume from sleep that was
    /// the one thing needed to find it.
    /// </summary>
    static string Where(Exception ex)
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(ex);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var m = trace.GetFrame(i).GetMethod();
                if (m != null && m.DeclaringType == typeof(Activity)) return " (in " + m.Name + ")";
            }
        }
        catch { }
        return "";
    }

    static string Percent(float level) { return Math.Round(level * 100) + "%"; }

    static string Db(float peak) { return peak <= 0 ? "silent" : Math.Round(20 * Math.Log10(peak)) + " dB"; }

    static string Dur(TimeSpan t)
    {
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
        return (int)t.TotalMinutes + "m" + t.Seconds.ToString("00") + "s";
    }
}

/// <summary>
/// Logs the display turning off, dimming and coming back on.
///
/// The display on the machine this was found on turns off after 15 minutes idle, which
/// is also how long its speakers lasted, and whether one causes the other cannot be told
/// without both in the same log. A sleeping monitor also takes its HDMI audio output
/// away, which reshuffles the default output, so it earns its place regardless.
///
/// GUID_CONSOLE_DISPLAY_STATE, not the PowerModeChanged event the app already logs:
/// that one is the whole machine sleeping, and says nothing about the screen. Windows
/// sends the current state the moment this registers, so the first line after startup
/// says what the display was doing then.
/// </summary>
class DisplayWatch : NativeWindow
{
    static readonly Guid ConsoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
    const int WM_POWERBROADCAST = 0x0218;
    const int PBT_POWERSETTINGCHANGE = 0x8013;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    static DisplayWatch _instance;
    int _last = -1;

    /// <summary>Must run on the UI thread: the notifications arrive through its message loop.</summary>
    public static void Start()
    {
        if (_instance != null) return;
        try
        {
            var w = new DisplayWatch();
            // A hidden top-level window. Not a message-only one: those are left out of
            // power broadcasts, and this is not the place to find out which ones.
            w.CreateHandle(new CreateParams());
            var g = ConsoleDisplayState;
            if (RegisterPowerSettingNotification(w.Handle, ref g, 0) == IntPtr.Zero)
                Program.Log("display state unavailable: error " + Marshal.GetLastWin32Error());
            _instance = w;
        }
        catch (Exception ex) { Program.Log("display state unavailable: " + ex.Message); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: the setting's GUID, a DWORD length, then the value.
            var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
            if (setting == ConsoleDisplayState)
            {
                int state = Marshal.ReadInt32(m.LParam, 20);
                if (state != _last)
                {
                    bool first = _last == -1;
                    _last = state;
                    Program.Log("display " + (state == 0 ? "off" : state == 1 ? "on" : state == 2 ? "dimmed" : "state " + state)
                                + (first ? " (at startup)" : ""));
                }
            }
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Holds one silent stream per speaker the user wants kept awake.
///
/// One stream per speaker rather than one for "the current output", because a speaker
/// that is connected but not selected still goes to sleep, and then switching to it has
/// exactly the delay this app exists to remove. Every enabled, connected speaker is held,
/// so switching between them is instant in both directions.
/// </summary>
static class Keeper
{
    static readonly Dictionary<string, Silence> _held =
        new Dictionary<string, Silence>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Speakers currently being held awake, by endpoint id.</summary>
    public static ICollection<string> Endpoints { get { return _held.Keys; } }

    /// <summary>
    /// Speakers actually being held right now. Not the number of streams on the books:
    /// one that failed and is waiting out its backoff is counted by nobody, because
    /// saying "keeping 1 awake" while nothing is being kept awake is the one thing the
    /// status line must never do.
    /// </summary>
    public static int Count
    {
        get
        {
            int n = 0;
            foreach (var s in _held.Values) if (s.Active) n++;
            return n;
        }
    }

    public static bool Holding(string endpointId)
    {
        Silence s;
        return endpointId != null && _held.TryGetValue(endpointId, out s) && s.Active;
    }

    /// <summary>
    /// Brings the set of live streams in line with the set of speakers that should be
    /// held. Called on every tick, so it is also what restarts a stream that died.
    /// </summary>
    public static void Apply(List<DeviceProps.AudioDevice> wanted, DeviceProps.Snapshot snap,
                             Dictionary<string, string> reasons)
    {
        // One physical speaker can publish a live endpoint on each Bluetooth radio the PC
        // has, so the same speaker can appear twice. Holding both would open two streams
        // to one speaker. The endpoint Windows is actually playing through wins; failing
        // that, the first one seen.
        var byContainer = new Dictionary<Guid, DeviceProps.AudioDevice>();
        foreach (var d in wanted)
        {
            DeviceProps.AudioDevice kept;
            if (!byContainer.TryGetValue(d.Container, out kept) || d.IsDefault)
                byContainer[d.Container] = d;
        }

        var keep = new Dictionary<string, DeviceProps.AudioDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in byContainer.Values) keep[d.EndpointId] = d;

        // Endpoints that still exist at all, whether or not we want to hold them. An
        // endpoint we were holding that is no longer in this set did not stop being
        // wanted: it went away underneath us.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in snap.Outputs) present.Add(d.EndpointId);

        foreach (var id in new List<string>(_held.Keys))
        {
            if (keep.ContainsKey(id)) continue;
            var s = _held[id];
            _held.Remove(id);

            string why;
            reasons.TryGetValue(id, out why);

            if (s.Disconnected)
            {
                // The stream already noticed and said so. Nothing to add.
            }
            else if (!present.Contains(id) || why == Program.Unidentified)
            {
                // Unidentified is the same disconnection seen from the other side. A
                // speaker powering off takes its Bluetooth device nodes away a moment
                // before its audio output, so for one tick the output is still there
                // with nothing behind it. That read as "letting it sleep", a decision,
                // at exactly the moment the speaker switched itself off.
                // A disconnection this tick saw before the stream thread did. The stream
                // polls every 500ms and the policy runs every 5s, so whenever a speaker
                // vanishes in that last half second it is Stop() that ends the stream,
                // cleanly, and nothing ever records a failure. Left unhandled this reads
                // in the log as a decision the app made, and never reaches the count that
                // decides whether the user is told their speaker keeps dropping.
                Program.Log(s.Label + ": the speaker disconnected, held for " + s.HeldFor);
                Health.RecordDrop(s.Container, s.Label);
            }
            else
            {
                // Present, wanted by nobody. With no reason it lost to another output of
                // the same speaker, which Apply's container check above allows only one of.
                Program.Log("letting " + s.Label + " sleep - "
                            + (why ?? "another output of the same speaker is held instead")
                            + ", held for " + s.HeldFor);
            }

            s.Stop();
        }

        foreach (var kv in keep)
        {
            string label = snap.NameOf(kv.Value);

            Silence s;
            if (!_held.TryGetValue(kv.Key, out s))
            {
                _held[kv.Key] = s = new Silence(kv.Key, kv.Value.Container, label);
                s.Start();
                continue;
            }

            s.Label = label;
            if (s.Active || !s.ReadyToRetry) continue;

            // Read this before Start(), which clears it. A speaker that dropped its
            // Bluetooth link and came straight back lands here, and the reason it went
            // is the one line worth having.
            string died = s.LastError;
            if (died != null) Program.Log("retrying " + s.Label + " after " + died);
            s.Start();
        }
    }

    public static void StopAll()
    {
        foreach (var s in _held.Values) s.Stop();
        _held.Clear();
    }
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

    // Rotate at 4 MB and keep three previous files: SpeakerKeeper.log.1 is the newest.
    //
    // It used to be 1 MB and one previous file, which was weeks when the log said little.
    // Activity now records what every speaker was hearing, and a speaker that "keeps
    // switching off" is a pattern across days, so the history has to reach back that far.
    // 16 MB at most, in a folder nobody backs up.
    const long MaxLogBytes = 4 * 1024 * 1024;
    const int KeepLogs = 3;

    // The UI thread, every speaker's stream thread and Activity all write here. Two
    // appends racing each other throw on the file share, and the loser's line is lost.
    static readonly object _logLock = new object();

    internal static void Log(string m)
    {
        lock (_logLock)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);

                // Without this the log grows forever on a machine that's always on.
                try
                {
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > MaxLogBytes) RotateLogs();
                }
                catch { }

                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine);
            }
            catch { }
        }
    }

    static void RotateLogs()
    {
        string oldest = LogFile + "." + KeepLogs;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = KeepLogs - 1; i >= 1; i--)
        {
            string from = LogFile + "." + i;
            if (File.Exists(from)) File.Move(from, LogFile + "." + (i + 1));
        }
        File.Move(LogFile, LogFile + ".1");

        // Versions before 1.6 kept a single SpeakerKeeper.log.old. It is older than
        // anything numbered, so it takes the first free slot after them rather than
        // being thrown away; with no slot left it has aged out anyway.
        string legacy = LogFile + ".old";
        if (!File.Exists(legacy)) return;
        for (int i = 2; i <= KeepLogs; i++)
            if (!File.Exists(LogFile + "." + i)) { File.Move(legacy, LogFile + "." + i); return; }
        File.Delete(legacy);
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
    /// Decides whether one output should be held awake.
    ///
    /// Four gates: it must be Bluetooth at all (keeping a monitor or a wired speaker
    /// awake achieves nothing); it must be the endpoint that plays music rather than the
    /// speaker's call channel; it must be a speaker rather than earbuds, which are
    /// supposed to sleep when they are put away; and it must not have been switched off
    /// for that particular device.
    /// </summary>
    // A Bluetooth output whose device is not in the snapshot. Keeper treats it as a
    // disconnection in progress, and checks for it through this rather than retyping it.
    internal static readonly string Unidentified = "output device not identifiable";

    static bool ShouldKeepAwake(DeviceProps.AudioDevice d, DeviceProps.Snapshot snap, out string reason)
    {
        if (!d.IsBluetooth) { reason = "not a Bluetooth output"; return false; }

        var bt = snap.Of(d.Container);
        if (bt == null) { reason = Unidentified; return false; }

        if (!snap.IsPlayable(d)) { reason = "this is the call channel, not the speaker"; return false; }

        DevicePolicy.Remember(d.Container, snap.NameOf(d), bt.ClassOfDevice);

        if (!DevicePolicy.IsEnabled(d.Container, bt.IsSpeaker))
        {
            reason = bt.IsSpeaker
                ? "turned off for this speaker"
                : "earbuds are left to sleep";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Brings the held speakers in line with the policy.
    ///
    /// Every connected speaker the user has left switched on is held, not just whichever
    /// one Windows is currently playing through: the other one going to sleep is exactly
    /// the delay this app exists to remove, and it shows up the moment you switch to it.
    /// </summary>
    static void ApplyPolicy()
    {
        var snap = DeviceProps.Read();
        var wanted = new List<DeviceProps.AudioDevice>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string defaultReason = "no default output";

        foreach (var d in snap.Outputs)
        {
            d.IsDefault = d.EndpointId == _lastDevice;

            string reason;
            if (ShouldKeepAwake(d, snap, out reason)) wanted.Add(d);
            else reasons[d.EndpointId] = reason;
            if (d.IsDefault) defaultReason = reason;
        }

        Keeper.Apply(wanted, snap, reasons);

        // What the tray says about the output you are actually listening through. The
        // other speakers being held are in the log and in Settings; the panel is about
        // the one in front of you. Only worth a log line when nothing at all is held:
        // "idle" while two speakers are being kept awake would simply be untrue.
        string idle = Keeper.Holding(_lastDevice) ? null : defaultReason;
        if (idle != null && idle != _idleReason && Keeper.Count == 0) Log("idle - " + idle);
        _idleReason = idle;

        ReportHealth();
    }

    /// <summary>
    /// Says once, plainly, when the machine rather than the app is the problem.
    ///
    /// Once per run: a speaker on a failing adapter drops all day, and a toast every time
    /// would be worse than the fault. Settings keeps the same text for as long as it is
    /// true, which is the copy that can be read at leisure.
    /// </summary>
    static void ReportHealth()
    {
        // The first policy run happens before the tray icon exists, and a balloon with
        // nothing to anchor to is dropped by the shell without a word.
        if (Health.Announced || _tray == null) return;

        string advice = Health.Advice();
        if (advice == null) return;

        Health.Announced = true;
        Log("diagnosis: " + advice);

        try
        {
            _balloonClick = () => ShowSettings();
            _tray.ShowBalloonTip(15000, "Your speaker keeps switching off",
                advice.Length > 250 ? advice.Substring(0, 247) + "..." : advice,
                ToolTipIcon.Warning);
        }
        catch (Exception ex) { Log("diagnosis notice failed: " + ex.Message); }
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
            ApplyPolicy();
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
            ApplyPolicy();
            RefreshUi();
        };
        _settings.Show();
    }

    static void QuitApp()
    {
        Log("quit requested from tray");
        Keeper.StopAll();
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
            var snap = DeviceProps.Read();

            DeviceProps.AudioDevice self = null;
            foreach (var d in snap.Outputs)
                if (d.EndpointId == id) { self = d; break; }

            s.Name = self != null ? snap.NameOf(self) : "No output";

            if (self != null && self.IsBluetooth)
            {
                s.Bluetooth = true;
                s.Container = self.Container;
                var bt = snap.Of(self.Container);
                s.IsSpeaker = bt == null || bt.IsSpeaker;
            }

            s.Battery = string.IsNullOrEmpty(id) ? -1 : DeviceProps.BatteryPercent(id);
            // "on charger" only when a rise was actually observed; a falling battery is
            // labelled as such rather than claimed to be unplugged.
            s.Charge = _charge == Charge.Charging ? "on charger"
                     : _charge == Charge.Draining ? "draining" : "";
            s.KeepingAwake = Keeper.Holding(id);
            // Speakers held awake that are not the one you are listening through. The
            // panel names the current output, so this is how the others get a mention.
            s.AlsoHeld = Keeper.Count - (s.KeepingAwake ? 1 : 0);
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

            // Invoked elevated by Settings > Speakers > Turn off speaker microphones.
            // "off" means the microphones go off.
            if (string.Equals(a, "--speaker-microphones", StringComparison.OrdinalIgnoreCase))
            {
                bool off = i + 1 < args.Length &&
                           string.Equals(args[i + 1], "off", StringComparison.OrdinalIgnoreCase);
                Environment.Exit(Microphones.Apply(off));
                return;
            }

            // Invoked as SYSTEM by the Speaker Keeper Microphones task.
            if (string.Equals(a, "--enforce-speaker-microphones", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(Microphones.EnforceFromTask());
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
        if (Microphones.Enabled)
            Log("speaker microphones are set to off (Settings > Speakers); their Hands-Free"
                + " profile is switched off whenever Windows sets one up");
        Activity.Start();

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
                    ApplyPolicy();
                    RefreshUi();
                };
                resume.Start();
            }
        };

        Microsoft.Win32.SystemEvents.SessionEnding += (s, e) =>
            Log("session ending: " + e.Reason);

        // Locking matters for the same reason the display does: a locked PC turns its
        // display off after a minute, not after the usual timeout.
        Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
            Log(e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock ? "PC locked"
              : e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ? "PC unlocked"
              : "session: " + e.Reason);
        DisplayWatch.Start();
        _lastDevice = DefaultDeviceId();
        ApplyPolicy();
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
                ApplyPolicy();
                RefreshUi();   // keep the tooltip pointing at the new device
            }
            else
            {
                ApplyPolicy();
            }

            // Activity notices call mode on its own thread; the notification icon belongs
            // to this one, so the message waits here for the next tick.
            var notice = Activity.TakeNotice();
            if (notice != null && _tray != null)
            {
                Log("notice: " + notice.Title);
                _balloonClick = () => { ShowSettings(); if (_settings != null) _settings.ShowSpeakers(); };
                _tray.ShowBalloonTip(15000, notice.Title, notice.Text, ToolTipIcon.Warning);
            }

            // Battery moves slowly and each read walks the device tree, so poll it
            // every 2 minutes rather than on every 5-second tick.
            if (_ticks % 24 == 0)
                CheckBattery();

            if (_ticks % 120 == 0)
                Log("heartbeat " + (Keeper.Count > 0
                        ? "keeping " + Keeper.Count + " awake"
                        : "idle - " + (_idleReason ?? "stopped")));
        }
        catch (Exception ex) { Log("tick error: " + ex.Message); }
    }
}
