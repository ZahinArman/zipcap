// Zipcap: press Win+Shift+N anywhere, type, press Enter -> the text is appended
// to the bottom of the Notion page you picked. Mac-style floating panel (WPF).
// Build with build.cmd (uses the C# compiler that ships with Windows, no installs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: System.Reflection.AssemblyTitle("Zipcap")]
[assembly: System.Reflection.AssemblyProduct("Zipcap")]
[assembly: System.Reflection.AssemblyDescription("Capture a thought to Notion or Todoist from anywhere")]
[assembly: System.Reflection.AssemblyVersion("0.1.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.1.0")]

static class Program
{
    public const string Version = "0.1.1"; // release.ps1 keeps this and the attributes above in sync
    public const string Repo = "ZahinArman/zipcap";

    [STAThread]
    static void Main()
    {
        bool isNew;
        using (var mutex = new Mutex(true, "ZipcapSingleInstance", out isNew))
        {
            if (!isNew) return; // already running
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Store.MigrateLegacy();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new ZipcapWindow();
            app.Run();
            GC.KeepAlive(win);
        }
    }
}

// Everything Zipcap keeps on this PC lives in %APPDATA%\Zipcap. Keys are encrypted with
// Windows DPAPI (CurrentUser), so only this Windows account can read them. Nothing secret
// is ever written anywhere else, logged, or shown.
static class Store
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zipcap");
    public static readonly string PendingDir = System.IO.Path.Combine(Dir, "pending");
    public const string NotionKey = "notion-key", TodoistToken = "todoist-token";
    static readonly string PageFile = System.IO.Path.Combine(Dir, "page-id.txt");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zipcap.v1");

    static string SecretPath(string name) { return System.IO.Path.Combine(Dir, name + ".bin"); }

    public static bool Has(string name) { return File.Exists(SecretPath(name)); }

    public static string Read(string name)
    {
        try
        {
            var data = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(SecretPath(name)), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return null; } // missing, or encrypted by another Windows account
    }

    public static void Write(string name, string value)
    {
        Directory.CreateDirectory(Dir);
        var data = System.Security.Cryptography.ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SecretPath(name), data);
    }

    // The capture shortcut, as RegisterHotKey modifier bits + a virtual-key code. Plain text
    // ("mods vk"), nothing secret. Missing or unreadable -> the default, Win+Shift+N.
    static readonly string HotkeyFile = System.IO.Path.Combine(Dir, "hotkey.txt");
    public const uint DefaultMods = 0x8 | 0x4, DefaultVk = 0x4E; // MOD_WIN | MOD_SHIFT, N

    public static void ReadHotkey(out uint mods, out uint vk)
    {
        mods = DefaultMods; vk = DefaultVk;
        try
        {
            var p = File.ReadAllText(HotkeyFile).Trim().Split(' ');
            uint m = uint.Parse(p[0]), v = uint.Parse(p[1]);
            if ((m & 0xB) != 0 && (m & ~0xFu) == 0 && v > 0 && v < 0xFF) { mods = m; vk = v; } // needs Ctrl, Alt or Win
        }
        catch { }
    }

    public static void WriteHotkey(uint mods, uint vk)
    {
        Directory.CreateDirectory(Dir);
        if (mods == DefaultMods && vk == DefaultVk) { try { File.Delete(HotkeyFile); } catch { } return; }
        File.WriteAllText(HotkeyFile, mods + " " + vk);
    }

    public static string PageId
    {
        get { try { var s = File.ReadAllText(PageFile).Trim(); return s.Length == 32 ? s : null; } catch { return null; } }
        set { Directory.CreateDirectory(Dir); File.WriteAllText(PageFile, value); }
    }

    // A Notion page link (or bare ID) -> the 32-hex page ID, or null.
    // Handles notion.so/Title-<id>, notion.so/<workspace>/<id>?v=..., and dashed UUIDs.
    public static string ParsePageId(string link)
    {
        if (link == null) return null;
        var s = link.Trim();
        int cut = s.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) s = s.Substring(0, cut);
        var dashed = System.Text.RegularExpressions.Regex.Match(s,
            "([0-9a-fA-F]{8})-([0-9a-fA-F]{4})-([0-9a-fA-F]{4})-([0-9a-fA-F]{4})-([0-9a-fA-F]{12})");
        if (dashed.Success) return dashed.Value.Replace("-", "").ToLowerInvariant();
        var plain = System.Text.RegularExpressions.Regex.Matches(s, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])");
        return plain.Count > 0 ? plain[plain.Count - 1].Value.ToLowerInvariant() : null;
    }

    // legacy: one-time move from the pre-Zipcap folder (%APPDATA%\QuickCapture, plaintext keys).
    // Keys are re-saved encrypted and the plaintext copies deleted. Waiting notes move over.
    // The page isn't carried over on purpose: no page ID is baked into the app, so you pick it once.
    public static void MigrateLegacy()
    {
        try
        {
            var old = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickCapture");
            if (!Directory.Exists(old)) return;
            Directory.CreateDirectory(Dir);
            foreach (var pair in new[] { new[] { "notion-key.txt", NotionKey }, new[] { "todoist-token.txt", TodoistToken } })
            {
                var f = System.IO.Path.Combine(old, pair[0]);
                if (!File.Exists(f)) continue;
                var v = File.ReadAllText(f).Trim();
                if (v.Length > 0 && !Has(pair[1])) Write(pair[1], v);
                File.Delete(f);
            }
            var oldPending = System.IO.Path.Combine(old, "pending");
            if (Directory.Exists(oldPending))
            {
                Directory.CreateDirectory(PendingDir);
                foreach (var f in Directory.GetFiles(oldPending))
                {
                    var to = System.IO.Path.Combine(PendingDir, System.IO.Path.GetFileName(f));
                    if (!File.Exists(to)) File.Move(f, to);
                }
            }
            Directory.Delete(old, true);
        }
        catch { } // never block startup on this
    }
}

class Theme
{
    public Color Window, Hairline, Outline, Text, Secondary, Accent, Selection, KeyFill, KeyEdge, Success, Error;
    public bool Dark;

    public static Theme Current()
    {
        bool dark = false;
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            dark = v is int && (int)v == 0;
        }
        catch { }
        var force = Environment.GetEnvironmentVariable("ZIPCAP_THEME"); // "light"/"dark" override for testing
        if (force == "light") dark = false; else if (force == "dark") dark = true;
        return dark
            ? new Theme
            {
                Dark = true,
                Window = C(0x24, 0x24, 0x26), Hairline = C(0xFF, 0xFF, 0xFF, 0x1F), Outline = C(0, 0, 0, 0x99),
                Text = C(0xF5, 0xF5, 0xF7), Secondary = C(0x9C, 0x9C, 0xA1),
                Accent = C(0x0A, 0x84, 0xFF), Selection = C(0x0A, 0x84, 0xFF, 0x66),
                KeyFill = C(0xFF, 0xFF, 0xFF, 0x12), KeyEdge = C(0xFF, 0xFF, 0xFF, 0x1A),
                Success = C(0x32, 0xD7, 0x4B), Error = C(0xFF, 0x6B, 0x61)
            }
            : new Theme
            {
                Window = C(0xFC, 0xFC, 0xFB), Hairline = C(0, 0, 0, 0x14), Outline = C(0, 0, 0, 0x2E),
                Text = C(0x1D, 0x1D, 0x1F), Secondary = C(0x6E, 0x6E, 0x73),
                Accent = C(0x00, 0x7A, 0xFF), Selection = C(0x00, 0x7A, 0xFF, 0x40),
                KeyFill = C(0, 0, 0, 0x08), KeyEdge = C(0, 0, 0, 0x14),
                Success = C(0x1F, 0x9D, 0x3A), Error = C(0xD7, 0x34, 0x2A)
            };
    }

    static Color C(byte r, byte g, byte b, byte a = 0xFF) { return Color.FromArgb(a, r, g, b); }
}

class ZipcapWindow : Window
{
    const string NotionDevToolsUrl = "https://www.notion.so/profile/integrations";
    const string TodoistTokenUrl = "https://app.todoist.com/app/settings/integrations/developer";
    // ZIPCAP_TODOIST lets a test point task sending at a fake server.
    static readonly string TodoistBase = Environment.GetEnvironmentVariable("ZIPCAP_TODOIST") ?? "https://api.todoist.com";
    static readonly System.Text.RegularExpressions.Regex TaskPrefix =
        new System.Text.RegularExpressions.Regex(@"^\s*t:\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly Color TodoistRed = Color.FromRgb(0xE4, 0x43, 0x32);

    // A note that starts with "t:" becomes a Todoist task instead of a Zipcap note.
    static bool IsTask(string text) { var m = TaskPrefix.Match(text); return m.Success && text.Length > m.Length; }
    // ZIPCAP_API lets a test point the app at a dead address to simulate being offline.
    static readonly string ApiBase = Environment.GetEnvironmentVariable("ZIPCAP_API") ?? "https://api.notion.com";
    static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    uint hkMods, hkVk; // the capture shortcut in use (see Store.ReadHotkey)
    Forms.ToolStripMenuItem captureItem;
    const int WM_HOTKEY = 0x0312, HOTKEY_ID = 1;
    const double ShadowPad = 28; // transparent room around the panel for the drop shadow

    Theme t;
    Border panel;
    TextBox box;
    PasswordBox keyBox;
    TextBox linkBox;  // setup step 2: the Notion page link (not secret, so shown in clear)
    TextBlock title, placeholder, status, help;
    StackPanel hints;
    Path statusIcon;
    Forms.NotifyIcon tray;
    IntPtr hwnd;
    bool positioned, hiding, keyMode; // keyMode: the panel is showing a setup step, not the note box
    string setup; // which setup step: "notion" (key), "page" (page link), "todoist" (token); null = capturing
    int level; // 0 = default height; each Ctrl+Down adds one more default-height step
    const double LinesSmall = 150; // default typing-area height, about 7 lines

    public ZipcapWindow()
    {
        t = Theme.Current();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 420 + ShadowPad * 2;
        SizeToContent = SizeToContent.Height;
        Title = "Zipcap";
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        Content = BuildUi();
        PreviewKeyDown += OnKey;


        hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        Store.ReadHotkey(out hkMods, out hkVk);
        BuildTray();
        if (!RegisterHotKey(hwnd, HOTKEY_ID, hkMods | MOD_NOREPEAT, hkVk))
            Notify(HotkeyText(hkMods, hkVk) + " is taken by another app. Pick another in the tray menu (Change shortcut…), " +
                   "or click this tray icon to capture.");

        // Retry quietly every 30s while something is waiting, and right away when the
        // internet comes back. Also send anything left over from last time (e.g. after a reboot).
        retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        retry.Tick += (s, e) => Flush();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (s, e) =>
        { if (e.IsAvailable) Dispatcher.BeginInvoke(new Action(Flush)); };
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) =>
            Dispatcher.BeginInvoke(new Action(() => { if (PendingFiles().Length > 0) Flush(); }));
        UpdateTray();
        Flush();
    }

    // ---------- UI ----------

    UIElement BuildUi()
    {
        var root = new Grid { Margin = new Thickness(ShadowPad) };

        // Outer 1px line (separates the panel from any background), inner 1px hairline highlight.
        panel = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(t.Window),
            BorderBrush = new SolidColorBrush(t.Outline),
            BorderThickness = new Thickness(0.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, Direction = 270, ShadowDepth = 10, BlurRadius = 34,
                Opacity = t.Dark ? 0.55 : 0.22, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            },
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = new ScaleTransform(1, 1)
        };
        var inner = new Border
        {
            CornerRadius = new CornerRadius(11.5),
            BorderBrush = new SolidColorBrush(t.Hairline),
            BorderThickness = new Thickness(t.Dark ? 0.5 : 0)
        };
        panel.Child = inner;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // setup help
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        inner.Child = layout;

        // Title bar: traffic lights + centered title. Whole bar drags the window.
        var bar = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Arrow };
        bar.MouseLeftButtonDown += Drag;
        title = new TextBlock
        {
            Text = "Zipcap", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(t.Secondary),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        bar.Children.Add(title);
        bar.Children.Add(TrafficLights());
        Grid.SetRow(bar, 0);
        layout.Children.Add(bar);

        // Editor
        var editor = new Grid { Margin = new Thickness(18, 2, 18, 0) };
        box = new TextBox
        {
            FontSize = 15, MinHeight = Math.Min(LinesSmall * (level + 1), 2000), MaxHeight = level == 0 ? 400 : 2000,
            AcceptsReturn = false, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(t.Text),
            CaretBrush = new SolidColorBrush(t.Accent),
            SelectionBrush = new SolidColorBrush(t.Selection), SelectionOpacity = 1,
            Padding = new Thickness(0), FocusVisualStyle = null
        };
        box.TextChanged += (s, e) => { UpdatePlaceholder(); UpdateTitle(); };
        box.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), ThinScrollBar());
        keyBox = new PasswordBox
        {
            FontSize = 15, Height = 30, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(t.Text), CaretBrush = new SolidColorBrush(t.Accent),
            SelectionBrush = new SolidColorBrush(t.Selection), SelectionOpacity = 1,
            Padding = new Thickness(0), FocusVisualStyle = null, Visibility = Visibility.Collapsed
        };
        keyBox.PasswordChanged += (s, e) => UpdatePlaceholder();
        linkBox = new TextBox
        {
            FontSize = 15, Height = 30, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(t.Text), CaretBrush = new SolidColorBrush(t.Accent),
            SelectionBrush = new SolidColorBrush(t.Selection), SelectionOpacity = 1,
            Padding = new Thickness(0), FocusVisualStyle = null, Visibility = Visibility.Collapsed
        };
        linkBox.TextChanged += (s, e) => UpdatePlaceholder();
        placeholder = new TextBlock
        {
            FontSize = 15, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0),
            Foreground = new SolidColorBrush(t.Secondary), TextWrapping = TextWrapping.Wrap
        };
        editor.Children.Add(box);
        editor.Children.Add(keyBox);
        editor.Children.Add(linkBox);
        editor.Children.Add(placeholder);
        Grid.SetRow(editor, 1);
        layout.Children.Add(editor);

        // Setup help: one or two short lines on where to find the key, with a link that opens it.
        help = new TextBlock
        {
            FontSize = 12, Margin = new Thickness(18, 2, 18, 6), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(t.Secondary), LineHeight = 17, Visibility = Visibility.Collapsed
        };
        Grid.SetRow(help, 2);
        layout.Children.Add(help);

        // Footer: key hints on the left, status on the right. Also drags.
        var foot = new Grid { Margin = new Thickness(16, 0, 16, 0), Background = Brushes.Transparent };
        foot.MouseLeftButtonDown += Drag;
        hints = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusIcon = new Path
        {
            Data = Geometry.Parse("M1,5.5 L4.2,8.6 L10,1.8"), StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 11, Height = 10,
            Margin = new Thickness(0, 1, 6, 0), Visibility = Visibility.Collapsed
        };
        status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(statusIcon);
        statusRow.Children.Add(status);
        foot.Children.Add(hints);
        foot.Children.Add(statusRow);
        Grid.SetRow(foot, 3);
        layout.Children.Add(foot);

        root.Children.Add(panel);
        SetMode(null);
        return root;
    }

    UIElement TrafficLights()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        var glyphs = new List<Path>();
        row.Children.Add(Light(Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xE1, 0x45, 0x3F),
            "M3.2,3.2 L8.8,8.8 M8.8,3.2 L3.2,8.8", "Close (Esc)", HideBox, glyphs));
        row.Children.Add(Light(Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0xDE, 0x9F, 0x1E),
            "M2.8,6 L9.2,6", "Hide", HideBox, glyphs));
        row.Children.Add(Light(Color.FromRgb(0x28, 0xC8, 0x40), Color.FromRgb(0x1D, 0xAA, 0x31),
            "M3.6,8.4 L3.6,4.6 L7.4,8.4 Z M8.4,3.6 L8.4,7.4 L4.6,3.6 Z", "Open the full page in Notion", OpenPage, glyphs));
        // Like macOS: hovering the group reveals the glyphs on all three.
        row.MouseEnter += (s, e) => { foreach (var g in glyphs) g.Opacity = 1; };
        row.MouseLeave += (s, e) => { foreach (var g in glyphs) g.Opacity = 0; };
        return row;
    }

    UIElement Light(Color fill, Color edge, string glyph, string tip, Action click, List<Path> glyphs)
    {
        var g = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip, Cursor = Cursors.Arrow };
        var dot = new Ellipse { Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(edge), StrokeThickness = 0.5 };
        bool filled = glyph.Contains("Z");
        var p = new Path
        {
            Data = Geometry.Parse(glyph), Width = 12, Height = 12, Stretch = Stretch.None,
            Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x4D, 0x00, 0x00)),
            Fill = filled ? new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x40, 0x10)) : null,
            StrokeThickness = filled ? 0 : 1.3, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, Opacity = 0, IsHitTestVisible = false
        };
        if (filled) p.Stroke = null;
        glyphs.Add(p);
        var press = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
        var overlay = new Ellipse { Fill = Brushes.Transparent, IsHitTestVisible = false };
        g.Children.Add(dot);
        g.Children.Add(overlay);
        g.Children.Add(p);
        g.MouseLeftButtonDown += (s, e) => { overlay.Fill = press; e.Handled = true; g.CaptureMouse(); };
        g.MouseLeftButtonUp += (s, e) =>
        {
            overlay.Fill = Brushes.Transparent; g.ReleaseMouseCapture();
            if (g.IsMouseOver) click();
        };
        return g;
    }

    void SetHints(params string[] pairs)
    {
        hints.Children.Clear();
        for (int i = 0; i < pairs.Length; i += 2)
        {
            // Key glyphs are drawn (font fallback renders ↵ / ⇧ tiny and blurry on Windows).
            var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var k in pairs[i].Split(' '))
            {
                string glyph = null;
                if (k == "RET") glyph = "M9,1.5 L9,6 L2,6 M4.6,3.4 L2,6 L4.6,8.6";
                else if (k == "SHIFT") glyph = "M5.5,1.2 L10,6 L7.6,6 L7.6,9.5 L3.4,9.5 L3.4,6 L1,6 Z";
                else if (k == "DOWN") glyph = "M5.5,1.5 L5.5,9.5 M2,6 L5.5,9.5 L9,6";
                else if (k == "UP") glyph = "M5.5,9.5 L5.5,1.5 M2,5 L5.5,1.5 L9,5";
                if (glyph != null)
                    keys.Children.Add(new Path
                    {
                        Data = Geometry.Parse(glyph),
                        Stroke = new SolidColorBrush(t.Secondary), StrokeThickness = 1.2,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round, Width = 11, Height = 11,
                        Margin = new Thickness(keys.Children.Count > 0 ? 3 : 0, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                else
                    keys.Children.Add(new TextBlock
                    {
                        Text = k, FontSize = 11, Foreground = new SolidColorBrush(t.Secondary),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1)
                    });
            }
            var cap = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 0), MinWidth = 22, Height = 18,
                Background = new SolidColorBrush(t.KeyFill), BorderBrush = new SolidColorBrush(t.KeyEdge),
                BorderThickness = new Thickness(0.5), Margin = new Thickness(i == 0 ? 0 : 12, 0, 6, 0),
                Child = keys
            };
            hints.Children.Add(cap);
            hints.Children.Add(new TextBlock
            {
                Text = pairs[i + 1], FontSize = 12, Foreground = new SolidColorBrush(t.Secondary),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    void SetMode(string kind)
    {
        setup = kind;
        keyMode = kind != null;
        HookKeys(kind == "hotkey");
        bool page = kind == "page", hk = kind == "hotkey";
        box.Visibility = keyMode ? Visibility.Collapsed : Visibility.Visible;
        keyBox.Visibility = keyMode && !page && !hk ? Visibility.Visible : Visibility.Collapsed;
        linkBox.Visibility = page ? Visibility.Visible : Visibility.Collapsed;
        help.Visibility = keyMode ? Visibility.Visible : Visibility.Collapsed;
        if (hk)
        {
            title.Text = "Change shortcut";
            title.Foreground = new SolidColorBrush(t.Secondary);
            SetHelp(kind);
            SetHints("DEL", "Reset to Win+Shift+N", "esc", "Cancel");
        }
        else if (keyMode)
        {
            title.Text = kind == "todoist" ? "Connect Todoist" : page ? "Choose a Notion page (2 of 2)" : "Connect Notion (1 of 2)";
            title.Foreground = new SolidColorBrush(t.Secondary);
            SetHelp(kind);
            SetHints("RET", page ? "Save page" : "Save key", "esc", "Cancel");
        }
        else
        {
            UpdateTitle();
            SetHints("RET", "Save", "SHIFT RET", "New line",
                level > 0 ? "ctrl UP" : "ctrl DOWN", level > 0 ? "Default" : "Taller", "esc", "Close");
        }
        ClearStatus();
        UpdatePlaceholder();
    }

    void SetHelp(string kind)
    {
        help.Inlines.Clear();
        if (kind == "notion")
        {
            help.Inlines.Add("Developer tools → New connection (API token) → copy the token.  ");
            help.Inlines.Add(Link("Open Notion developer tools", NotionDevToolsUrl));
        }
        else if (kind == "hotkey")
        {
            help.Inlines.Add("Now: " + HotkeyText(hkMods, hkVk) + ". Hold Ctrl, Alt or Win (Shift optional) and press a key. It's saved as soon as you press it.");
        }
        else if (kind == "page")
        {
            help.Inlines.Add("Give your connection the page (its Content access tab → Edit access). Then on the page: Share → Copy link, and paste it here.");
        }
        else
        {
            help.Inlines.Add("Settings → Integrations → Developer → copy the API token.  ");
            help.Inlines.Add(Link("Open Todoist settings", TodoistTokenUrl));
        }
    }

    System.Windows.Documents.Hyperlink Link(string text, string url)
    {
        var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text))
        {
            Foreground = new SolidColorBrush(t.Accent), TextDecorations = null, Cursor = Cursors.Hand
        };
        h.Click += (s, e) => { try { System.Diagnostics.Process.Start(url); } catch { } };
        return h;
    }

    // Opens the panel on a setup step, ready to paste.
    void StartSetup(string kind)
    {
        if (!IsVisible || hiding) ShowBox();
        SetMode(kind);
        FocusInput();
    }

    void FocusInput()
    {
        if (setup == "hotkey") Focus(); // keys go to OnKey; there's nothing to type into
        else if (setup == "page") linkBox.Focus();
        else if (keyMode) keyBox.Focus();
        else { box.Focus(); box.CaretIndex = box.Text.Length; ShowLatest(); }
    }

    // Title tells you where Enter will send the note: Zipcap, or Todoist when it starts with "t:".
    void UpdateTitle()
    {
        if (keyMode) return;
        bool task = IsTask(box.Text);
        title.Text = task ? "New Todoist task" : "Zipcap";
        title.Foreground = new SolidColorBrush(task ? TodoistRed : t.Secondary);
    }

    void UpdatePlaceholder()
    {
        placeholder.Text = !keyMode ? "What's on your mind?   ·   t: for a Todoist task"
            : setup == "todoist" ? "Paste your Todoist API token"
            : setup == "page" ? "Paste the Notion page link"
            : setup == "hotkey" ? "Press the new shortcut…"
            : "Paste your Notion API token (starts with ntn_)";
        bool empty = setup == "hotkey" ? true
                   : setup == "page" ? linkBox.Text.Length == 0
                   : keyMode ? keyBox.Password.Length == 0 : box.Text.Length == 0;
        placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetStatus(string text, Color color, bool check)
    {
        status.Text = text;
        status.Foreground = new SolidColorBrush(color);
        statusIcon.Stroke = new SolidColorBrush(color);
        statusIcon.Visibility = check ? Visibility.Visible : Visibility.Collapsed;
        hints.Visibility = text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; // status takes the row
    }

    void ClearStatus() { SetStatus("", t.Secondary, false); }

    // Tallest the typing area may get without the panel running off the bottom of the screen.
    double ScreenLimit()
    {
        var wa = SystemParameters.WorkArea;
        double panelTop = Top + ShadowPad;
        return Math.Max(LinesSmall, wa.Bottom - panelTop - 38 - 40 - 16);
    }

    double MaxFor(int lvl) { return lvl == 0 ? 400 : ScreenLimit(); }

    // Ctrl+Down: each press adds another default-height step (until the screen bottom).
    // Ctrl+Up: straight back to the default. Either way the latest text stays in view.
    void Resize(bool grow)
    {
        int next = grow ? level + 1 : 0;
        double to = Math.Min(LinesSmall * (next + 1), ScreenLimit());
        if (grow && to <= box.MinHeight + 1) return; // already at the screen limit
        if (next == level) return;
        level = next;
        SetMode(setup); // refresh the footer hint (Taller <-> Default)
        double max = MaxFor(level);
        // Where the box will settle: at least the new size, more if the text needs it (up to the cap).
        double settle = Math.Max(to, Math.Min(box.ExtentHeight, max));
        double from = box.ActualHeight;
        var dur = TimeSpan.FromMilliseconds(200);
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        // Ease both bounds so the height glides even when long text is pushing against them.
        var aMin = new DoubleAnimation(from, to, dur) { EasingFunction = ease };
        var aMax = new DoubleAnimation(from, settle, dur) { EasingFunction = ease };
        aMax.Completed += (s, e) =>
        {
            box.BeginAnimation(MinHeightProperty, null);
            box.BeginAnimation(MaxHeightProperty, null);
            box.MinHeight = to;
            box.MaxHeight = max;
            ShowLatest();
        };
        box.BeginAnimation(MinHeightProperty, aMin);
        box.BeginAnimation(MaxHeightProperty, aMax);
        ShowLatest();
    }

    // Mac-style overlay scrollbar: no arrows, no track, a thin rounded thumb.
    Style ThinScrollBar()
    {
        var c = t.Dark ? "#59FFFFFF" : "#47000000";
        var xaml =
            "<Style TargetType='ScrollBar' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Setter Property='Width' Value='8'/><Setter Property='MinWidth' Value='8'/>" +
            "<Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ScrollBar'>" +
            "<Track Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True'>" +
            "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            "<Border CornerRadius='3' Width='6' Margin='1,2,1,2' Background='" + c + "'/>" +
            "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
            "</Track></ControlTemplate></Setter.Value></Setter></Style>";
        return (Style)XamlReader.Parse(xaml);
    }

    void ShowLatest()
    {
        box.UpdateLayout();
        box.ScrollToEnd();
    }

    // ---------- show / hide ----------

    void ShowBox()
    {
        if (hiding) { BeginAnimation(OpacityProperty, null); hiding = false; }
        if (clearPending) { box.Clear(); clearPending = false; }
        RefreshTheme();
        SetMode(NeededSetup());
        if (!positioned)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width + ShadowPad - 24;
            Top = wa.Top + 84 - ShadowPad;
            positioned = true; // afterwards keep wherever the user dragged it
        }
        Opacity = 0;
        Show();
        Activate();
        SetForegroundWindow(hwnd);
        FocusInput();

        // One authored moment: the panel settles in from just above, quick exponential ease-out.
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var dur = TimeSpan.FromMilliseconds(220);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        var st = (ScaleTransform)panel.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
    }

    bool clearPending; // a just-saved note stays visible while the panel fades, then is cleared

    // Exit mirrors the entrance: a quick fade while the panel eases back to 96%. Short enough
    // (130ms) that it reads as a response, not a wait.
    void HideBox()
    {
        if (!IsVisible || hiding) return;
        hiding = true;
        var dur = TimeSpan.FromMilliseconds(130);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var a = new DoubleAnimation(Opacity, 0, dur) { EasingFunction = ease };
        a.Completed += (s, e) =>
        {
            if (!hiding) return; // reopened mid-fade
            hiding = false;
            Hide();
            BeginAnimation(OpacityProperty, null);
            var st2 = (ScaleTransform)panel.RenderTransform;
            st2.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (clearPending) { box.Clear(); clearPending = false; }
            HookKeys(false);
            keyBox.Clear();
            linkBox.Clear();
            ClearStatus();
        };
        BeginAnimation(OpacityProperty, a);
        var st = (ScaleTransform)panel.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, dur) { EasingFunction = ease });
    }

    void RefreshTheme()
    {
        var now = Theme.Current();
        if (now.Dark == t.Dark) return;
        t = now;
        var text = box.Text;
        Content = BuildUi();
        box.Text = text;
    }

    void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; HideBox(); return; }
        if (setup == "hotkey")
        {
            e.Handled = true;
            if (llHook == IntPtr.Zero) PickHotkey(e.Key == Key.System ? e.SystemKey : e.Key); // else the hook has it
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Down || e.Key == Key.Up) && !keyMode)
        {
            e.Handled = true;
            Resize(e.Key == Key.Down);
            return;
        }
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        e.Handled = true;
        if (keyMode) { SaveKey(); return; }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int at = box.CaretIndex;
            box.SelectedText = Environment.NewLine;
            box.CaretIndex = at + Environment.NewLine.Length;
            box.SelectionLength = 0;
            return;
        }
        Save();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == HOTKEY_ID)
        {
            handled = true;
            if (setup == "hotkey" && IsVisible && !hiding)
                SetStatus("That's already your shortcut", t.Secondary, false); // pressed it while picking a new one
            else if (IsVisible && IsActive && !hiding) HideBox(); else ShowBox();
        }
        return IntPtr.Zero;
    }

    // ---------- shortcut ----------

    static string HotkeyText(uint mods, uint vk)
    {
        var s = "";
        if ((mods & MOD_WIN) != 0) s += "Win+";
        if ((mods & MOD_CONTROL) != 0) s += "Ctrl+";
        if ((mods & MOD_ALT) != 0) s += "Alt+";
        if ((mods & MOD_SHIFT) != 0) s += "Shift+";
        return s + KeyName(vk);
    }

    static string KeyName(uint vk)
    {
        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
        switch (vk)
        {
            case 0x20: return "Space";
            case 0xBA: return ";"; case 0xBB: return "="; case 0xBC: return ","; case 0xBD: return "-";
            case 0xBE: return "."; case 0xBF: return "/"; case 0xC0: return "`"; case 0xDB: return "[";
            case 0xDC: return "\\"; case 0xDD: return "]"; case 0xDE: return "'";
        }
        var k = KeyInterop.KeyFromVirtualKey((int)vk).ToString();
        if (k.StartsWith("NumPad")) return "Num " + k.Substring(6);
        return k == "Next" ? "PageDown" : k == "Prior" ? "PageUp" : k == "Return" ? "Enter" : k == "Back" ? "Backspace" : k;
    }

    // While "Change shortcut" is up and the panel is in front, a low-level keyboard hook sees
    // each combo before Windows hands it to whichever app registered it. Without this, a combo
    // another app owns never reaches the panel: that app's action runs and Zipcap can't say
    // "taken". The hook only exists in this mode and ignores keys meant for other windows.
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr w, IntPtr l);
    LowLevelKeyboardProc hookProc; // kept in a field so the GC can't collect it while hooked
    IntPtr llHook;

    void HookKeys(bool on)
    {
        if (on && llHook == IntPtr.Zero)
        {
            hookProc = OnLowLevelKey;
            llHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, hookProc, GetModuleHandle(null), 0);
        }
        else if (!on && llHook != IntPtr.Zero) { UnhookWindowsHookEx(llHook); llHook = IntPtr.Zero; }
    }

    static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    IntPtr OnLowLevelKey(int code, IntPtr w, IntPtr l)
    {
        int msg = w.ToInt32();
        if (code >= 0 && (msg == 0x100 || msg == 0x104) && GetForegroundWindow() == hwnd) // WM_KEYDOWN / WM_SYSKEYDOWN
        {
            uint vk = (uint)Marshal.ReadInt32(l);
            bool modifier = (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
            if (!modifier && vk != 0x1B && vk != 0xE8) // Esc goes through to close; 0xE8 is our own dummy key
            {
                uint mods = 0;
                bool win = Down(0x5B) || Down(0x5C);
                if (win) mods |= MOD_WIN;
                if (Down(0x11)) mods |= MOD_CONTROL;
                if (Down(0x12)) mods |= MOD_ALT;
                if (Down(0x10)) mods |= MOD_SHIFT;
                // Swallowing the key would leave Windows seeing a bare Win press, so the Start
                // menu would open on release. A harmless unassigned key in between prevents it.
                if (win) keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                Dispatcher.BeginInvoke(new Action(() => PickHotkey(mods, vk)));
                return (IntPtr)1; // swallowed: the app that owns this combo doesn't fire
            }
        }
        return CallNextHookEx(llHook, code, w, l);
    }

    // Fallback when the hook couldn't be set: read the key from WPF instead.
    void PickHotkey(Key key)
    {
        if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin || key == Key.None) return;
        var m = Keyboard.Modifiers;
        uint mods = 0;
        if ((m & ModifierKeys.Windows) != 0 || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= MOD_WIN;
        if ((m & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0) mods |= MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
        PickHotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    // A combo was pressed on "Change shortcut". Delete/Backspace alone goes back to the default.
    void PickHotkey(uint mods, uint vk)
    {
        if (setup != "hotkey") return;
        if (mods == 0 && (vk == 0x2E || vk == 0x08)) { mods = Store.DefaultMods; vk = Store.DefaultVk; }
        else if ((mods & (MOD_WIN | MOD_CONTROL | MOD_ALT)) == 0)
        {
            SetStatus("Hold Ctrl, Alt or Win too", t.Error, false);
            return;
        }
        if (mods == hkMods && vk == hkVk) { SetStatus("That's already your shortcut", t.Secondary, false); return; }

        // Swap: let go of the old one, try the new one, and put the old one back if it's taken.
        UnregisterHotKey(hwnd, HOTKEY_ID);
        if (!RegisterHotKey(hwnd, HOTKEY_ID, mods | MOD_NOREPEAT, vk))
        {
            RegisterHotKey(hwnd, HOTKEY_ID, hkMods | MOD_NOREPEAT, hkVk);
            SetStatus(HotkeyText(mods, vk) + " is taken. Try another.", t.Error, false);
            return;
        }
        hkMods = mods; hkVk = vk;
        try { Store.WriteHotkey(mods, vk); }
        catch { SetStatus("Works now, but couldn't save it for next time", t.Error, false); UpdateTray(); return; }
        UpdateTray();
        SetHelp("hotkey");
        ShortcutSaved();
    }

    async void ShortcutSaved()
    {
        SetStatus("Shortcut: " + HotkeyText(hkMods, hkVk), t.Success, true);
        await Task.Delay(900);
        if (setup == "hotkey") HideBox();
    }

    // ---------- saving ----------

    // First thing the panel needs before it can save notes: a Notion key, then a page.
    static string NeededSetup()
    {
        if (!Store.Has(Store.NotionKey)) return "notion";
        if (Store.PageId == null) return "page";
        return null;
    }

    bool checking; // a key/page is being checked with the service; ignore extra Enters

    async void SaveKey()
    {
        if (checking) return;
        if (setup == "page") { SavePage(); return; }
        var k = keyBox.Password.Trim();
        if (k.Length == 0) return;
        bool todoist = setup == "todoist";
        if (todoist && !System.Text.RegularExpressions.Regex.IsMatch(k, "^[0-9a-fA-F]{40}$"))
        {
            SetStatus("A Todoist token is 40 letters/numbers", t.Error, false);
            return;
        }
        if (!todoist && !(k.StartsWith("ntn_") || k.StartsWith("secret_")))
        {
            SetStatus("That doesn't look like a Notion token (ntn_…)", t.Error, false);
            return;
        }
        checking = true;
        SetStatus("Checking…", t.Secondary, false);
        var r = await Task.Run(() => todoist ? CheckTodoist(k) : CheckNotion(k));
        checking = false;
        if (r == "KEY")
        {
            SetStatus(todoist ? "Todoist didn't accept that token" : "Notion didn't accept that token", t.Error, false);
            return;
        }
        // "OK", or offline ("RETRY"): save it anyway; a bad key gets caught when sending.
        try { Store.Write(todoist ? Store.TodoistToken : Store.NotionKey, k); }
        catch { SetStatus("Couldn't save the key on this PC", t.Error, false); return; }
        keyBox.Clear();
        if (!todoist && Store.PageId == null)
        {
            SetMode("page");
            SetStatus("Connected. Now pick the page.", t.Success, true);
            linkBox.Focus();
            return;
        }
        Connected();
    }

    async void SavePage()
    {
        var id = Store.ParsePageId(linkBox.Text);
        if (id == null) { SetStatus("That isn't a Notion page link", t.Error, false); return; }
        checking = true;
        SetStatus("Checking…", t.Secondary, false);
        var r = await Task.Run(() => CheckPage(id));
        checking = false;
        if (r == "KEY") { SetMode("notion"); SetStatus("Notion token rejected. Paste it again.", t.Error, false); keyBox.Focus(); return; }
        if (r == "PAGE") { SetStatus("Not shared yet: add it in Content access", t.Error, false); return; }
        try { Store.PageId = id; }
        catch { SetStatus("Couldn't save the page on this PC", t.Error, false); return; }
        warnedPage = false;
        linkBox.Clear();
        Connected();
    }

    async void Connected()
    {
        SetMode(null);
        SetStatus("Connected", t.Success, true);
        Flush(); // send anything that was waiting on the key
        box.Focus();
        if (box.Text.Trim().Length == 0)
        {
            await Task.Delay(700); // nothing left to type: show the tick, then get out of the way
            if (!keyMode && box.Text.Trim().Length == 0) HideBox();
        }
    }

    // Quick read-only checks so a typo is caught while you're still in the box.
    // "OK", "KEY" (rejected), "PAGE" (not found / not shared), or "RETRY" (offline etc.).
    static string Check(HttpRequestMessage req)
    {
        try
        {
            var res = http.SendAsync(req).Result;
            int code = (int)res.StatusCode;
            if (res.IsSuccessStatusCode) return "OK";
            if (code == 401) return "KEY";
            if (code == 403 || code == 404) return "PAGE";
            return "RETRY";
        }
        catch { return "RETRY"; }
    }

    static HttpRequestMessage NotionGet(string path, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Headers.Add("Notion-Version", "2022-06-28");
        return req;
    }

    static string CheckNotion(string key) { return Check(NotionGet("/v1/users/me", key)); }

    static string CheckPage(string id)
    {
        var key = Store.Read(Store.NotionKey);
        if (key == null) return "KEY";
        return Check(NotionGet("/v1/pages/" + id, key));
    }

    static string CheckTodoist(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, TodoistBase + "/api/v1/projects?limit=1");
        req.Headers.Add("Authorization", "Bearer " + token);
        var r = Check(req);
        return r == "PAGE" ? "KEY" : r; // Todoist answers 403 for a bad token
    }

    // ---------- outbox ----------
    // Every note is written to disk FIRST (one file per note, named by time so order is kept),
    // then sent. A file is deleted only after Notion accepts it, so nothing is lost when
    // offline, when Notion is down, or if the PC shuts down mid-send.
    static readonly string PendingDir = Store.PendingDir;
    Task outbox = Task.FromResult(0);          // sends run one after another
    System.Windows.Threading.DispatcherTimer retry;
    bool warnedPage;

    // Enter: the box fades out right away; the note is safe on disk and sends in the background.
    void Save()
    {
        var text = box.Text.Trim();
        // First task ever and no Todoist token yet: keep the box open and ask for it now,
        // while the box still has focus. The task is already safe on disk.
        if (IsTask(text) && !Store.Has(Store.TodoistToken))
        {
            try { Enqueue(text); } catch { return; }
            box.Clear();
            UpdateTray();
            SetMode("todoist");
            SetStatus("Connect Todoist once. Your task is saved.", t.Secondary, false);
            keyBox.Focus();
            return;
        }
        clearPending = true; // cleared once the fade-out finishes
        HideBox();
        if (text.Length == 0) return;
        try { Enqueue(text); }
        catch (Exception)
        {
            // Disk write failed (very unlikely): keep the note in the box instead.
            clearPending = false;
            ShowBox();
            box.Text = text;
            SetStatus("Couldn't save the note on this PC.", t.Error, false);
            return;
        }
        UpdateTray();
        Flush();
    }

    static void Enqueue(string text)
    {
        Directory.CreateDirectory(PendingDir);
        var name = DateTime.UtcNow.Ticks.ToString("D20") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var tmp = System.IO.Path.Combine(PendingDir, name + ".tmp");
        File.WriteAllText(tmp, text, Encoding.UTF8);
        File.Move(tmp, System.IO.Path.Combine(PendingDir, name + ".txt")); // appears complete or not at all
    }

    static string[] PendingFiles()
    {
        if (!Directory.Exists(PendingDir)) return new string[0];
        var f = Directory.GetFiles(PendingDir, "*.txt");
        Array.Sort(f, StringComparer.Ordinal);
        return f;
    }

    void Flush()
    {
        outbox = outbox.ContinueWith(_ =>
        {
            // Notion and Todoist are separate lanes: a problem with one (no token, offline,
            // page not shared) pauses only that lane; the other keeps sending. Order is kept per lane.
            string stopNotion = null, stopTodoist = null;
            int bad = 0;
            foreach (var f in PendingFiles())
            {
                string text;
                try { text = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                bool task = IsTask(text);
                if (task ? stopTodoist != null : stopNotion != null) continue; // lane paused
                var r = text.Trim().Length == 0 ? null : task ? AddTask(text) : Append(text);
                if (r == null) { try { File.Delete(f); } catch { } continue; }
                if (r == "BAD")
                {
                    // The service will never accept this one; park it so it doesn't block the rest.
                    try { File.Move(f, System.IO.Path.ChangeExtension(f, ".failed")); } catch { }
                    bad++;
                    continue;
                }
                if (task) stopTodoist = r; else stopNotion = r;
            }
            Dispatcher.BeginInvoke(new Action(() => AfterFlush(stopNotion, stopTodoist, bad)));
        });
    }

    void AfterFlush(string stopNotion, string stopTodoist, int bad)
    {
        UpdateTray();
        if (bad > 0)
            Notify(bad + " note(s) were refused by Notion/Todoist. They're kept in %APPDATA%\\Zipcap\\pending (.failed files).");
        if (stopNotion == null && stopTodoist == null) { retry.Stop(); warnedPage = false; return; }

        // Something only you can fix (key missing/rejected, no page chosen): ask in the box, Notion first.
        string ask = stopNotion == "KEY" ? "notion" : stopNotion == "NOPAGE" ? "page"
                   : (stopTodoist == "TKEY" || stopTodoist == "TNONE") ? "todoist" : null;
        if (ask != null && !(IsVisible && keyMode))
        {
            StartSetup(ask);
            SetStatus(ask == "page" ? "Pick a page for your notes. They're safe." :
                      ask == "todoist" && stopTodoist == "TNONE" ? "Connect Todoist once to send tasks." :
                      ask == "todoist" ? "Todoist token rejected. Notes are safe." :
                                         "Notion token rejected. Notes are safe.", t.Error, false);
        }
        if (stopNotion == "PAGE" && !warnedPage)
        {
            warnedPage = true;
            Notify("Your Zipcap page isn't shared with your Notion connection (Developer tools → your connection → Content access). " +
                   "Your notes are safe and will send once it's fixed.");
        }
        // Keep retrying while any lane is waiting on the network or Notion (keys wait for you).
        bool transient = stopNotion == "PAGE" || stopNotion == "RETRY" || stopTodoist == "RETRY";
        if (transient) retry.Start(); else retry.Stop();
    }

    // "t: submit lab fri 8pm #Study p1" -> Todoist task via Quick Add, which understands the same
    // shorthand as the Todoist app (dates, #project, @label, p1-p4). Extra lines become the description.
    // Returns null on success, "TNONE"/"TKEY" for a missing/bad token, "BAD", or "RETRY".
    static string AddTask(string text)
    {
        try
        {
            var token = Store.Read(Store.TodoistToken);
            if (token == null) return "TNONE";
            var lines = new List<string>();
            foreach (var raw in TaskPrefix.Replace(text, "", 1).Replace("\r\n", "\n").Split('\n'))
                if (raw.Trim().Length > 0) lines.Add(raw.Trim());
            if (lines.Count == 0) return null;
            var quick = lines[0];
            if (lines.Count > 1) quick += " // " + string.Join(" · ", lines.GetRange(1, lines.Count - 1));
            var req = new HttpRequestMessage(HttpMethod.Post, TodoistBase + "/api/v1/tasks/quick");
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Content = new StringContent("{\"text\":" + Json(quick) + ",\"auto_reminder\":true}", Encoding.UTF8, "application/json");
            var res = http.SendAsync(req).Result;
            if (res.IsSuccessStatusCode) return null;
            int code = (int)res.StatusCode;
            if (code == 401 || code == 403) return "TKEY";
            if (code == 400) return "BAD";
            return "RETRY";
        }
        catch (Exception) { return "RETRY"; }
    }

    // Returns null on success, or why it failed:
    // "KEY" bad/missing key, "NOPAGE" no page chosen yet, "PAGE" page not shared with the connection,
    // "RETRY" offline / timeout / Notion busy, "BAD" Notion refused this note's content.
    static string Append(string text)
    {
        try
        {
            var token = Store.Read(Store.NotionKey);
            if (token == null) return "KEY";
            var pageId = Store.PageId;
            if (pageId == null) return "NOPAGE";
            var blocks = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                var parts = new List<string>(); // Notion allows max 2000 chars per text piece
                for (int i = 0; i < line.Length; i += 2000)
                    parts.Add("{\"type\":\"text\",\"text\":{\"content\":" + Json(line.Substring(i, Math.Min(2000, line.Length - i))) + "}}");
                blocks.Add("{\"object\":\"block\",\"type\":\"paragraph\",\"paragraph\":{\"rich_text\":[" + string.Join(",", parts) + "]}}");
            }
            for (int start = 0; start < blocks.Count; start += 100) // max 100 blocks per request
            {
                var chunk = blocks.GetRange(start, Math.Min(100, blocks.Count - start));
                var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                    ApiBase + "/v1/blocks/" + pageId + "/children");
                req.Headers.Add("Authorization", "Bearer " + token);
                req.Headers.Add("Notion-Version", "2022-06-28");
                req.Content = new StringContent("{\"children\":[" + string.Join(",", chunk) + "]}", Encoding.UTF8, "application/json");
                var res = http.SendAsync(req).Result;
                if (!res.IsSuccessStatusCode)
                {
                    int code = (int)res.StatusCode;
                    if (code == 401) return "KEY";
                    if (code == 403 || code == 404) return "PAGE";
                    if (code == 400 || code == 413) return "BAD";
                    return "RETRY"; // 429 rate limit, 5xx Notion trouble
                }
            }
            return null;
        }
        catch (Exception)
        {
            return "RETRY"; // no internet, DNS, timeout
        }
    }

    static string Json(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c); else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    void OpenPage()
    {
        var id = Store.PageId;
        if (id == null) { StartSetup(NeededSetup() ?? "page"); return; }
        try { System.Diagnostics.Process.Start("https://www.notion.so/" + id); } catch { }
        HideBox();
    }

    // ---------- tray ----------

    void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        captureItem = new Forms.ToolStripMenuItem("", null, (s, e) => ShowBox());
        menu.Items.Add(captureItem);
        menu.Items.Add("Open Zipcap page in Notion", null, (s, e) => OpenPage());
        menu.Items.Add("Change Notion page…", null, (s, e) => StartSetup(Store.Has(Store.NotionKey) ? "page" : "notion"));
        menu.Items.Add("Change Notion token…", null, (s, e) => StartSetup("notion"));
        menu.Items.Add("Change Todoist token…", null, (s, e) => StartSetup("todoist"));
        menu.Items.Add("Change shortcut…", null, (s, e) => StartSetup("hotkey"));
        sendNow = new Forms.ToolStripMenuItem("Send waiting notes now", null, (s, e) => Flush()) { Visible = false };
        menu.Items.Add(sendNow);
        menu.Items.Add(new Forms.ToolStripSeparator());
        updateItem = new Forms.ToolStripMenuItem("", null, (s, e) => InstallUpdate()) { Visible = false };
        menu.Items.Add(updateItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Zipcap " + Program.Version + " (beta)") { Enabled = false });
        menu.Items.Add("Quit", null, (s, e) => Quit());
        tray = new Forms.NotifyIcon
        {
            Icon = iconIdle, ContextMenuStrip = menu, Visible = true
        };
        UpdateTray();
        tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowBox(); };
        tray.BalloonTipClicked += (s, e) => { if (updateUrl != null && updateItem.Enabled) InstallUpdate(); };

        // Look for a new release shortly after start, then once a day.
        var daily = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        daily.Tick += (s, e) => CheckForUpdate();
        daily.Start();
        var soon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        soon.Tick += (s, e) => { soon.Stop(); CheckForUpdate(); };
        soon.Start();
    }

    // ---------- updates ----------
    // Asks GitHub for the latest release of this repo. If it's newer, the tray offers it.
    // Only an installer published as an asset of this exact repo is ever downloaded or run.
    Forms.ToolStripMenuItem updateItem;
    string updateUrl, updateVersion;
    bool updateAnnounced;
    static readonly string AssetPrefix = "https://github.com/" + Program.Repo + "/releases/download/";

    void CheckForUpdate()
    {
        if (updateUrl != null) return;
        Task.Run(() =>
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Program.Repo + "/releases/latest");
                req.Headers.Add("User-Agent", "Zipcap/" + Program.Version);
                req.Headers.Add("Accept", "application/vnd.github+json");
                var res = http.SendAsync(req).Result;
                if (!res.IsSuccessStatusCode) return;
                var json = res.Content.ReadAsStringAsync().Result;
                var tag = System.Text.RegularExpressions.Regex.Match(json, @"""tag_name""\s*:\s*""v?([0-9]+(\.[0-9]+){1,3})""");
                var url = System.Text.RegularExpressions.Regex.Match(json,
                    @"""browser_download_url""\s*:\s*""(" + System.Text.RegularExpressions.Regex.Escape(AssetPrefix) + @"[^""/]+/ZipcapSetup\.exe)""");
                if (!tag.Success || !url.Success) return;
                if (new Version(tag.Groups[1].Value) <= new Version(Program.Version)) return;
                var v = tag.Groups[1].Value; var u = url.Groups[1].Value;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    updateVersion = v; updateUrl = u;
                    updateItem.Text = "Update to Zipcap " + v;
                    updateItem.Visible = true;
                    if (!updateAnnounced)
                    {
                        updateAnnounced = true;
                        tray.ShowBalloonTip(8000, "Zipcap " + v + " is ready", "Click here (or the tray menu) to update. Takes a few seconds.", Forms.ToolTipIcon.Info);
                    }
                }));
            }
            catch { } // offline or GitHub down: try again tomorrow
        });
    }

    void InstallUpdate()
    {
        if (updateUrl == null || !updateUrl.StartsWith(AssetPrefix)) return;
        updateItem.Enabled = false;
        updateItem.Text = "Downloading update…";
        var url = updateUrl; var v = updateVersion;
        Task.Run(() =>
        {
            try
            {
                var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZipcapSetup-" + v + ".exe");
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "Zipcap/" + Program.Version);
                var res = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }.SendAsync(req).Result;
                res.EnsureSuccessStatusCode();
                File.WriteAllBytes(file, res.Content.ReadAsByteArrayAsync().Result);
                System.Diagnostics.Process.Start(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART");
                Dispatcher.BeginInvoke(new Action(Quit)); // the installer replaces the exe and starts it again
            }
            catch
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    updateItem.Enabled = true;
                    updateItem.Text = "Update to Zipcap " + v + " (retry)";
                    Notify("Couldn't download the update. Check your connection and try again from the tray.");
                }));
            }
        });
    }

    static readonly Drawing.Icon iconIdle = MakeIcon(false), iconWaiting = MakeIcon(true);
    Forms.ToolStripMenuItem sendNow;

    // Tray shows an orange dot (and a count in the tooltip) while notes are waiting to send.
    void UpdateTray()
    {
        if (tray == null) return;
        int n = PendingFiles().Length;
        tray.Icon = n > 0 ? iconWaiting : iconIdle;
        tray.Text = n > 0 ? "Zipcap: " + n + (n == 1 ? " note" : " notes") + " waiting to send"
                          : "Zipcap (" + HotkeyText(hkMods, hkVk) + ")";
        sendNow.Visible = n > 0;
        captureItem.Text = "Capture   " + HotkeyText(hkMods, hkVk);
    }

    void Notify(string msg)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (tray != null) tray.ShowBalloonTip(5000, "Zipcap", msg, Forms.ToolTipIcon.Warning);
        }));
    }

    void Quit()
    {
        try { outbox.Wait(3000); } catch { } // give an in-flight send a moment; unsent notes stay on disk
        HookKeys(false);
        UnregisterHotKey(hwnd, HOTKEY_ID);
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    // Drawn tray icon: a small rounded note with two lines, in the accent blue.
    static Drawing.Icon MakeIcon(bool waiting)
    {
        using (var bmp = new Drawing.Bitmap(32, 32))
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using (var path = new Drawing.Drawing2D.GraphicsPath())
            {
                int r = 9; var rc = new Drawing.Rectangle(2, 2, 28, 28);
                path.AddArc(rc.X, rc.Y, r, r, 180, 90);
                path.AddArc(rc.Right - r, rc.Y, r, r, 270, 90);
                path.AddArc(rc.Right - r, rc.Bottom - r, r, r, 0, 90);
                path.AddArc(rc.X, rc.Bottom - r, r, r, 90, 90);
                path.CloseFigure();
                using (var b = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0A, 0x84, 0xFF))) g.FillPath(b, path);
            }
            using (var pen = new Drawing.Pen(Drawing.Color.White, 3f))
            {
                pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pen, 9, 12, 23, 12);
                g.DrawLine(pen, 9, 19, 18, 19);
            }
            if (waiting)
            {
                // Orange dot, bottom-right, with a ring so it reads against the blue.
                using (var ring = new Drawing.SolidBrush(Drawing.Color.White)) g.FillEllipse(ring, 17, 17, 15, 15);
                using (var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0x9F, 0x0A))) g.FillEllipse(dot, 19, 19, 11, 11);
            }
            var h = bmp.GetHicon();
            var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }
}
