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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            long total = 0;
            foreach (var f in Payload.Files)
                using (var s = Payload.Read(f)) total += s.Length;
            return total;
        }
    }

    /// <summary>
    /// Files earlier versions left in the install folder that nothing reads any more.
    ///
    /// Installing over an existing copy only overwrites what is being written, so
    /// without this the 3.4 MB silent.wav that versions up to 1.2.1 wrote would sit in
    /// Program Files forever on every machine that ever ran one of them. The .old copies
    /// are the previous binaries an update moved aside; the install has already stopped
    /// the running app, so nothing is holding them open by the time this runs.
    /// </summary>
    static readonly string[] Obsolete = { "silent.wav" };

    static void RemoveObsolete(string target)
    {
        var dead = new List<string>(Obsolete);
        try { dead.AddRange(Directory.GetFiles(target, "*.old")); }
        catch { }

        foreach (var name in dead)
        {
            try
            {
                string f = Path.IsPathRooted(name) ? name : Path.Combine(target, name);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }   // a leftover file is not worth failing an install over
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
        RemoveObsolete(target);

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
///
/// It is drawn with the same Fluent parts as the app, and follows the same light or dark
/// theme, because setup is the first thing anyone sees of Speaker Keeper and a grey 2001
/// dialog would be a promise the app then doesn't keep.
/// </summary>
class SetupWizard : Form
{
    const int PageWelcome = 0, PageLicense = 1, PageLocation = 2, PageProgress = 3, PageFinish = 4;
    const int W = 500;   // usable width inside the content padding

    readonly Panel _content;
    readonly Panel[] _pages = new Panel[5];
    readonly FluentButton _back, _next, _cancel;
    readonly Panel _header;
    readonly Image _logo;

    string _headTitle = "", _headSub = "";

    ToggleSwitch _accept, _autostart, _shortcut, _autoUpdate, _launch;
    FluentTextBox _path;
    Note _spaceNote;
    FluentProgress _bar;
    Note _step;
    Note _finishText;
    Note _launchLabel;

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
        AutoScaleMode = AutoScaleMode.Dpi;
        // Tall enough that the welcome text, the version and the "click Next" line all
        // fit without the content panel clipping the last two.
        ClientSize = new Size(560, 480);
        Font = Fluent.Body;
        BackColor = Fluent.Window;
        DoubleBuffered = true;

        // --- bottom button bar (added first; docking fills in reverse z-order) ---
        var bar = new Panel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 64;
        bar.BackColor = Fluent.Window;
        bar.Paint += (s, e) =>
        {
            using (var p = new Pen(Fluent.Divider))
                e.Graphics.DrawLine(p, 0, 0, bar.Width, 0);
        };

        _cancel = MakeButton("Cancel", false);
        _cancel.Click += (s, e) => OnCancel();
        _next = MakeButton("Next", true);
        _next.Click += (s, e) => OnNext();
        _back = MakeButton("Back", false);
        _back.Click += (s, e) => Go(_page - 1);

        var flow = new FlowLayoutPanel();
        flow.Dock = DockStyle.Right;
        flow.FlowDirection = FlowDirection.RightToLeft;
        flow.WrapContents = false;
        flow.AutoSize = true;
        flow.BackColor = Fluent.Window;
        flow.Padding = new Padding(0, 16, 20, 0);
        flow.Controls.Add(_next);       // right-to-left: rightmost is added first
        flow.Controls.Add(_cancel);
        flow.Controls.Add(_back);
        bar.Controls.Add(flow);

        // --- header ---
        _header = new Panel();
        _header.Dock = DockStyle.Top;
        _header.Height = 84;
        _header.BackColor = Fluent.Card;
        _header.Paint += PaintHeader;

        // --- content ---
        _content = new Panel();
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Fluent.Window;
        _content.Padding = new Padding(28, 24, 28, 12);

        _pages[PageWelcome] = BuildWelcome();
        _pages[PageLicense] = BuildLicense();
        _pages[PageLocation] = BuildLocation();
        _pages[PageProgress] = BuildProgress();
        _pages[PageFinish] = BuildFinish();
        foreach (var p in _pages)
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            p.BackColor = Fluent.Window;
            _content.Controls.Add(p);
        }

        Controls.Add(_content);
        Controls.Add(_header);
        Controls.Add(bar);

        Go(PageWelcome);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fluent.Trim(this, false);
    }

    void PaintHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.Quality(g);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var b = new SolidBrush(Fluent.Card)) g.FillRectangle(b, _header.ClientRectangle);
        g.DrawImage(_logo, new Rectangle(28, 16, 52, 52));

        Fluent.Draw(g, _headTitle, Fluent.Subtitle, Fluent.Text, new Rectangle(96, 18, _header.Width - 120, 28),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        Fluent.Draw(g, _headSub, Fluent.Caption, Fluent.TextSecondary, new Rectangle(96, 46, _header.Width - 120, 22),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        using (var p = new Pen(Fluent.Divider))
            g.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
    }

    static FluentButton MakeButton(string text, bool primary)
    {
        var b = new FluentButton();
        b.Text = text;
        b.Primary = primary;
        b.Size = new Size(104, 32);
        b.Margin = new Padding(8, 0, 0, 0);
        return b;
    }

    /// <summary>
    /// Adds a block below the last one and returns the next free y.
    ///
    /// The paragraphs on these pages wrap to whatever the text needs, so laying them out
    /// at fixed offsets means the longest one silently runs under whatever follows it.
    /// </summary>
    static int Stack(Panel p, Control c, int y, int gap)
    {
        c.Top = y;
        p.Controls.Add(c);
        return y + c.Height + gap;
    }

    /// <summary>A block of body text, sized to its own wrapped height.</summary>
    static Note Para(string text, int y, int width, bool secondary)
    {
        var n = new Note();
        n.Primary = !secondary;
        n.Text = text;
        n.SetBounds(0, y, width, 20);
        n.FitHeight();
        return n;
    }

    /// <summary>A card with a switch on it, matching the rows in the app's own settings.</summary>
    SettingsCard Option(string glyph, string title, string description, int y, out ToggleSwitch toggle)
    {
        toggle = new ToggleSwitch();
        toggle.SetSilently(true);
        var c = new SettingsCard();
        c.Glyph = glyph;
        c.Title = title;
        c.Description = description;
        c.SetBounds(0, y, W, 56);
        c.SetAction(toggle);
        c.FitHeight();
        return c;
    }

    Panel BuildWelcome()
    {
        var p = new Panel();

        var h = new Heading();
        h.Text = "Welcome to Speaker Keeper";
        h.SetBounds(0, 0, W, 34);
        int y = Stack(p, h, 0, 12);

        y = Stack(p, Para(
            "Bluetooth speakers switch themselves off after a few minutes of quiet. That is "
            + "why the first second of your music keeps getting cut off, and why you sometimes "
            + "have to reconnect the speaker by hand.\r\n\r\n"
            + "Speaker Keeper quietly plays silence in the background, so your speaker never "
            + "nods off. You hear nothing - it simply stays awake, like a wired speaker.\r\n\r\n"
            + "It sits in your system tray and shows your speaker's battery. Windows never sees "
            + "it: no media card, no volume mixer row, and your media keys keep working.",
            0, W, false), y, 20);

        y = Stack(p, Para("Version " + SetupActions.Version, 0, W, true), y, 8);
        Stack(p, Para("Click Next to continue.", 0, W, false), y, 0);
        return p;
    }

    Panel BuildLicense()
    {
        var p = new Panel();

        var frame = new Panel();
        frame.Dock = DockStyle.Fill;
        frame.Padding = new Padding(1);
        frame.BackColor = Fluent.Stroke;

        var box = new TextBox();
        box.Multiline = true;
        box.ReadOnly = true;
        box.ScrollBars = ScrollBars.Vertical;
        box.BorderStyle = BorderStyle.None;
        // ReadOnly renders grey unless the colours are set explicitly.
        box.BackColor = Fluent.Card;
        box.ForeColor = Fluent.TextSecondary;
        box.Font = Fluent.Caption;
        box.Dock = DockStyle.Fill;
        try
        {
            using (var s = Payload.Read("LICENSE"))
            using (var r = new StreamReader(s))
                box.Text = r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        catch { box.Text = "MIT License"; }
        box.Select(0, 0);
        box.HandleCreated += (s, e) => Fluent.DarkScrollbars(box);
        frame.Controls.Add(box);

        var bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 48;
        bottom.BackColor = Fluent.Window;

        // Nothing is installed until this is on, which is the point of the page.
        _accept = new ToggleSwitch();
        _accept.SetSilently(false);
        _accept.Location = new Point(0, 16);
        _accept.Toggled += (s, e) => _next.Enabled = _accept.On;
        bottom.Controls.Add(_accept);

        var label = new Note();
        label.Primary = true;
        label.Text = "I accept the terms of the licence";
        label.SetBounds(_accept.Right + 12, 8, 320, 32);
        bottom.Controls.Add(label);

        var spacer = new Panel();
        spacer.Dock = DockStyle.Bottom;
        spacer.Height = 8;
        spacer.BackColor = Fluent.Window;

        p.Controls.Add(frame);
        p.Controls.Add(spacer);
        p.Controls.Add(bottom);
        return p;
    }

    Panel BuildLocation()
    {
        var p = new Panel();

        p.Controls.Add(Para("Speaker Keeper will be installed in this folder.", 0, W, false));

        _path = new FluentTextBox();
        _path.SetBounds(0, 28, W - 116, 32);
        _path.Text = SetupActions.DefaultTarget;
        _path.Inner.TextChanged += (s, e) => UpdateSpace();
        p.Controls.Add(_path);

        var browse = new FluentButton();
        browse.Text = "Browse";
        browse.SetBounds(W - 104, 28, 104, 32);
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

        _spaceNote = Para("", 68, W, true);
        p.Controls.Add(_spaceNote);

        p.Controls.Add(Option(Fluent.IconFolder, "Add a Start Menu shortcut", null, 100, out _shortcut));
        p.Controls.Add(Option(Fluent.IconPower, "Start Speaker Keeper when I sign in", null, 160, out _autostart));

        // Offered here because setup is already elevated: creating the SYSTEM update
        // task costs nothing extra now, whereas turning it on later from Settings needs
        // its own UAC prompt. That prompt is easy to dismiss, and dismissing it silently
        // reverts the switch - so most people who meant to enable updates never did.
        p.Controls.Add(Option(Fluent.IconDownload, "Install updates automatically",
            "Checks once a day in the background. Speaker Keeper is not code-signed, so "
            + "updates are verified by checksum over HTTPS.", 220, out _autoUpdate));

        p.Controls.Add(Para("All three can be changed later from Settings.", 300, W, true));

        UpdateSpace();
        return p;
    }

    void UpdateSpace()
    {
        try
        {
            long need = SetupActions.EstimatedBytes;
            string text = "Space required: " + Math.Max(1, need / 1024 / 1024) + " MB";
            var root = Path.GetPathRoot(_path.Text);
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady)
                    text += "          Available on " + di.Name + " " +
                            (di.AvailableFreeSpace / 1024 / 1024 / 1024) + " GB";
            }
            _spaceNote.Text = text;
            _spaceNote.FitHeight();
            _spaceNote.Invalidate();
        }
        catch { _spaceNote.Text = ""; }
    }

    Panel BuildProgress()
    {
        var p = new Panel();

        p.Controls.Add(Para("Please wait while Speaker Keeper is installed.", 0, W, false));

        _bar = new FluentProgress();
        _bar.SetBounds(0, 44, W, 12);
        p.Controls.Add(_bar);

        _step = Para("", 68, W, true);
        p.Controls.Add(_step);
        return p;
    }

    Panel BuildFinish()
    {
        var p = new Panel();

        var h = new Heading();
        h.Text = "Speaker Keeper is installed";
        h.SetBounds(0, 0, W, 34);
        Stack(p, h, 0, 12);

        _finishText = Para("", 46, W, false);
        p.Controls.Add(_finishText);

        _launch = new ToggleSwitch();
        _launch.SetSilently(true);
        p.Controls.Add(_launch);

        _launchLabel = new Note();
        _launchLabel.Primary = true;
        _launchLabel.Text = "Launch Speaker Keeper now";
        _launchLabel.Width = 320;
        p.Controls.Add(_launchLabel);

        LayoutFinish();
        return p;
    }

    /// <summary>The launch switch sits under the message, whose length isn't known until then.</summary>
    void LayoutFinish()
    {
        _finishText.FitHeight();
        int y = _finishText.Bottom + 24;
        _launch.Location = new Point(0, y + 6);
        _launchLabel.SetBounds(_launch.Right + 12, y, 320, 32);

        // Controls added later sit lower in the z-order, and the message grows over
        // where these started out, so without this the switch ends up behind the text.
        _launch.BringToFront();
        _launchLabel.BringToFront();
    }

    void Go(int page)
    {
        _page = page;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = (i == page);

        switch (page)
        {
            case PageWelcome:
                _headTitle = "Speaker Keeper";
                _headSub = "Keep your Bluetooth speaker awake";
                _back.Enabled = false; _next.Enabled = true; _next.Text = "Next";
                _cancel.Enabled = true;
                break;
            case PageLicense:
                _headTitle = "Licence";
                _headSub = "Speaker Keeper is free and open source";
                _back.Enabled = true; _next.Enabled = _accept.On; _next.Text = "Next";
                break;
            case PageLocation:
                _headTitle = "Install location";
                _headSub = "Choose where to install, and how it starts";
                _back.Enabled = true; _next.Enabled = true; _next.Text = "Install";
                UpdateSpace();
                break;
            case PageProgress:
                _headTitle = "Installing";
                _headSub = "This only takes a moment";
                _back.Enabled = false; _next.Enabled = false; _cancel.Enabled = false;
                break;
            case PageFinish:
                _headTitle = _error == null ? "Finished" : "Setup failed";
                _headSub = _error == null
                    ? "Speaker Keeper is ready to use"
                    : "Nothing was installed";
                _back.Enabled = false; _next.Enabled = true; _next.Text = "Finish";
                _cancel.Enabled = false;
                break;
        }
        _header.Invalidate();
        _back.Invalidate(); _next.Invalidate(); _cancel.Invalidate();
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

        bool autostart = _autostart.On, shortcut = _shortcut.On;
        bool autoUpdate = _autoUpdate.On;
        Go(PageProgress);

        // On a worker thread so the window keeps repainting: the install stops a running
        // copy and waits on it, and a frozen installer looks like a crashed one.
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
                        try { BeginInvoke((MethodInvoker)delegate { _step.Text = msg; _step.Invalidate(); _bar.Value = d; }); }
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
                    string ok = "Speaker Keeper is in your system tray. Click it once for the "
                              + "panel - your speaker, its battery, and a switch to turn it off "
                              + "for that speaker - or use the cog there to open Settings.\r\n\r\n"
                              + "Installed to:\r\n" + target;
                    // A warning means it IS installed, so it must not read as a failure.
                    if (warning != null) ok = warning + "\r\n\r\nInstalled to:\r\n" + target;
                    _finishText.Text = _error == null ? ok : "Setup could not complete:\r\n\r\n" + _error;
                    LayoutFinish();
                    _finishText.Invalidate();
                    if (_error != null) { _launch.Visible = false; _launchLabel.Visible = false; }
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
        if (_installed && _launch.Visible && _launch.On)
            SetupActions.LaunchAsUser(Path.Combine(_path.Text.Trim(), "SpeakerKeeper.exe"));
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _logo != null) _logo.Dispose();
        base.Dispose(disposing);
    }
}
