// Setup.cs - Install.exe, the double-click installer.
//
// Built from this source plus SpeakerKeeper.cs via /main:SetupProgram, the same
// "one source, several entry points" arrangement Uninstall.exe uses. That matters
// here for a specific reason: the installer and the uninstaller must agree on
// exactly which registry keys and files make up an install, and they can only
// disagree if they are written twice. They share Installer's key constants instead.
//
// Everything the app needs at runtime is carried inside this exe as an embedded
// resource, so a user downloads one file and double-clicks it. The manifest
// requests administrator, so Windows raises the normal UAC prompt before any of
// this runs - the same prompt any other installer shows.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>Entry point for Install.exe.</summary>
static class SetupProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // /S is the long-standing convention for a silent install (NSIS, Inno and
        // friends all take it); --quiet mirrors what Uninstall.exe already accepts.
        bool silent = false;
        bool autoUpdate = true;     // same default the wizard offers
        string target = SetupActions.DefaultTarget;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)) silent = true;
            if (string.Equals(a, "/D", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                target = args[i + 1];
            // For managed deployments that patch on their own schedule and do not want a
            // SYSTEM task reaching the internet nightly.
            if (string.Equals(a, "/NOAUTOUPDATE", StringComparison.OrdinalIgnoreCase))
                autoUpdate = false;
        }

        if (silent)
        {
            try
            {
                string warning = SetupActions.Install(target, true, true, autoUpdate, delegate { });
                if (warning != null)
                {
                    // Installed, but not entirely as asked. Say so on stderr and use a
                    // distinct exit code so a deployment script can tell the difference.
                    Console.Error.WriteLine(warning);
                    Environment.Exit(2);
                }
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("install failed: " + ex.Message);
                Environment.Exit(1);
            }
            return;
        }

        Application.Run(new SetupWizard());
    }
}

/// <summary>
/// The payload. Each file the app needs at runtime is compiled into this exe with
/// /resource: and written out at install time.
///
/// silent.wav is the exception: it is 3.4 MB of pure digital silence, so embedding it
/// would bloat the download by ~30x to carry nothing but zeros. It is generated instead,
/// byte-for-byte identical to the file in the repo.
/// </summary>
static class Payload
{
    // The runtime payload, matching what install.ps1 used to copy. Uninstall removes
    // the whole folder, so this list only has to be complete, not tracked item by item.
    public static readonly string[] Files =
    {
        "SpeakerKeeper.exe",
        "Uninstall.exe",
        "SpeakerKeeper.ico",
        "update.ps1",
        "README.md",
        "LICENSE",
    };

    public static void Write(string dir, string name)
    {
        using (var src = Read(name))
        using (var dst = File.Create(Path.Combine(dir, name)))
            src.CopyTo(dst);
    }

    public static Stream Read(string name)
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) throw new FileNotFoundException("missing embedded resource: " + name);
        return s;
    }

    /// <summary>
    /// Writes silent.wav: 20 seconds of 44.1 kHz 16-bit stereo where every sample is zero.
    ///
    /// The keep-alive comes from holding a media *session* open, not from any sound, so
    /// the audio only has to exist - it never has to be audible.
    /// </summary>
    public static void WriteSilentWav(string dir)
    {
        const int Rate = 44100, Channels = 2, Bits = 16, Seconds = 20;
        int align = Channels * Bits / 8;
        int bytes = Rate * align * Seconds;

        using (var fs = File.Create(Path.Combine(dir, "silent.wav")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(new char[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + bytes);
            w.Write(new char[] { 'W', 'A', 'V', 'E' });
            w.Write(new char[] { 'f', 'm', 't', ' ' });
            w.Write(16);                          // PCM fmt chunk length
            w.Write((short)1);                    // PCM
            w.Write((short)Channels);
            w.Write(Rate);
            w.Write(Rate * align);                // byte rate
            w.Write((short)align);
            w.Write((short)Bits);
            w.Write(new char[] { 'd', 'a', 't', 'a' });
            w.Write(bytes);

            // Written in chunks rather than one 3.4 MB allocation.
            var zeros = new byte[64 * 1024];
            for (int left = bytes; left > 0; )
            {
                int n = Math.Min(zeros.Length, left);
                w.Write(zeros, 0, n);
                left -= n;
            }
        }
    }
}

/// <summary>
/// The install itself, with no UI of its own so the wizard and /S share one code path.
/// </summary>
static class SetupActions
{
    public const string UpdateUrl =
        "https://raw.githubusercontent.com/kevinabouhanna/speaker-keeper/main/manifest.json";

    public static string DefaultTarget
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Speaker Keeper");
        }
    }

    public static string ShortcutPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs" + (char)92 + "Speaker Keeper.lnk");
        }
    }

    /// <summary>Roughly what the install will occupy, for the location page.</summary>
    public static long EstimatedBytes
    {
        get
        {
            long total = 44 + (long)44100 * 4 * 20;     // silent.wav
            foreach (var f in Payload.Files)
                using (var s = Payload.Read(f)) total += s.Length;
            return total;
        }
    }

    /// <summary>
    /// Returns null on a clean install, or a message describing something that did not
    /// work but did not stop the install - see the auto-update branch below.
    /// </summary>
    public static string Install(string target, bool autostart, bool shortcut, bool autoUpdate,
                                 Action<string> report)
    {
        string warning = null;
        report("Closing any running copy...");
        StopRunning();

        report("Copying files...");
        Directory.CreateDirectory(target);
        foreach (var f in Payload.Files) Payload.Write(target, f);
        Payload.WriteSilentWav(target);

        string exe = Path.Combine(target, "SpeakerKeeper.exe");
        string uninst = Path.Combine(target, "Uninstall.exe");

        report("Registering with Windows...");
        RegisterUninstall(target, exe, uninst);

        // The update feed. Written whether or not auto-update is on, so turning it on
        // later from Settings has somewhere to point.
        using (var k = Registry.LocalMachine.CreateSubKey("Software" + (char)92 + "SpeakerKeeper"))
            if (k != null) k.SetValue("UpdateUrl", UpdateUrl);

        if (autoUpdate)
        {
            report("Setting up automatic updates...");
            // Setup is already elevated, so the SYSTEM task can be created without a
            // second prompt. Told the target explicitly: this process runs from wherever
            // Install.exe was saved, not from the install folder.
            int rc = Updater.Apply(true, target);
            if (rc != 0)
            {
                // Deliberately NOT fatal: the app is installed and works, it just won't
                // update itself. Failing the whole install here would be a lie. But it
                // must be said out loud - a silent no-op is how someone ends up believing
                // updates are on for months when they never were.
                warning = "Speaker Keeper is installed, but automatic updates could not be "
                        + "switched on (error " + rc + "). You can turn them on from "
                        + "Settings once the app is running.";
            }
        }

        if (shortcut)
        {
            report("Creating Start Menu shortcut...");
            try { CreateShortcut(ShortcutPath, exe, target); } catch { }
        }

        if (autostart)
        {
            report("Enabling start with Windows...");
            // Per-user on purpose: each account decides for itself, and the tray menu can
            // then toggle it without needing elevation.
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(
                    "Software" + (char)92 + "Microsoft" + (char)92 + "Windows" + (char)92 +
                    "CurrentVersion" + (char)92 + "Run"))
                    if (k != null) k.SetValue("Speaker Keeper", (char)34 + exe + (char)34);
            }
            catch { }
        }

        report("Cleaning up...");
        CleanUpOldPerUserInstall(target);

        report("Done.");
        return warning;
    }

    static void StopRunning()
    {
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
        Thread.Sleep(300);      // let the file handles go
    }

    static void RegisterUninstall(string target, string exe, string uninst)
    {
        long size = 0;
        try { foreach (var f in Directory.GetFiles(target)) size += new FileInfo(f).Length; }
        catch { }

        using (var k = Registry.LocalMachine.CreateSubKey(Installer.UninstallKey))
        {
            if (k == null) return;
            k.SetValue("DisplayName", "Speaker Keeper");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Kevin Abou Hanna");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", target);
            k.SetValue("UninstallString", (char)34 + uninst + (char)34);
            k.SetValue("QuietUninstallString", (char)34 + uninst + (char)34 + " --quiet");
            k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>Shared with the app so the installer and the tray never disagree.</summary>
    public static string Version { get { return Project.ShortVersion; } }

    /// <summary>
    /// Creates the Start Menu shortcut through WScript.Shell by late binding, which
    /// avoids taking a COM reference on IWshRuntimeLibrary just to write one .lnk -
    /// the build stays plain csc with no interop assembly to generate or ship.
    /// </summary>
    static void CreateShortcut(string lnkPath, string exe, string workingDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));

        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        object shell = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                    new object[] { lnkPath });
        var lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exe });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workingDir });
        lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { exe + ",0" });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk,
                        new object[] { "Keeps a Bluetooth speaker awake" });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    /// <summary>Earlier builds installed per-user into %LocalAppData%\SpeakerKeeper.</summary>
    static void CleanUpOldPerUserInstall(string target)
    {
        try
        {
            string old = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeakerKeeper");
            if (Directory.Exists(old) &&
                !string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(old, true);
        }
        catch { }

        try { Registry.CurrentUser.DeleteSubKeyTree(Installer.UninstallKey, false); }
        catch { }
    }

    /// <summary>
    /// Starts the app as the logged-on user rather than as administrator.
    ///
    /// This process is elevated, and anything it launches inherits that. A tray app
    /// running elevated is subtly broken: it writes its settings to the wrong hive and
    /// drag-and-drop from Explorer stops working. Going through explorer.exe hands the
    /// launch to the desktop shell, which runs unelevated, so the app lands in the
    /// right session with the right token.
    /// </summary>
    public static void LaunchAsUser(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", (char)34 + exe + (char)34);
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}

/// <summary>
/// A conventional five-page setup wizard: Welcome, Licence, Location, Progress, Finish.
///
/// Pages are panels stacked in the same content area with one visible at a time, rather
/// than separate forms, so the window never flickers or moves between steps.
/// </summary>
class SetupWizard : Form
{
    const int PageWelcome = 0, PageLicense = 1, PageLocation = 2, PageProgress = 3, PageFinish = 4;

    readonly Panel _content;
    readonly Panel[] _pages = new Panel[5];
    readonly Button _back, _next, _cancel;
    readonly Label _headTitle, _headSub;
    readonly Panel _header;
    readonly Image _logo;

    CheckBox _accept, _autostart, _shortcut, _autoUpdate, _launch;
    TextBox _path;
    Label _spaceNote;
    ProgressBar _bar;
    Label _step;
    Label _finishText;

    int _page;
    bool _installed;
    string _error;

    public SetupWizard()
    {
        using (var s = Payload.Read("Logo.png")) _logo = Image.FromStream(s);
        // Forms need the whole multi-resolution icon, not one frame, or Windows
        // stretches a small frame up for the taskbar and it looks soft.
        using (var s = Payload.Read("SpeakerKeeper.ico")) Icon = new Icon(s);

        Text = "Speaker Keeper Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Tall enough that the welcome text, the version and the "click Next" line all
        // fit without the content panel clipping the last two.
        ClientSize = new Size(520, 440);
        Font = SystemFonts.MessageBoxFont;
        BackColor = SystemColors.Control;

        // --- bottom button bar (added first; docking fills in reverse z-order) ---
        var bar = new Panel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 56;
        bar.BackColor = SystemColors.Control;

        _cancel = MakeButton("Cancel");
        _cancel.Click += (s, e) => OnCancel();
        _next = MakeButton("Next >");
        _next.Click += (s, e) => OnNext();
        _back = MakeButton("< Back");
        _back.Click += (s, e) => Go(_page - 1);

        var flow = new FlowLayoutPanel();
        flow.Dock = DockStyle.Right;
        flow.FlowDirection = FlowDirection.RightToLeft;
        flow.WrapContents = false;
        flow.AutoSize = true;
        flow.Padding = new Padding(0, 12, 14, 0);
        flow.Controls.Add(_cancel);     // right-to-left: rightmost is added first
        flow.Controls.Add(_next);
        flow.Controls.Add(_back);
        bar.Controls.Add(flow);

        var rule = new Panel();
        rule.Dock = DockStyle.Top;
        rule.Height = 1;
        rule.BackColor = SystemColors.ControlDark;
        bar.Controls.Add(rule);

        // --- header ---
        _header = new Panel();
        _header.Dock = DockStyle.Top;
        _header.Height = 68;
        _header.BackColor = Color.White;
        _header.Paint += (s, e) =>
        {
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_logo, new Rectangle(14, 10, 48, 48));
            using (var p = new Pen(SystemColors.ControlDark))
                e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };

        _headTitle = new Label();
        _headTitle.Location = new Point(74, 14);
        _headTitle.AutoSize = true;
        _headTitle.Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
        _headTitle.BackColor = Color.Transparent;
        _header.Controls.Add(_headTitle);

        _headSub = new Label();
        _headSub.Location = new Point(76, 38);
        _headSub.AutoSize = true;
        _headSub.ForeColor = SystemColors.GrayText;
        _headSub.BackColor = Color.Transparent;
        _header.Controls.Add(_headSub);

        // --- content ---
        _content = new Panel();
        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(20, 16, 20, 8);

        _pages[PageWelcome] = BuildWelcome();
        _pages[PageLicense] = BuildLicense();
        _pages[PageLocation] = BuildLocation();
        _pages[PageProgress] = BuildProgress();
        _pages[PageFinish] = BuildFinish();
        foreach (var p in _pages) { p.Dock = DockStyle.Fill; p.Visible = false; _content.Controls.Add(p); }

        Controls.Add(_content);
        Controls.Add(_header);
        Controls.Add(bar);

        Go(PageWelcome);
    }

    static Button MakeButton(string text)
    {
        var b = new Button();
        b.Text = text;
        b.Size = new Size(92, 30);
        b.Margin = new Padding(6, 0, 0, 0);
        return b;
    }

    static Label Para(string text, int y, int width)
    {
        var l = new Label();
        l.Text = text;
        l.Location = new Point(0, y);
        l.AutoSize = true;
        l.MaximumSize = new Size(width, 0);
        return l;
    }

    Panel BuildWelcome()
    {
        var p = new Panel();
        const int W = 460;

        var h = Para("Welcome to Speaker Keeper", 4, W);
        h.Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Regular);
        p.Controls.Add(h);

        p.Controls.Add(Para(
            "Bluetooth speakers switch themselves off after a few minutes of quiet. That is "
            + "why the first second of your music keeps getting cut off, and why you sometimes "
            + "have to reconnect the speaker by hand.\r\n\r\n"
            + "Speaker Keeper quietly plays silence in the background, so your speaker never "
            + "nods off. You hear nothing - it simply stays awake, like a wired speaker.\r\n\r\n"
            + "It sits in your system tray, shows your speaker's battery, and leaves your "
            + "media keys alone.", 42, W));

        var v = Para("Version " + SetupActions.Version, 224, W);
        v.ForeColor = SystemColors.GrayText;
        p.Controls.Add(v);

        p.Controls.Add(Para("Click Next to continue.", 252, W));
        return p;
    }

    Panel BuildLicense()
    {
        var p = new Panel();

        var box = new TextBox();
        box.Multiline = true;
        box.ReadOnly = true;
        box.ScrollBars = ScrollBars.Vertical;
        box.BackColor = Color.White;       // ReadOnly renders grey otherwise
        box.Dock = DockStyle.Fill;
        try
        {
            using (var s = Payload.Read("LICENSE"))
            using (var r = new StreamReader(s))
                box.Text = r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        catch { box.Text = "MIT License"; }
        box.Select(0, 0);

        var bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 34;

        _accept = new CheckBox();
        _accept.Text = "I accept the terms of the licence";
        _accept.AutoSize = true;
        _accept.Location = new Point(0, 8);
        // Nothing is installed until this is ticked, which is the point of the page.
        _accept.CheckedChanged += (s, e) => _next.Enabled = _accept.Checked;
        bottom.Controls.Add(_accept);

        p.Controls.Add(box);
        p.Controls.Add(bottom);
        return p;
    }

    Panel BuildLocation()
    {
        var p = new Panel();
        const int W = 460;

        p.Controls.Add(Para("Speaker Keeper will be installed in this folder.", 4, W));

        _path = new TextBox();
        _path.Location = new Point(0, 32);
        _path.Width = 360;
        _path.Text = SetupActions.DefaultTarget;
        _path.TextChanged += (s, e) => UpdateSpace();
        p.Controls.Add(_path);

        var browse = new Button();
        browse.Text = "Browse...";
        browse.Size = new Size(92, 26);
        browse.Location = new Point(368, 31);
        browse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Choose where to install Speaker Keeper";
                if (d.ShowDialog(this) == DialogResult.OK)
                    _path.Text = Path.Combine(d.SelectedPath, "Speaker Keeper");
            }
        };
        p.Controls.Add(browse);

        _spaceNote = Para("", 62, W);
        _spaceNote.ForeColor = SystemColors.GrayText;
        p.Controls.Add(_spaceNote);

        _shortcut = new CheckBox();
        _shortcut.Text = "Add a Start Menu shortcut";
        _shortcut.AutoSize = true;
        _shortcut.Checked = true;
        _shortcut.Location = new Point(0, 100);
        p.Controls.Add(_shortcut);

        _autostart = new CheckBox();
        _autostart.Text = "Start Speaker Keeper when I sign in";
        _autostart.AutoSize = true;
        _autostart.Checked = true;
        _autostart.Location = new Point(0, 124);
        p.Controls.Add(_autostart);

        // Offered here because setup is already elevated: creating the SYSTEM update
        // task costs nothing extra now, whereas turning it on later from Settings needs
        // its own UAC prompt. That prompt is easy to dismiss, and dismissing it silently
        // reverts the checkbox - so most people who meant to enable updates never did.
        _autoUpdate = new CheckBox();
        _autoUpdate.Text = "Install updates automatically";
        _autoUpdate.AutoSize = true;
        _autoUpdate.Checked = true;
        _autoUpdate.Location = new Point(0, 148);
        p.Controls.Add(_autoUpdate);

        var note = Para(
            "Checks once a day in the background. Speaker Keeper is not code-signed, so "
            + "updates are verified by checksum over HTTPS. All three can be changed later "
            + "from the tray menu and Settings.", 176, W);
        note.ForeColor = SystemColors.GrayText;
        p.Controls.Add(note);

        UpdateSpace();
        return p;
    }

    void UpdateSpace()
    {
        try
        {
            long need = SetupActions.EstimatedBytes;
            string text = "Space required: " + (need / 1024 / 1024) + " MB";
            var root = Path.GetPathRoot(_path.Text);
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady)
                    text += "          Available on " + di.Name + " " +
                            (di.AvailableFreeSpace / 1024 / 1024 / 1024) + " GB";
            }
            _spaceNote.Text = text;
        }
        catch { _spaceNote.Text = ""; }
    }

    Panel BuildProgress()
    {
        var p = new Panel();
        const int W = 460;

        p.Controls.Add(Para("Please wait while Speaker Keeper is installed.", 4, W));

        _bar = new ProgressBar();
        _bar.Location = new Point(0, 40);
        _bar.Size = new Size(W, 22);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Maximum = 100;
        p.Controls.Add(_bar);

        _step = Para("", 72, W);
        _step.ForeColor = SystemColors.GrayText;
        p.Controls.Add(_step);
        return p;
    }

    Panel BuildFinish()
    {
        var p = new Panel();
        const int W = 460;

        var h = Para("Speaker Keeper is installed", 4, W);
        h.Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Regular);
        p.Controls.Add(h);

        _finishText = Para("", 42, W);
        p.Controls.Add(_finishText);

        _launch = new CheckBox();
        _launch.Text = "Launch Speaker Keeper now";
        _launch.AutoSize = true;
        _launch.Checked = true;
        _launch.Location = new Point(0, 190);
        p.Controls.Add(_launch);
        return p;
    }

    void Go(int page)
    {
        _page = page;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = (i == page);

        switch (page)
        {
            case PageWelcome:
                _headTitle.Text = "Speaker Keeper";
                _headSub.Text = "Keep your Bluetooth speaker awake";
                _back.Enabled = false; _next.Enabled = true; _next.Text = "Next >";
                _cancel.Enabled = true;
                break;
            case PageLicense:
                _headTitle.Text = "Licence";
                _headSub.Text = "Speaker Keeper is free and open source";
                _back.Enabled = true; _next.Enabled = _accept.Checked; _next.Text = "Next >";
                break;
            case PageLocation:
                _headTitle.Text = "Install location";
                _headSub.Text = "Choose where to install, and how it starts";
                _back.Enabled = true; _next.Enabled = true; _next.Text = "Install";
                UpdateSpace();
                break;
            case PageProgress:
                _headTitle.Text = "Installing";
                _headSub.Text = "This only takes a moment";
                _back.Enabled = false; _next.Enabled = false; _cancel.Enabled = false;
                break;
            case PageFinish:
                _headTitle.Text = _error == null ? "Finished" : "Setup failed";
                _headSub.Text = _error == null
                    ? "Speaker Keeper is ready to use"
                    : "Nothing was installed";
                _back.Enabled = false; _next.Enabled = true; _next.Text = "Finish";
                _cancel.Enabled = false;
                break;
        }
        _header.Invalidate();
    }

    void OnNext()
    {
        if (_page == PageFinish) { Finish(); return; }
        if (_page == PageLocation) { StartInstall(); return; }
        Go(_page + 1);
    }

    void OnCancel()
    {
        if (_installed) { Finish(); return; }
        if (MessageBox.Show(this, "Cancel setup? Speaker Keeper will not be installed.",
                            "Speaker Keeper Setup", MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            Close();
    }

    void StartInstall()
    {
        string target = _path.Text.Trim();
        if (target.Length == 0)
        {
            MessageBox.Show(this, "Choose a folder to install into.", "Speaker Keeper Setup");
            return;
        }

        bool autostart = _autostart.Checked, shortcut = _shortcut.Checked;
        bool autoUpdate = _autoUpdate.Checked;
        Go(PageProgress);

        // On a worker thread so the window keeps repainting; writing silent.wav alone is
        // 3.4 MB, and a frozen installer looks like a crashed one.
        var t = new Thread(delegate ()
        {
            int done = 0;
            string warning = null;
            try
            {
                warning = SetupActions.Install(target, autostart, shortcut, autoUpdate,
                    delegate (string msg)
                    {
                        done += 14;
                        int d = Math.Min(done, 100);
                        try { BeginInvoke((MethodInvoker)delegate { _step.Text = msg; _bar.Value = d; }); }
                        catch { }
                    });
            }
            catch (Exception ex) { _error = ex.Message; }

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _bar.Value = 100;
                    _installed = _error == null;
                    string ok = "Speaker Keeper is in your system tray. Hover it to see your "
                              + "speaker and its battery, right-click it for the menu, or "
                              + "double-click it to open Settings.\r\n\r\n"
                              + "Installed to:\r\n" + target;
                    // A warning means it IS installed, so it must not read as a failure.
                    if (warning != null) ok = warning + "\r\n\r\nInstalled to:\r\n" + target;
                    _finishText.Text = _error == null ? ok : "Setup could not complete:\r\n\r\n" + _error;
                    if (_error != null) _launch.Visible = false;
                    Go(PageFinish);
                });
            }
            catch { }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);    // WScript.Shell is an STA COM object
        t.Start();
    }

    void Finish()
    {
        if (_installed && _launch.Visible && _launch.Checked)
            SetupActions.LaunchAsUser(Path.Combine(_path.Text.Trim(), "SpeakerKeeper.exe"));
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _logo != null) _logo.Dispose();
        base.Dispose(disposing);
    }
}
