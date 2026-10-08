// WideAgent 창의 테마와 직접 그리는 컨트롤.
//
// 구성과 컨트롤 모양은 BluePage(Microsoft 365 Web Launcher)의 UI를 따랐다. BluePage는 .NET 8과
// 최신 C# 문법으로 되어 있고 WideAgent는 Windows 기본 csc(C# 5)로 빌드하므로, 코드를 그대로
// 가져오지 않고 같은 동작을 C# 5 문법으로 다시 썼다.
//
// 색은 WideAgent 아이콘에서 뽑았다. 옅은 아쿠아(창 유리) = 배경 톤, 청록(물결) = 보조 강조색,
// 진한 틸(테두리) = 주 강조색.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WideAgent
{
    enum ThemePreference
    {
        System,
        Light,
        Dark
    }

    class ThemePalette
    {
        public Color Background, CardBackground, Border, TextPrimary, TextSecondary;
        public Color ButtonBackground, ButtonBorder, ButtonHover;
        public Color Accent, Link, Sidebar, AccentSoft, AccentSoftText, OnAccent;
        public Color SuccessSoft, SuccessSoftText, WarningSoft, WarningSoftText, DangerSoft, DangerSoftText, NeutralSoft;
        public Color Danger;
    }

    static class AppTheme
    {
        static readonly ThemePalette LightPalette = new ThemePalette
        {
            Background = Color.FromArgb(243, 250, 249),
            CardBackground = Color.FromArgb(255, 255, 255),
            Border = Color.FromArgb(207, 232, 228),
            TextPrimary = Color.FromArgb(28, 56, 53),
            TextSecondary = Color.FromArgb(94, 122, 118),
            ButtonBackground = Color.FromArgb(255, 255, 255),
            ButtonBorder = Color.FromArgb(190, 222, 217),
            ButtonHover = Color.FromArgb(230, 245, 242),
            Accent = Color.FromArgb(17, 122, 113),
            Link = Color.FromArgb(17, 112, 104),
            Sidebar = Color.FromArgb(226, 245, 242),
            AccentSoft = Color.FromArgb(200, 240, 234),
            AccentSoftText = Color.FromArgb(12, 102, 94),
            OnAccent = Color.FromArgb(255, 255, 255),
            SuccessSoft = Color.FromArgb(214, 241, 226),
            SuccessSoftText = Color.FromArgb(28, 110, 70),
            WarningSoft = Color.FromArgb(252, 236, 214),
            WarningSoftText = Color.FromArgb(160, 90, 30),
            DangerSoft = Color.FromArgb(252, 226, 222),
            DangerSoftText = Color.FromArgb(180, 60, 50),
            NeutralSoft = Color.FromArgb(232, 241, 239),
            Danger = Color.FromArgb(192, 64, 52)
        };

        static readonly ThemePalette DarkPalette = new ThemePalette
        {
            Background = Color.FromArgb(18, 34, 33),
            CardBackground = Color.FromArgb(26, 46, 44),
            Border = Color.FromArgb(42, 68, 65),
            TextPrimary = Color.FromArgb(226, 244, 240),
            TextSecondary = Color.FromArgb(146, 174, 170),
            ButtonBackground = Color.FromArgb(32, 55, 53),
            ButtonBorder = Color.FromArgb(55, 86, 82),
            ButtonHover = Color.FromArgb(40, 66, 63),
            Accent = Color.FromArgb(78, 217, 203),
            Link = Color.FromArgb(110, 226, 214),
            Sidebar = Color.FromArgb(14, 27, 26),
            AccentSoft = Color.FromArgb(28, 74, 69),
            AccentSoftText = Color.FromArgb(132, 232, 220),
            OnAccent = Color.FromArgb(8, 46, 42),
            SuccessSoft = Color.FromArgb(30, 66, 50),
            SuccessSoftText = Color.FromArgb(150, 222, 180),
            WarningSoft = Color.FromArgb(72, 54, 34),
            WarningSoftText = Color.FromArgb(240, 192, 140),
            DangerSoft = Color.FromArgb(74, 42, 40),
            DangerSoftText = Color.FromArgb(244, 162, 150),
            NeutralSoft = Color.FromArgb(36, 58, 56),
            Danger = Color.FromArgb(244, 140, 126)
        };

        public static ThemePreference Preference = ThemePreference.System;

        // 테마를 바꾸거나 Windows 테마가 바뀌면 열린 창들이 다시 칠한다.
        public static event Action Changed;

        public static bool IsDark
        {
            get
            {
                if (Preference == ThemePreference.Dark) return true;
                if (Preference == ThemePreference.Light) return false;
                return IsSystemDarkMode();
            }
        }

        public static ThemePalette Current
        {
            get { return IsDark ? DarkPalette : LightPalette; }
        }

        public static void SetPreference(ThemePreference p)
        {
            Preference = p;
            if (Changed != null) Changed();
        }

        public static void NotifySystemThemeChanged()
        {
            if (Preference == ThemePreference.System && Changed != null) Changed();
        }

        static bool IsSystemDarkMode()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // 타이틀바를 다크로 맞추고 Windows 11 둥근 모서리를 요청한다. 지원하지 않으면 조용히 넘어간다.
        public static void ApplyTitleBar(IntPtr handle, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                DwmSetWindowAttribute(handle, 20, ref v, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(handle, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            }
            catch { }
        }
    }

    static class UiDraw
    {
        static float scale;
        static string iconFamily;

        static float Scale
        {
            get
            {
                if (scale == 0)
                {
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; }
                    catch { scale = 1; }
                }
                return scale;
            }
        }

        // 96 DPI 기준 픽셀을 현재 DPI로 바꾼다. 글꼴은 알아서 커지므로 고정 크기에만 쓴다.
        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static Padding S(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        // Windows 11은 Segoe Fluent Icons, Windows 10은 Segoe MDL2 Assets. 코드 포인트는 같다.
        public static Font IconFont(float size)
        {
            if (iconFamily == null)
            {
                iconFamily = "Segoe MDL2 Assets";
                try
                {
                    using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                        foreach (FontFamily f in fonts.Families)
                            if (f.Name == "Segoe Fluent Icons") { iconFamily = f.Name; break; }
                }
                catch { }
            }
            return new Font(iconFamily, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static GraphicsPath RoundedRect(RectangleF b, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(b.Width, b.Height));
            if (d <= 0) { path.AddRectangle(b); return path; }
            var arc = new RectangleF(b.Location, new SizeF(d, d));
            path.AddArc(arc, 180, 90);
            arc.X = b.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = b.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = b.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color SurfaceBehind(Control c)
        {
            return c.Parent == null ? AppTheme.Current.Background : c.Parent.BackColor;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    // Segoe Fluent Icons / MDL2 Assets 코드 포인트.
    static class Glyphs
    {
        public const string Home = "";
        public const string List = "";
        public const string Settings = "";
        public const string Info = "";
        public const string Code = "";
        public const string History = "";
        public const string ChevronRight = "";
        public const string ChevronDown = "";
    }

    // 둥근 모서리 카드.
    class CardPanel : Panel
    {
        public int CornerRadius = UiDraw.S(14);

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Padding = UiDraw.S(18, 16, 18, 16);
            BackColor = AppTheme.Current.CardBackground;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            e.Graphics.Clear(UiDraw.SurfaceBehind(this));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = UiDraw.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), CornerRadius))
            using (var fill = new SolidBrush(t.CardBackground))
            using (var border = new Pen(t.Border))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    // 왼쪽 사이드 메뉴 항목. 선택되면 연한 강조색 배경과 강조색 글자로 그린다.
    class NavItem : Control
    {
        readonly string glyph;
        bool selected, hovered;

        public NavItem(string glyph, string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            this.glyph = glyph;
            Text = text;
            TabStop = true;
            Cursor = Cursors.Hand;
            Height = UiDraw.S(40);
            Margin = new Padding(0, 0, 0, UiDraw.S(4));
            AccessibleRole = AccessibleRole.PageTab;
            AccessibleName = text;
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            Graphics g = e.Graphics;
            Color surface = UiDraw.SurfaceBehind(this);
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (selected || hovered || (Focused && ShowFocusCues))
            {
                Color back = selected ? t.AccentSoft : UiDraw.Blend(surface, t.TextPrimary, 0.05f);
                using (GraphicsPath path = UiDraw.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), UiDraw.S(10)))
                using (var fill = new SolidBrush(back))
                    g.FillPath(fill, path);
            }

            Color color = selected ? t.AccentSoftText : t.TextSecondary;
            using (Font icon = UiDraw.IconFont(11f))
                TextRenderer.DrawText(g, glyph, icon, new Rectangle(UiDraw.S(12), 0, UiDraw.S(22), Height), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            using (var bold = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(g, Text, selected ? bold : Font, new Rectangle(UiDraw.S(44), 0, Width - UiDraw.S(48), Height),
                    selected ? t.AccentSoftText : t.TextPrimary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // 체크박스 대신 쓰는 켜기/끄기 스위치. CheckBox를 상속하므로 Checked, CheckedChanged를 그대로 쓴다.
    class ToggleSwitch : CheckBox
    {
        bool hovered;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Size = new Size(UiDraw.S(42), UiDraw.S(24));
            Cursor = Cursors.Hand;
            Margin = new Padding(0);
            Text = "";
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            Graphics g = e.Graphics;
            Color surface = UiDraw.SurfaceBehind(this);
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var track = new RectangleF(1, 1, Width - 3, Height - 3);
            Color trackColor = Checked ? t.Accent : t.ButtonBorder;
            if (hovered && Enabled) trackColor = UiDraw.Blend(trackColor, t.TextPrimary, 0.08f);
            if (!Enabled) trackColor = UiDraw.Blend(trackColor, surface, 0.55f);
            using (GraphicsPath path = UiDraw.RoundedRect(track, track.Height / 2))
            using (var fill = new SolidBrush(trackColor))
                g.FillPath(fill, path);

            float knob = track.Height - UiDraw.S(6);
            float x = Checked ? track.Right - knob - UiDraw.S(3) : track.Left + UiDraw.S(3);
            Color knobColor = Checked ? t.OnAccent : t.CardBackground;
            if (!Enabled) knobColor = UiDraw.Blend(knobColor, surface, 0.35f);
            using (var b = new SolidBrush(knobColor))
                g.FillEllipse(b, x, track.Top + UiDraw.S(3), knob, knob);

            if (Focused && ShowFocusCues)
            {
                using (var focus = new Pen(t.Accent, 1.5f))
                using (GraphicsPath path = UiDraw.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), (Height - 2) / 2f))
                    g.DrawPath(focus, path);
            }
        }
    }

    // 알약 모양 트랙 안에서 하나를 고르는 버튼 묶음(예: 시스템 / 라이트 / 다크).
    class SegmentedControl : Control
    {
        readonly string[] items;
        readonly List<RectangleF> segments = new List<RectangleF>();
        int selectedIndex = -1;
        int hoverIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public SegmentedControl(params string[] items)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            this.items = items;
            TabStop = true;
            Cursor = Cursors.Hand;
            Margin = new Padding(0);
            Recalculate();
        }

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value == selectedIndex || value < -1 || value >= items.Length) return;
                selectedIndex = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        // 이벤트 없이 표시만 바꾼다. 설정 값을 화면에 반영할 때 쓴다.
        public void SetSilently(int index)
        {
            if (index < -1 || index >= items.Length) return;
            selectedIndex = index;
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Recalculate(); }

        void Recalculate()
        {
            segments.Clear();
            int pad = UiDraw.S(3);
            float x = pad;
            int height = TextRenderer.MeasureText("가", Font).Height + UiDraw.S(12);
            foreach (string item in items)
            {
                int w = TextRenderer.MeasureText(item, Font, Size.Empty, TextFormatFlags.NoPadding).Width + UiDraw.S(26);
                segments.Add(new RectangleF(x, pad, w, height));
                x += w;
            }
            Size = new Size((int)x + pad, height + pad * 2);
            Invalidate();
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < segments.Count; i++)
                if (segments[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = HitTest(e.Location);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && selectedIndex > 0) SelectedIndex = selectedIndex - 1;
            if (e.KeyCode == Keys.Right && selectedIndex < items.Length - 1) SelectedIndex = selectedIndex + 1;
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            Graphics g = e.Graphics;
            Color surface = UiDraw.SurfaceBehind(this);
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color trackColor = UiDraw.Blend(surface, t.TextPrimary, AppTheme.IsDark ? 0.08f : 0.05f);
            using (GraphicsPath track = UiDraw.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            using (var fill = new SolidBrush(trackColor))
            {
                g.FillPath(fill, track);
                if (Focused)
                    using (var focus = new Pen(t.Accent, 1.2f)) g.DrawPath(focus, track);
            }

            for (int i = 0; i < items.Length; i++)
            {
                RectangleF seg = segments[i];
                bool sel = i == selectedIndex;
                if (sel || i == hoverIndex)
                {
                    using (GraphicsPath path = UiDraw.RoundedRect(seg, seg.Height / 2))
                    using (var fill = new SolidBrush(sel ? t.CardBackground : UiDraw.Blend(trackColor, t.TextPrimary, 0.05f)))
                    {
                        g.FillPath(fill, path);
                        if (sel) using (var border = new Pen(t.Border)) g.DrawPath(border, path);
                    }
                }
                TextRenderer.DrawText(g, items[i], Font, Rectangle.Round(seg), sel ? t.TextPrimary : t.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }

    enum BadgeKind { Neutral, Success, Warning, Danger, Accent }

    static class BadgeColors
    {
        public static void For(BadgeKind kind, out Color back, out Color fore)
        {
            ThemePalette t = AppTheme.Current;
            switch (kind)
            {
                case BadgeKind.Success: back = t.SuccessSoft; fore = t.SuccessSoftText; break;
                case BadgeKind.Warning: back = t.WarningSoft; fore = t.WarningSoftText; break;
                case BadgeKind.Danger: back = t.DangerSoft; fore = t.DangerSoftText; break;
                case BadgeKind.Accent: back = t.AccentSoft; fore = t.AccentSoftText; break;
                default: back = t.NeutralSoft; fore = t.TextSecondary; break;
            }
        }
    }

    // "넓음", "실행 안 함" 같은 상태를 알약 모양으로 보여 준다.
    class StatusBadge : Control
    {
        BadgeKind kind;

        public StatusBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            Margin = new Padding(0);
        }

        public void Set(BadgeKind k, string text)
        {
            kind = k;
            Text = text;
            Visible = !string.IsNullOrEmpty(text);
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Size s = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            Size = new Size(s.Width + UiDraw.S(20), s.Height + UiDraw.S(8));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(UiDraw.SurfaceBehind(this));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color back, fore;
            BadgeColors.For(kind, out back, out fore);
            using (GraphicsPath path = UiDraw.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (var fill = new SolidBrush(back))
                e.Graphics.FillPath(fill, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // 카드 왼쪽의 둥근 아이콘 타일. 아이콘 글리프나 글자 하나를 가운데에 그린다.
    class IconTile : Control
    {
        readonly bool iconFont;
        readonly BadgeKind kind;

        public IconTile(string glyphOrLetter, BadgeKind kind, bool useIconFont)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            iconFont = useIconFont;
            this.kind = kind;
            Text = glyphOrLetter;
            Size = new Size(UiDraw.S(40), UiDraw.S(40));
            Margin = new Padding(0);
            Font = useIconFont ? UiDraw.IconFont(13f) : new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(UiDraw.SurfaceBehind(this));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color back, fore;
            BadgeColors.For(kind, out back, out fore);
            using (GraphicsPath path = UiDraw.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), UiDraw.S(12)))
            using (var fill = new SolidBrush(back))
                e.Graphics.FillPath(fill, path);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            if (!iconFont) flags |= TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fore, flags);
        }
    }

    // 알약 모양 버튼. IsPrimary 는 화면의 주요 동작, IsDanger 는 되돌릴 수 없는 동작에 쓴다.
    class ModernButton : Button
    {
        bool hovered, pressed;
        public bool IsPrimary;
        public bool IsDanger;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Height = UiDraw.S(34);
            Padding = UiDraw.S(14, 0, 14, 0);
            MinimumSize = new Size(UiDraw.S(76), UiDraw.S(34));
            Margin = new Padding(UiDraw.S(8), 0, 0, 0);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color surface = Parent != null ? Parent.BackColor : t.Background;
            g.Clear(surface);

            Rectangle b = Rectangle.Inflate(ClientRectangle, -1, -1);
            Color back, text;
            if (IsPrimary)
            {
                back = pressed ? UiDraw.Blend(t.Accent, t.TextPrimary, 0.15f) : hovered ? UiDraw.Blend(t.Accent, t.CardBackground, 0.12f) : t.Accent;
                text = t.OnAccent;
            }
            else
            {
                back = pressed ? UiDraw.Blend(t.ButtonHover, t.TextPrimary, 0.08f) : hovered ? t.ButtonHover : t.ButtonBackground;
                text = IsDanger ? t.Danger : t.TextPrimary;
            }
            if (!Enabled)
            {
                back = UiDraw.Blend(back, surface, 0.5f);
                text = IsPrimary ? UiDraw.Blend(t.OnAccent, back, 0.3f) : t.TextSecondary;
            }

            using (GraphicsPath path = UiDraw.RoundedRect(b, Math.Max(1, b.Height / 2)))
            {
                using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
                if (!IsPrimary || Focused)
                    using (var border = new Pen(Focused ? t.Accent : t.ButtonBorder, Focused ? 1.5f : 1f)) g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, Text, Font, b, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // 테마 색으로 그리는 툴팁. 설정 설명을 화면에 늘어놓지 않고 마우스를 올렸을 때 보여 준다.
    class ThemedToolTip : ToolTip
    {
        const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left;
        readonly Font font = new Font("Segoe UI", 9f);

        public ThemedToolTip()
        {
            OwnerDraw = true;
            InitialDelay = 250;
            ReshowDelay = 100;
            AutoPopDelay = 20000;
            ShowAlways = true;
            Popup += delegate (object s, PopupEventArgs e)
            {
                string text = e.AssociatedControl == null ? "" : GetToolTip(e.AssociatedControl);
                Size size = TextRenderer.MeasureText(text, font, new Size(UiDraw.S(300), 0), Flags);
                e.ToolTipSize = new Size(size.Width + UiDraw.S(24), size.Height + UiDraw.S(16));
            };
            Draw += delegate (object s, DrawToolTipEventArgs e)
            {
                ThemePalette t = AppTheme.Current;
                using (var back = new SolidBrush(t.CardBackground)) e.Graphics.FillRectangle(back, e.Bounds);
                using (var border = new Pen(t.Border)) e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, Rectangle.Inflate(e.Bounds, -UiDraw.S(12), -UiDraw.S(8)), t.TextPrimary, Flags);
            };
        }
    }

    // 트레이 메뉴를 테마 색으로 그린다.
    static class ThemedMenu
    {
        public static void Apply(ToolStripDropDown menu)
        {
            ThemePalette t = AppTheme.Current;
            var r = new ToolStripProfessionalRenderer(new ColorTable(t));
            r.RoundedEdges = true;
            menu.Renderer = r;
            menu.BackColor = t.CardBackground;
            menu.ForeColor = t.TextPrimary;
            foreach (ToolStripItem item in menu.Items) Paint(item, t);
        }

        static void Paint(ToolStripItem item, ThemePalette t)
        {
            item.ForeColor = t.TextPrimary;
            var mi = item as ToolStripMenuItem;
            if (mi == null) return;
            mi.DropDown.BackColor = t.CardBackground;
            mi.DropDown.Renderer = new ToolStripProfessionalRenderer(new ColorTable(t));
            foreach (ToolStripItem child in mi.DropDownItems) Paint(child, t);
        }

        class ColorTable : ProfessionalColorTable
        {
            readonly ThemePalette t;
            public ColorTable(ThemePalette t) { this.t = t; }
            public override Color ToolStripDropDownBackground { get { return t.CardBackground; } }
            public override Color ImageMarginGradientBegin { get { return t.CardBackground; } }
            public override Color ImageMarginGradientMiddle { get { return t.CardBackground; } }
            public override Color ImageMarginGradientEnd { get { return t.CardBackground; } }
            public override Color MenuItemSelected { get { return t.ButtonHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return t.ButtonHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return t.ButtonHover; } }
            public override Color MenuItemBorder { get { return t.ButtonHover; } }
            public override Color MenuBorder { get { return t.Border; } }
            public override Color SeparatorDark { get { return t.Border; } }
            public override Color SeparatorLight { get { return t.Border; } }
            public override Color CheckBackground { get { return t.AccentSoft; } }
            public override Color CheckSelectedBackground { get { return t.AccentSoft; } }
            public override Color CheckPressedBackground { get { return t.AccentSoft; } }
        }
    }

    // 창 하나의 컨트롤 트리를 돌며 현재 테마 색을 입힌다.
    static class ThemeApplier
    {
        public const string SkipTag = "theme-skip";
        public const string SecondaryTag = "theme-secondary";
        public const string SidebarTag = "theme-sidebar";

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

        public static void Apply(Form form)
        {
            ThemePalette t = AppTheme.Current;
            form.BackColor = t.Background;
            form.ForeColor = t.TextPrimary;
            Walk(form.Controls, t);
            AppTheme.ApplyTitleBar(form.Handle, AppTheme.IsDark);
        }

        static void Walk(Control.ControlCollection controls, ThemePalette t)
        {
            foreach (Control c in controls)
            {
                if (c.Tag as string != SkipTag) One(c, t);
                if (c.HasChildren) Walk(c.Controls, t);
            }
        }

        static Color SurfaceOf(Control c, ThemePalette t)
        {
            for (Control p = c.Parent; p != null; p = p.Parent)
            {
                if (p is CardPanel) return t.CardBackground;
                if (p.Tag as string == SidebarTag) return t.Sidebar;
            }
            return t.Background;
        }

        static string ScrollTheme { get { return AppTheme.IsDark ? "DarkMode_Explorer" : "Explorer"; } }

        static void One(Control c, ThemePalette t)
        {
            if (c is CardPanel)
            {
                c.BackColor = t.CardBackground;
                c.ForeColor = t.TextPrimary;
                c.Invalidate();
            }
            else if (c is Panel && c.Tag as string == SidebarTag)
            {
                c.BackColor = t.Sidebar;
                c.ForeColor = t.TextPrimary;
                c.Invalidate();
            }
            else if (c is WhaleFooter)
            {
                c.BackColor = t.Sidebar;
                c.Invalidate();
            }
            else if (c is ToggleSwitch || c is StatusBadge || c is IconTile || c is SegmentedControl || c is NavItem || c is ModernButton)
            {
                c.Invalidate();
            }
            else if (c is DataGridView)
            {
                Grid((DataGridView)c, t);
            }
            else if (c is TextBox)
            {
                var tb = (TextBox)c;
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.BackColor = t.ButtonBackground;
                tb.ForeColor = t.TextPrimary;
            }
            else if (c is LinkLabel)
            {
                var link = (LinkLabel)c;
                link.LinkColor = t.Link;
                link.ActiveLinkColor = t.Accent;
                link.VisitedLinkColor = t.Link;
            }
            else if (c is Label)
            {
                c.ForeColor = c.Tag as string == SecondaryTag ? t.TextSecondary : t.TextPrimary;
            }
            else if (c is Panel)
            {
                c.BackColor = SurfaceOf(c, t);
                c.ForeColor = t.TextPrimary;
                if (((Panel)c).AutoScroll)
                    try { SetWindowTheme(c.Handle, ScrollTheme, null); } catch { }
            }
        }

        // 세로 구분선 없이 가로줄만 두고, 선택은 연한 강조색으로 표시한다.
        static void Grid(DataGridView g, ThemePalette t)
        {
            Color surface = SurfaceOf(g, t);
            g.BackgroundColor = surface;
            g.GridColor = t.Border;
            g.EnableHeadersVisualStyles = false;
            g.BorderStyle = BorderStyle.None;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

            Padding pad = UiDraw.S(8, 0, 8, 0);
            g.ColumnHeadersDefaultCellStyle.BackColor = surface;
            g.ColumnHeadersDefaultCellStyle.ForeColor = t.TextSecondary;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = surface;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = t.TextSecondary;
            g.ColumnHeadersDefaultCellStyle.Font = new Font(g.Font, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.Padding = UiDraw.S(8, 0, 2, 0);   // 오른쪽은 정렬 화살표 자리

            g.DefaultCellStyle.BackColor = surface;
            g.DefaultCellStyle.ForeColor = t.TextPrimary;
            g.DefaultCellStyle.SelectionBackColor = t.AccentSoft;
            g.DefaultCellStyle.SelectionForeColor = t.AccentSoftText;
            g.DefaultCellStyle.Padding = pad;
            g.RowsDefaultCellStyle.BackColor = surface;
            g.AlternatingRowsDefaultCellStyle.BackColor = surface;
            try { SetWindowTheme(g.Handle, ScrollTheme, null); } catch { }
        }
    }

    // 앱 아이콘. 저장소의 WideAgent.ico(여러 크기가 들어 있다)를 exe에 리소스로 넣어 두고 거기서 읽는다.
    // 실행 파일 경로에서 아이콘을 뽑으면 32px 하나만 나와 크게 쓰면 흐리고, 다른 프로세스가 이 어셈블리를
    // 불러 쓰면 그 프로세스의 아이콘이 나온다.
    static class AppIcon
    {
        static Stream Open()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("WideAgent.ico");
        }

        // size 에 가장 가까운 이미지를 고른다. 리소스가 없으면 exe 의 아이콘으로 물러난다.
        public static Icon Get(int size)
        {
            try
            {
                using (Stream s = Open())
                    if (s != null) return new Icon(s, size, size);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
            catch { return SystemIcons.Application; }
        }

        // ico 안에서 가장 큰 이미지를 비트맵으로 꺼낸다. 큰 크기는 PNG로 압축돼 들어 있는데,
        // .NET Framework 의 Icon.ToBitmap() 은 이것을 제대로 풀지 못해 깨진 그림이 나온다.
        // 그래서 ico 목차를 직접 읽어 PNG 바이트는 PNG로 연다.
        static Bitmap Largest()
        {
            try
            {
                byte[] data;
                using (Stream s = Open())
                {
                    if (s == null) return null;
                    data = new byte[s.Length];
                    s.Read(data, 0, data.Length);
                }
                int count = BitConverter.ToUInt16(data, 4);
                int best = -1, bestSize = 0;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + i * 16;
                    int w = data[e] == 0 ? 256 : data[e];
                    if (w > bestSize) { bestSize = w; best = e; }
                }
                if (best < 0) return null;
                int len = BitConverter.ToInt32(data, best + 8);
                int off = BitConverter.ToInt32(data, best + 12);
                bool png = data[off] == 0x89 && data[off + 1] == 0x50 && data[off + 2] == 0x4E && data[off + 3] == 0x47;
                if (png)
                    using (var ms = new MemoryStream(data, off, len))
                    using (Image img = Image.FromStream(ms))
                        return new Bitmap(img);
                using (Icon ic = Get(bestSize)) return ic.ToBitmap();
            }
            catch { return null; }
        }

        // 화면에 그릴 비트맵. 큰 이미지에서 고품질로 줄여 가장자리가 뭉개지지 않게 한다.
        public static Bitmap Bitmap(int size)
        {
            var bmp = new Bitmap(size, size);
            Bitmap src = Largest();
            if (src == null) using (Icon ic = Get(size)) src = ic.ToBitmap();
            using (src)
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }
    }

    // 둥근 끝의 얇은 진행 막대. 0~1 값을 받는다.
    class ThinBar : Control
    {
        double value;
        public Color Fill;

        public ThinBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Fill = AppTheme.Current.Accent;
        }

        public double Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            Graphics g = e.Graphics;
            Color surface = UiDraw.SurfaceBehind(this);
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = UiDraw.RoundedRect(track, track.Height / 2))
            using (var b = new SolidBrush(UiDraw.Blend(surface, t.TextPrimary, AppTheme.IsDark ? 0.12f : 0.08f)))
                g.FillPath(b, p);
            float w = (float)(track.Width * value);
            if (w < 1) return;
            using (GraphicsPath p = UiDraw.RoundedRect(new RectangleF(0, 0, Math.Max(w, track.Height), track.Height), track.Height / 2))
            using (var b = new SolidBrush(Fill))
                g.FillPath(b, p);
        }
    }

    // miniwhalelabs 고래 도형. BluePage 아이콘에서 색 영역별로 뽑은 윤곽을 그대로 쓴다.
    // 아이콘 폭을 150으로 맞춘 좌표이고, 아이콘 바닥이 y=0(위로 음수)이다.
    static class WhaleShape
    {
        public const float BaseWidth = 150f;

        // 몸통(아래는 바닥까지 채움)
        public const string Body = "M0.0,-30.8 L0.0,0.0 L150.0,0.0 L150.0,-21.1 L149.4,-21.1 L149.2,-21.7 L147.3,-23.0 L144.8,-23.0 L143.8,-25.9 L141.9,-28.1 L139.2,-28.4 L136.8,-26.8 L135.9,-24.9 L135.1,-20.3 L132.4,-16.8 L129.4,-15.7 L123.8,-16.0 L118.1,-18.3 L111.9,-21.6 L83.0,-41.0 L69.4,-47.6 L58.3,-51.3 L48.6,-52.9 L37.9,-53.2 L29.0,-52.2 L21.1,-50.2 L13.7,-47.0 L7.8,-43.0 L3.0,-37.1 L0.8,-31.0 Z";

        // 등을 따라 흐르는 밝은 선과 첫 번째(말린) 꼬리 테두리
        public const string Ribbon = "M3.0,-37.1 L4.8,-39.5 L9.4,-43.7 L14.1,-46.5 L17.8,-48.1 L22.4,-49.5 L28.6,-50.6 L35.1,-51.1 L41.9,-51.0 L48.6,-50.2 L55.7,-48.6 L66.5,-44.8 L77.5,-39.0 L100.6,-24.0 L105.9,-21.0 L114.0,-17.0 L121.0,-14.8 L126.3,-14.1 L131.3,-15.2 L132.9,-16.2 L134.9,-18.6 L136.0,-21.4 L136.7,-25.1 L137.9,-26.8 L139.5,-27.6 L141.0,-27.6 L142.2,-27.1 L143.5,-26.0 L142.5,-27.5 L141.0,-28.3 L139.2,-28.3 L138.1,-27.8 L136.3,-25.9 L135.6,-21.3 L134.8,-19.4 L133.2,-17.5 L131.4,-16.3 L129.4,-15.7 L123.8,-16.0 L118.1,-18.3 L111.9,-21.6 L87.3,-38.1 L76.3,-44.4 L68.6,-47.9 L61.1,-50.5 L53.3,-52.2 L46.7,-53.0 L37.9,-53.2 L33.3,-52.9 L26.0,-51.6 L18.9,-49.4 L13.7,-47.0 L8.7,-43.8 L5.9,-41.1 Z";

        // 두 번째 꼬리 조각. 위가 밝고 아래로 어두워지는 그라데이션으로 그린다.
        public const string Fin = "M149.4,-21.7 L148.7,-21.9 L148.6,-22.4 L148.1,-22.4 L147.5,-23.0 L145.2,-23.0 L142.4,-18.6 L141.6,-17.8 L141.6,-16.0 L140.3,-12.7 L137.6,-8.3 L134.0,-4.8 L129.5,-2.2 L125.2,-0.8 L125.2,0.0 L135.2,0.0 L135.4,-0.6 L138.9,-1.7 L143.2,-4.4 L146.5,-8.1 L148.9,-12.7 L149.4,-14.1 Z";
        public const float FinTop = -23.2f;
        public const float Height = 53.2f;
    }

    // 사이드바 아래쪽 정보 영역. 고래를 사이드바 색에 강조색을 조금 섞은 색으로 옅게 그리고,
    // 고래 아래는 몸통 색으로 바닥까지 채운다. 버전과 링크 글자는 몸통 안에 들어간다.
    // 안쪽 글자 컨트롤은 배경을 투명하게 둔다.
    class WhaleFooter : Panel
    {
        // 고래 바닥에서 컨트롤 바닥까지 몸통 색으로 채우는 높이
        public int BodyBelowWhale = UiDraw.S(62);

        public WhaleFooter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ThemePalette t = AppTheme.Current;
            bool dark = AppTheme.IsDark;
            Graphics g = e.Graphics;
            g.Clear(t.Sidebar);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color body = UiDraw.Blend(t.Sidebar, t.Accent, dark ? 0.14f : 0.10f);
            Color ribbon = UiDraw.Blend(t.Sidebar, t.Accent, dark ? 0.30f : 0.22f);
            Color finBottom = UiDraw.Blend(t.Sidebar, t.Accent, dark ? 0.20f : 0.14f);

            float scale = Width / WhaleShape.BaseWidth;
            int whaleBottom = Height - BodyBelowWhale;

            using (var fill = new SolidBrush(body))
                g.FillRectangle(fill, 0, whaleBottom, Width, Height - whaleBottom);

            GraphicsState state = g.Save();
            g.TranslateTransform(0, whaleBottom + 0.5f);
            g.ScaleTransform(scale, scale);
            using (GraphicsPath bodyPath = SvgPath.Parse(WhaleShape.Body))
            using (GraphicsPath ribbonPath = SvgPath.Parse(WhaleShape.Ribbon))
            using (GraphicsPath finPath = SvgPath.Parse(WhaleShape.Fin))
            using (var bodyBrush = new SolidBrush(body))
            using (var ribbonBrush = new SolidBrush(ribbon))
            using (var finBrush = new LinearGradientBrush(new PointF(0, WhaleShape.FinTop - 0.5f), new PointF(0, 0.5f), ribbon, finBottom))
            {
                g.FillPath(bodyBrush, bodyPath);
                g.FillPath(ribbonBrush, ribbonPath);
                g.FillPath(finBrush, finPath);
            }
            g.Restore(state);
        }
    }

    // "M x,y L x,y ... Z" 형태의 간단한 SVG 경로만 읽는다(WhaleShape 전용).
    static class SvgPath
    {
        static readonly Dictionary<string, PointF[][]> Cache = new Dictionary<string, PointF[][]>();

        public static GraphicsPath Parse(string data)
        {
            PointF[][] figures;
            if (!Cache.TryGetValue(data, out figures))
            {
                figures = ParseFigures(data);
                Cache[data] = figures;
            }
            var path = new GraphicsPath();
            foreach (PointF[] f in figures)
            {
                if (f.Length < 2) continue;
                path.StartFigure();
                path.AddLines(f);
            }
            if (data.TrimEnd().EndsWith("Z")) path.CloseAllFigures();
            return path;
        }

        static PointF[][] ParseFigures(string data)
        {
            var figures = new List<PointF[]>();
            var current = new List<PointF>();
            foreach (string token in data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "Z") continue;
                string text = token;
                if (text[0] == 'M')
                {
                    if (current.Count > 0) figures.Add(current.ToArray());
                    current = new List<PointF>();
                    text = text.Substring(1);
                }
                else if (text[0] == 'L') text = text.Substring(1);
                string[] parts = text.Split(',');
                current.Add(new PointF(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture)));
            }
            if (current.Count > 0) figures.Add(current.ToArray());
            return figures.ToArray();
        }
    }
}
