// WideAgent 창: 홈 / 세션 / 설정. 트레이 메뉴의 '열기'로 띄운다.
//
// 창을 닫아도 프로그램은 트레이에 남는다. 종료는 트레이 메뉴의 '종료'로만 한다.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WideAgent
{
    // exe 옆 WideAgent.settings.txt 에 'key=value' 로 한 줄씩 둔다.
    // 대화창 폭은 예전부터 쓰던 WideAgent.width.txt 를 그대로 쓴다.
    static class AppSettings
    {
        public static string FilePath;
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static void Load()
        {
            values.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    values[raw.Substring(0, eq).Trim().ToLowerInvariant()] = raw.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in values) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("설정 저장 실패: " + ex.Message); }
        }

        public static string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key.ToLowerInvariant(), out v) ? v : fallback;
        }

        public static int GetInt(string key, int fallback)
        {
            int n;
            return int.TryParse(Get(key, ""), out n) ? n : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            string v = Get(key, "");
            if (v == "") return fallback;
            return v == "1" || v.ToLowerInvariant() == "true";
        }

        public static void Set(string key, string value) { values[key.ToLowerInvariant()] = value; }
        public static void Set(string key, int value) { Set(key, value.ToString()); }
        public static void Set(string key, bool value) { Set(key, value ? "1" : "0"); }

        public static ThemePreference Theme
        {
            get
            {
                string v = Get("theme", "system");
                return v == "light" ? ThemePreference.Light : v == "dark" ? ThemePreference.Dark : ThemePreference.System;
            }
            set
            {
                Set("theme", value == ThemePreference.Light ? "light" : value == ThemePreference.Dark ? "dark" : "system");
                Save();
            }
        }

        public static bool CheckUpdates
        {
            get { return GetBool("checkUpdates", true); }
            set { Set("checkUpdates", value); Save(); }
        }
    }

    // GitHub Releases 에서 최신 버전을 확인한다. 받거나 설치하지는 않고 릴리스 페이지를 연다.
    static class Updater
    {
        const string Api = "https://api.github.com/repos/luaelsia/WideAgent/releases/latest";
        public const string ReleasesUrl = "https://github.com/luaelsia/WideAgent/releases/latest";
        public const string RepoUrl = "https://github.com/luaelsia/WideAgent";

        public static Version Current
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string CurrentText { get { return Current.ToString(3); } }

        // 결과는 부른 스레드(UI)로 돌려준다. latest 가 null 이면 error 에 이유가 있다.
        // 창 핸들에 기대지 않는다. 창을 한 번도 띄우지 않은 상태에서도 자동 확인이 돌기 때문이다.
        public static void Check(Action<Version, string> done)
        {
            SynchronizationContext ui = SynchronizationContext.Current ?? new SynchronizationContext();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Version latest = null;
                string error = null;
                try
                {
                    // .NET Framework 4 의 기본값에는 TLS 1.2 가 없다. GitHub 는 1.2 이상만 받는다.
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    using (var web = new WebClient())
                    {
                        web.Headers[HttpRequestHeader.UserAgent] = "WideAgent/" + CurrentText;
                        web.Encoding = Encoding.UTF8;
                        string json = web.DownloadString(Api);
                        Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                        if (m.Success) latest = new Version(m.Groups[1].Value);
                        else error = "릴리스 정보를 읽지 못했습니다";
                    }
                }
                catch (Exception ex) { error = ex.Message; }

                ui.Post(delegate { done(latest, error); }, null);
            });
        }

        public static void OpenReleases()
        {
            try { Process.Start(ReleasesUrl); } catch { }
        }
    }

    // 로그에서 적용 기록을 읽는다. 상주 중에 따로 세지 않아도 재시작 뒤까지 이어진다.
    static class ApplyHistory
    {
        // 성공한 적용만 센다. 실패는 로그와 최근 기록에서 본다.
        public class Stat
        {
            public int Applied;
            public DateTime LastAt;
        }

        static readonly Regex Line = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})  (.*)$");

        static string[] ReadLog()
        {
            try { return File.Exists(Log.Path) ? File.ReadAllLines(Log.Path, Encoding.UTF8) : new string[0]; }
            catch { return new string[0]; }
        }

        // 오늘 하루 성공한 적용 횟수와 마지막 성공 시각.
        public static Stat Today(string app)
        {
            var s = new Stat();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string prefix = app + " 적용 결과: ";
            foreach (string raw in ReadLog())
            {
                Match m = Line.Match(raw);
                if (!m.Success || !m.Groups[1].Value.StartsWith(today)) continue;
                string msg = m.Groups[2].Value;
                if (!msg.StartsWith(prefix)) continue;
                string r = msg.Substring(prefix.Length);
                int cut = r.IndexOf("  [");
                if (cut >= 0) r = r.Substring(0, cut);
                if (r != "OK") continue;
                s.Applied++;
                DateTime.TryParse(m.Groups[1].Value, out s.LastAt);
            }
            return s;
        }

        // 홈의 최근 기록 카드에 보여 줄 줄들. 적용 결과와 정리 내역만 고른다.
        public static List<string> Recent(int count)
        {
            var result = new List<string>();
            string[] lines = ReadLog();
            for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
            {
                Match m = Line.Match(lines[i]);
                if (!m.Success) continue;
                string msg = m.Groups[2].Value;
                if (!(msg.Contains("적용 결과:") || msg.StartsWith("자동 정리") || msg.StartsWith("세션 종료") ||
                      msg.StartsWith("터미널 종료") || msg.StartsWith("포기"))) continue;
                int cut = msg.IndexOf("  [");
                if (cut >= 0) msg = msg.Substring(0, cut);
                result.Add(m.Groups[1].Value.Substring(5, 11) + "  " + msg);
            }
            return result;
        }
    }

    class MainForm : Form
    {
        static readonly Font TitleFont = new Font("Segoe UI", 17f, FontStyle.Bold);
        static readonly Font CardTitleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        static readonly Font SectionFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        static readonly Font SmallFont = new Font("Segoe UI", 8.75f);

        public const int PageHome = 0, PageSessions = 1, PageSettings = 2;

        readonly List<NavItem> navs = new List<NavItem>();
        readonly List<Control> pages = new List<Control>();
        readonly ThemedToolTip tip = new ThemedToolTip();
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        Panel pageHost;
        TableLayoutPanel navList;
        int currentPage = -1;
        public bool AllowClose;

        public MainForm()
        {
            Text = "WideAgent";
            Icon = AppIcon.Get(32);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(UiDraw.S(1200), UiDraw.S(720));
            MinimumSize = new Size(UiDraw.S(820), UiDraw.S(560));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiDraw.S(208)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            pageHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            root.Controls.Add(BuildSidebar(), 0, 0);
            root.Controls.Add(pageHost, 1, 0);

            AddPage(Glyphs.Home, "홈", BuildHomePage());
            AddPage(Glyphs.List, "세션", BuildSessionsPage());
            AddPage(Glyphs.Settings, "설정", BuildSettingsPage());

            // 보이는 페이지만 주기적으로 새로 읽는다. 창이 숨겨져 있으면 아무것도 하지 않는다.
            refreshTimer.Interval = 15000;
            refreshTimer.Tick += delegate { if (Visible && WindowState != FormWindowState.Minimized) RefreshPage(); };
            refreshTimer.Start();

            AppTheme.Changed += OnThemeChanged;
            ThemeApplier.Apply(this);
            ShowPage(PageHome);
        }

        void OnThemeChanged()
        {
            if (IsDisposed) return;
            ThemeApplier.Apply(this);
            ReloadSessions();
            Invalidate(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // X 는 숨기기만 한다. 트레이 메뉴의 종료나 Windows 종료일 때만 실제로 닫는다.
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            AppTheme.Changed -= OnThemeChanged;
            refreshTimer.Stop();
            base.OnFormClosing(e);
        }

        public void ShowPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                navs[i].Selected = i == index;
                pages[i].Visible = i == index;
            }
            currentPage = index;
            RefreshPage();
        }

        public void RefreshPage()
        {
            if (Program.IsBusy) return;   // 적용 중에는 Claude 쪽 상태를 건드리지 않는다
            if (currentPage == PageHome) RefreshHome();
            else if (currentPage == PageSessions) ReloadSessions();
            else if (currentPage == PageSettings) RefreshSettings();
        }

        // ---------- 뼈대 ----------

        Control BuildSidebar()
        {
            // 사이드바는 여백 없이 두고 메뉴 영역에만 여백을 준다. 아래 고래가 사이드바 폭 끝까지 닿게 하기 위해서다.
            var sidebar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0), Tag = ThemeApplier.SidebarTag };
            var menuArea = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = UiDraw.S(14, 20, 14, 0) };
            sidebar.Controls.Add(menuArea);
            sidebar.Controls.Add(BuildSidebarFooter());

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            menuArea.Controls.Add(layout);

            var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = UiDraw.S(6, 0, 0, 18) };
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiDraw.S(40)));
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var logo = new PictureBox
            {
                Size = new Size(UiDraw.S(30), UiDraw.S(30)),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0),
                Image = AppIcon.Bitmap(UiDraw.S(30))
            };
            brand.Controls.Add(logo, 0, 0);
            brand.Controls.Add(new Label { Text = "WideAgent", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold), Margin = new Padding(0) }, 1, 0);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiDraw.S(52)));
            layout.Controls.Add(brand);

            navList = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
            navList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(navList);

            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) });
            return sidebar;
        }

        // 사이드바 맨 아래: 옅은 고래 위에 버전, 제작자, 저장소 링크.
        static Control BuildSidebarFooter()
        {
            int sidebarWidth = UiDraw.S(208);
            int whaleHeight = (int)Math.Ceiling(WhaleShape.Height * sidebarWidth / WhaleShape.BaseWidth);
            var footer = new WhaleFooter { Dock = DockStyle.Bottom, Margin = new Padding(0), Padding = UiDraw.S(22, 0, 14, 14) };
            footer.Height = whaleHeight + footer.BodyBelowWhale + UiDraw.S(12);

            // 글자 뒤로 고래가 보이도록 글자 쪽 배경은 투명하게 둔다(ThemeApplier가 칠하지 않게 SkipTag).
            var text = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Dock = DockStyle.Bottom,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Tag = ThemeApplier.SkipTag
            };
            text.Controls.Add(new Label { Text = "WideAgent " + Updater.CurrentText, AutoSize = true, BackColor = Color.Transparent, Font = SmallFont, Tag = ThemeApplier.SecondaryTag, Margin = new Padding(0, 0, 0, UiDraw.S(2)) });
            text.Controls.Add(new Label { Text = "miniwhalelabs", AutoSize = true, BackColor = Color.Transparent, Font = SmallFont, Tag = ThemeApplier.SecondaryTag, Margin = new Padding(0, 0, 0, UiDraw.S(2)) });
            var repo = new LinkLabel { Text = "GitHub", AutoSize = true, BackColor = Color.Transparent, Font = SmallFont, Margin = new Padding(0), LinkBehavior = LinkBehavior.HoverUnderline };
            repo.LinkClicked += delegate { try { Process.Start(Updater.RepoUrl); } catch { } };
            text.Controls.Add(repo);
            footer.Controls.Add(text);
            return footer;
        }

        void AddPage(string glyph, string title, Control page)
        {
            int index = pages.Count;
            var nav = new NavItem(glyph, title) { Dock = DockStyle.Fill };
            nav.Click += delegate { ShowPage(index); };
            navList.RowStyles.Add(new RowStyle(SizeType.Absolute, nav.Height + nav.Margin.Vertical));
            navList.Controls.Add(nav);
            navs.Add(nav);

            page.Dock = DockStyle.Fill;
            page.Visible = false;
            pageHost.Controls.Add(page);
            pages.Add(page);
        }

        // ---------- 공통 부품 ----------

        // 스크롤되는 페이지. 제목과 설명 아래로 Add 한 부품이 위에서부터 쌓인다.
        class PageStack
        {
            public readonly Panel Root;
            readonly TableLayoutPanel stack;

            public PageStack(string title, string subtitle)
            {
                Root = new Panel { AutoScroll = true, Padding = UiDraw.S(32, 26, 32, 0), Margin = new Padding(0) };
                stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0), Padding = new Padding(0, 0, 0, UiDraw.S(40)) };
                stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                Root.Controls.Add(stack);

                Add(new Label { Text = title, AutoSize = true, Font = TitleFont, Margin = new Padding(0, 0, 0, UiDraw.S(string.IsNullOrEmpty(subtitle) ? 12 : 2)) });
                if (!string.IsNullOrEmpty(subtitle))
                    Add(new Label { Text = subtitle, AutoSize = true, Tag = ThemeApplier.SecondaryTag, Margin = new Padding(0, 0, 0, UiDraw.S(18)) });
            }

            public void Add(Control c)
            {
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                if (!c.AutoSize) c.Dock = DockStyle.Fill;
                stack.Controls.Add(c);
            }

            public void AddSection(string caption)
            {
                Add(new Label { Text = caption, AutoSize = true, Font = SectionFont, Tag = ThemeApplier.SecondaryTag, Margin = UiDraw.S(4, 8, 0, 6) });
            }
        }

        static ModernButton Button(string text, bool primary)
        {
            return new ModernButton { Text = text, IsPrimary = primary };
        }

        static Label Detail(string text)
        {
            return new Label { Text = text };
        }

        // 제목 한 줄과 흐린 설명 한 줄.
        static TableLayoutPanel TextStack(string title, Label detail, Font titleFont)
        {
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            stack.Controls.Add(new Label { Text = title, AutoSize = true, Font = titleFont, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 0, 0, UiDraw.S(1)) }, 0, 0);
            detail.AutoSize = false;
            detail.AutoEllipsis = true;
            detail.Dock = DockStyle.Fill;
            detail.Tag = ThemeApplier.SecondaryTag;
            detail.Margin = new Padding(0, UiDraw.S(1), 0, 0);
            detail.TextAlign = ContentAlignment.TopLeft;
            stack.Controls.Add(detail, 0, 1);
            return stack;
        }

        static FlowLayoutPanel ButtonRow(Control[] buttons)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            for (int i = buttons.Length - 1; i >= 0; i--) row.Controls.Add(buttons[i]);
            return row;
        }

        // 카드 안에 같은 높이의 행들을 쌓는다.
        static CardPanel RowsCard(Control[] rows, int rowHeight)
        {
            var card = new CardPanel { Padding = UiDraw.S(20, 4, 20, 4), Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            foreach (Control r in rows)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
                r.Dock = DockStyle.Fill;
                table.Controls.Add(r);
            }
            card.Controls.Add(table);
            card.Height = rows.Length * rowHeight + card.Padding.Vertical;
            return card;
        }

        static int RowHeight { get { return UiDraw.S(52); } }

        // 설정 한 줄: 왼쪽에 제목과 설명 아이콘(툴팁), 필요하면 흐린 상태 문구, 오른쪽에 컨트롤.
        TableLayoutPanel SettingRow(string title, string description, Control control, Label status)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var titleRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            var titleLabel = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 0, UiDraw.S(4), 0) };
            var info = new Label { Text = Glyphs.Info, AutoSize = true, Font = UiDraw.IconFont(8.5f), Tag = ThemeApplier.SecondaryTag, Cursor = Cursors.Help, Margin = new Padding(0, UiDraw.S(3), UiDraw.S(10), 0), AccessibleName = title + " 설명", AccessibleDescription = description };
            titleRow.Controls.Add(titleLabel);
            titleRow.Controls.Add(info);
            if (status != null)
            {
                status.AutoSize = true;
                status.Tag = ThemeApplier.SecondaryTag;
                status.Font = SmallFont;
                status.Margin = new Padding(0, UiDraw.S(1), 0, 0);
                titleRow.Controls.Add(status);
            }
            row.Controls.Add(titleRow, 0, 0);
            foreach (Control target in new Control[] { row, titleRow, titleLabel, info }) tip.SetToolTip(target, description);

            control.Anchor = AnchorStyles.Right;
            control.Margin = new Padding(UiDraw.S(16), 0, 0, 0);
            row.Controls.Add(control, 1, 0);
            return row;
        }

        // 한 줄짜리 넓은 카드: 아이콘, 제목과 설명, 오른쪽 버튼들.
        static CardPanel WideCard(IconTile tile, string title, Label detail, params Control[] buttons)
        {
            var card = new CardPanel { Height = UiDraw.S(86), Padding = UiDraw.S(18, 12, 18, 12), Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiDraw.S(54)));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            card.Controls.Add(layout);
            tile.Anchor = AnchorStyles.Left;
            layout.Controls.Add(tile, 0, 0);
            layout.Controls.Add(TextStack(title, detail, CardTitleFont), 1, 0);
            FlowLayoutPanel buttonRow = ButtonRow(buttons);
            buttonRow.Anchor = AnchorStyles.Right;
            buttonRow.Dock = DockStyle.None;
            layout.Controls.Add(buttonRow, 2, 0);
            return card;
        }

        // 앱 카드: 위에 아이콘과 이름, 설명. 아래에 상태 배지와 버튼.
        static CardPanel AppCard(string letter, BadgeKind tileKind, string name, Label detail, StatusBadge badge, params Control[] buttons)
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Padding = UiDraw.S(18, 16, 18, 16) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiDraw.S(46)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiDraw.S(36)));
            card.Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiDraw.S(54)));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            header.Controls.Add(new IconTile(letter, tileKind, false) { Anchor = AnchorStyles.Left }, 0, 0);
            header.Controls.Add(TextStack(name, detail, CardTitleFont), 1, 0);
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Height = 3 }, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            badge.Anchor = AnchorStyles.Left;
            footer.Controls.Add(badge, 0, 0);
            footer.Controls.Add(ButtonRow(buttons), 1, 0);
            layout.Controls.Add(footer, 0, 2);
            return card;
        }

        // ---------- 홈 ----------

        StatusBadge claudeBadge, chatGPTBadge;
        Label claudeDetail, chatGPTDetail, sessionsDetail;
        Label recentLabel;

        Control BuildHomePage()
        {
            var page = new PageStack("홈", "대화창 폭과 Claude Code 세션 상태");

            claudeBadge = new StatusBadge();
            claudeDetail = Detail("확인하는 중...");
            ModernButton applyClaude = Button("지금 적용", true);
            applyClaude.Click += delegate { Program.ApplyNow(AppKind.Claude); };

            chatGPTBadge = new StatusBadge();
            chatGPTDetail = Detail("확인하는 중...");
            ModernButton applyChatGPT = Button("지금 적용", true);
            applyChatGPT.Click += delegate { Program.ApplyNow(AppKind.ChatGPT); };
            ModernButton shortcut = Button("바로가기", false);
            shortcut.Click += delegate { Program.CreateChatGPTShortcut(); };
            tip.SetToolTip(shortcut, "바탕 화면에 'ChatGPT (Wide)' 바로가기를 만듭니다. 이 바로가기로 열면 앱을 껐다 켜지 않고 넓어집니다.");

            var apps = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = UiDraw.S(150), Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
            apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            apps.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            CardPanel claudeCard = AppCard("C", BadgeKind.Accent, "Claude", claudeDetail, claudeBadge, applyClaude);
            claudeCard.Margin = new Padding(0, 0, UiDraw.S(5), 0);
            CardPanel chatGPTCard = AppCard("G", BadgeKind.Success, "ChatGPT", chatGPTDetail, chatGPTBadge, shortcut, applyChatGPT);
            chatGPTCard.Margin = new Padding(UiDraw.S(5), 0, 0, 0);
            apps.Controls.Add(claudeCard, 0, 0);
            apps.Controls.Add(chatGPTCard, 1, 0);
            page.Add(apps);

            sessionsDetail = Detail("확인하는 중...");
            ModernButton openSessions = Button("세션 보기", false);
            openSessions.Click += delegate { ShowPage(PageSessions); };
            page.Add(WideCard(new IconTile(Glyphs.Code, BadgeKind.Accent, true), "Claude Code 세션", sessionsDetail, openSessions));

            page.AddSection("최근 기록");
            var recentCard = new CardPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = UiDraw.S(20, 14, 20, 14), Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
            recentLabel = new Label { AutoSize = true, Font = SmallFont, Tag = ThemeApplier.SecondaryTag, Margin = new Padding(0), Dock = DockStyle.Top };
            recentCard.Controls.Add(recentLabel);
            page.Add(recentCard);
            recentCard.Dock = DockStyle.Fill;

            page.AddSection("자주 묻는 질문");
            CardPanel faq = BuildFaqCard();
            page.Add(faq);
            faq.Dock = DockStyle.Fill;

            return page.Root;
        }

        static readonly string[][] Faq =
        {
            new[] { "적용하는 동안 왜 키보드와 마우스에서 손을 떼야 하나요?",
                "Claude는 DevTools Console에 코드를 붙여넣어 넓힙니다. 다른 앱 창에 키를 보내려면 그 창이 맨 앞에 있어야 하고, " +
                "프로그램이 만들어 보낸 가짜 입력은 Claude(Chromium)가 무시합니다. 그래서 실제 키 입력을 보내는데, " +
                "그 사이에 클릭하거나 타이핑하면 포커스가 다른 창으로 넘어가 키가 엉뚱한 곳으로 갑니다. " +
                "이렇게 중단되면 약 3초 뒤에 최대 3번까지 다시 시도합니다." },
            new[] { "적용할 때 클립보드를 쓰나요?",
                "네. 한글 입력기가 켜져 있으면 타이핑한 코드가 한글로 들어가기 때문에 붙여넣기로 넣습니다. " +
                "적용이 됐는지 확인할 때도 결과를 클립보드로 꺼내 읽습니다. 원래 있던 클립보드 내용은 백업했다가 되돌립니다." },
            new[] { "앱 파일을 고치나요?",
                "고치지 않습니다. Claude에서 DevTools를 열려면 developer_settings.json 에 allowDevTools 설정이 필요해서, " +
                "없으면 이 파일만 만듭니다. 다른 내용이 들어 있으면 .bak 으로 백업한 뒤 씁니다." },
            new[] { "ChatGPT는 왜 'ChatGPT (Wide)' 바로가기로 열어야 하나요?",
                "ChatGPT를 넓히려면 디버깅 연결이 필요한데, 평범하게 연 ChatGPT에는 이 연결을 나중에 붙일 수 없어 껐다 켜야 합니다. " +
                "바로가기는 처음부터 연결을 달고 띄우므로 껐다 켜는 일이 없습니다. 평범하게 열었다면 알림을 클릭할 때 그 자리에서 다시 시작하며 넓힙니다." },
            new[] { "다시 좁아졌어요.",
                "앱 업데이트, 로그아웃, 화면 새로 고침 뒤에는 넓힘이 풀릴 수 있습니다. 트레이 메뉴나 위 카드의 [지금 적용]을 누르면 다시 넓어집니다." },
            new[] { "세션을 끄면 대화가 사라지나요?",
                "사라지지 않습니다. 끄는 것은 그 세션의 Claude Code 프로세스뿐이고 대화 기록은 남습니다. " +
                "다시 메시지를 보내면 앱이 Claude Code를 새로 띄워 이어 갑니다. 자동 정리는 작업 중인 세션을 끄지 않습니다." },
            new[] { "'짝 세션 없음' 터미널은 무엇인가요?",
                "터미널은 세션과 같은 순간에 뜨므로 시작 시각으로 짝을 맞춥니다. 세션의 Claude Code가 한 번 꺼졌다 다시 뜨면 시작 시각이 달라져 짝이 끊깁니다. " +
                "그래서 아직 열려 있는 세션의 터미널일 수 있고, 자동 정리는 이 터미널을 끄지 않습니다." },
            new[] { "인터넷으로 무엇을 보내나요?",
                "대화 내용은 읽지도 보내지도 않습니다. 네트워크는 ChatGPT 디버깅용 로컬 포트(127.0.0.1)와 " +
                "새 버전 확인용 GitHub Releases 조회만 씁니다. 새 버전 자동 확인은 설정에서 끌 수 있습니다." }
        };

        // 질문을 누르면 답이 펼쳐진다. 답은 카드 폭에 맞춰 줄바꿈한다.
        CardPanel BuildFaqCard()
        {
            var card = new CardPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = UiDraw.S(20, 10, 20, 10), Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
            var list = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiDraw.S(24)));
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var answers = new List<Label>();

            foreach (string[] qa in Faq)
            {
                var glyph = new Label { Text = Glyphs.ChevronRight, AutoSize = true, Font = UiDraw.IconFont(8f), Tag = ThemeApplier.SecondaryTag, Cursor = Cursors.Hand, Margin = new Padding(0, UiDraw.S(11), 0, 0) };
                var question = new Label { Text = qa[0], AutoSize = true, Font = new Font(Font, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, UiDraw.S(8), 0, UiDraw.S(8)) };
                var answer = new Label { Text = qa[1], AutoSize = true, Tag = ThemeApplier.SecondaryTag, Visible = false, Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
                answers.Add(answer);

                EventHandler toggle = delegate
                {
                    answer.Visible = !answer.Visible;
                    glyph.Text = answer.Visible ? Glyphs.ChevronDown : Glyphs.ChevronRight;
                };
                glyph.Click += toggle;
                question.Click += toggle;

                int row = list.RowCount;
                list.RowCount += 2;
                list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                list.Controls.Add(glyph, 0, row);
                list.Controls.Add(question, 1, row);
                list.Controls.Add(answer, 1, row + 1);
            }

            // 라벨은 스스로 줄바꿈하지 않으므로 최대 폭을 카드 안쪽 폭에 맞춰 준다.
            EventHandler fit = delegate
            {
                int w = Math.Max(UiDraw.S(200), card.ClientSize.Width - card.Padding.Horizontal - UiDraw.S(24));
                foreach (Label a in answers) a.MaximumSize = new Size(w, 0);
            };
            card.Resize += fit;
            card.Controls.Add(list);
            fit(card, EventArgs.Empty);
            return card;
        }

        void RefreshHome()
        {
            SetAppStatus(AppKind.Claude, "Claude", claudeBadge, claudeDetail);
            SetAppStatus(AppKind.ChatGPT, "ChatGPT", chatGPTBadge, chatGPTDetail);

            try
            {
                SessionSnapshot snap = CodeSessions.Take();
                long total = 0;
                TimeSpan longest = TimeSpan.Zero;
                int busy = 0;
                foreach (CodeSession s in snap.Sessions)
                {
                    total += s.Memory + (s.Terminal != null ? s.Terminal.Memory : 0);
                    if (s.Status == "busy") busy++;
                    else if (DateTime.Now - s.LastActivity > longest) longest = DateTime.Now - s.LastActivity;
                }
                string text = "세션 " + snap.Sessions.Count + "개 (작업 중 " + busy + "), 메모리 " + (total / (1024 * 1024)).ToString("N0") + " MB";
                if (longest > TimeSpan.Zero) text += ", 가장 오래 쉰 세션 " + Elapsed(longest);
                sessionsDetail.Text = text + ". " + Cleanup.Summary;
            }
            catch (Exception ex) { sessionsDetail.Text = "세션을 읽지 못했습니다: " + ex.Message; }

            List<string> recent = ApplyHistory.Recent(8);
            recentLabel.Text = recent.Count == 0 ? "기록 없음" : string.Join("\n", recent.ToArray());
        }

        static void SetAppStatus(AppKind kind, string name, StatusBadge badge, Label detail)
        {
            ApplyHistory.Stat st = ApplyHistory.Today(name);
            string history = st.Applied == 0 ? "오늘 적용 기록 없음"
                : "오늘 " + st.Applied + "회 적용, 마지막 " + st.LastAt.ToString("HH:mm");

            bool running = Claude.IsReady(kind);
            if (!running) { badge.Set(BadgeKind.Neutral, "실행 안 함"); detail.Text = history; return; }
            if (Claude.NeedsApply(kind)) { badge.Set(BadgeKind.Warning, "좁음"); detail.Text = history; return; }

            // 넓을 때는 적용된 폭을 함께 보여 주고, 지금 설정과 다르면 다시 적용해야 바뀐다고 알린다.
            string applied = AppliedWidth.Get(kind);
            string wanted = Claude.EffectiveWidth;
            badge.Set(BadgeKind.Success, applied == "" ? "넓음" : "넓음 " + applied);
            if (applied != "" && applied != wanted)
            {
                badge.Set(BadgeKind.Warning, "넓음 " + applied);
                detail.Text = "설정은 " + wanted + ". 지금 적용하면 바뀝니다";
            }
            else detail.Text = history;
        }

        // ---------- 세션 ----------

        class Row
        {
            public CodeSession Session;
            public CodeTerminal Terminal;
        }

        enum Col { Status, Name, Cwd, Start, Activity, Elapsed, Memory, Terminal, Pid }

        DataGridView grid;
        Label sessionSummary, sessionPolicy;
        ToggleSwitch withTerminal;
        ModernButton killButton;
        Col sortCol = Col.Activity;
        bool sortAsc = true;

        Control BuildSessionsPage()
        {
            var root = new Panel { Padding = UiDraw.S(32, 26, 32, 20), Margin = new Padding(0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.Controls.Add(layout);

            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "세션", AutoSize = true, Font = TitleFont, Margin = new Padding(0, 0, 0, UiDraw.S(2)) });
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "실행 중인 Claude Code 세션과 각 세션의 터미널. 머리글을 누르면 정렬합니다", AutoSize = true, Tag = ThemeApplier.SecondaryTag, Margin = new Padding(0, 0, 0, UiDraw.S(14)) });

            // 도구 줄: 왼쪽 요약, 오른쪽 터미널 스위치와 버튼
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, UiDraw.S(10)) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sessionSummary = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            bar.Controls.Add(sessionSummary, 0, 0);

            var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            withTerminal = new ToggleSwitch { Checked = true, Margin = new Padding(0, UiDraw.S(5), UiDraw.S(6), 0) };
            var withTerminalLabel = new Label { Text = "터미널도 같이 끄기", AutoSize = true, Margin = new Padding(0, UiDraw.S(8), UiDraw.S(8), 0) };
            withTerminalLabel.Click += delegate { withTerminal.Checked = !withTerminal.Checked; };
            tip.SetToolTip(withTerminal, "세션을 끌 때 그 세션의 Terminal 패널 셸도 함께 끕니다. 명령이 실행 중인 터미널은 끄지 않습니다.");
            ModernButton refresh = Button("새로고침", false);
            refresh.Click += delegate { ReloadSessions(); };
            killButton = Button("선택한 항목 끄기", false);
            killButton.IsDanger = true;
            killButton.Click += delegate { KillSelected(); };
            actions.Controls.Add(withTerminal);
            actions.Controls.Add(withTerminalLabel);
            actions.Controls.Add(refresh);
            actions.Controls.Add(killButton);
            bar.Controls.Add(actions, 1, 0);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(bar);

            var card = new CardPanel { Dock = DockStyle.Fill, Padding = UiDraw.S(10, 8, 10, 8), Margin = new Padding(0) };
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = UiDraw.S(36),
                StandardTab = true
            };
            grid.RowTemplate.Height = UiDraw.S(32);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            AddColumn("상태", 62);
            AddColumn("세션 이름", 230);
            AddColumn("작업 폴더", 150);
            AddColumn("시작", 62);
            AddColumn("마지막 활동", 104);
            AddColumn("경과", 92);
            AddColumn("메모리", 76);
            AddColumn("터미널", 96);
            AddColumn("PID", 58);
            grid.Columns[(int)Col.Elapsed].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns[(int)Col.Memory].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns[(int)Col.Pid].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.ColumnHeaderMouseClick += delegate (object s, DataGridViewCellMouseEventArgs e)
            {
                var col = (Col)e.ColumnIndex;
                if (col == sortCol) sortAsc = !sortAsc;
                else { sortCol = col; sortAsc = true; }
                ApplySort();
            };
            grid.SelectionChanged += delegate { killButton.Enabled = grid.SelectedRows.Count > 0; };
            card.Controls.Add(grid);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.Controls.Add(card);

            sessionPolicy = new Label { AutoSize = true, Font = SmallFont, Tag = ThemeApplier.SecondaryTag, Margin = new Padding(0, UiDraw.S(8), 0, 0) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(sessionPolicy);
            return root;
        }

        // 이름과 폴더 말고는 내용 길이가 거의 일정하므로 최소 폭을 줘서 잘리지 않게 한다.
        void AddColumn(string header, int weight)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, MinimumWidth = UiDraw.S(weight >= 150 ? weight * 2 / 3 : weight), SortMode = DataGridViewColumnSortMode.Programmatic };
            grid.Columns.Add(c);
        }

        public void ReloadSessions()
        {
            if (grid == null) return;

            // 새로 읽어도 고르던 줄이 풀리지 않게, 고른 PID를 기억했다가 다시 고른다.
            var picked = new HashSet<int>();
            foreach (DataGridViewRow r in grid.SelectedRows) picked.Add(PidOf((Row)r.Tag));

            SessionSnapshot snap;
            try { snap = CodeSessions.Take(); }
            catch (Exception ex) { sessionSummary.Text = "세션을 읽지 못했습니다: " + ex.Message; return; }

            ThemePalette t = AppTheme.Current;
            DateTime now = DateTime.Now;
            long total = 0;
            grid.Rows.Clear();
            foreach (CodeSession s in snap.Sessions)
            {
                total += s.Memory + (s.Terminal != null ? s.Terminal.Memory : 0);
                string status = s.Orphan ? "고아" : s.Status == "busy" ? "작업 중" : s.Status == "idle" ? "대기" : s.Status;
                int i = grid.Rows.Add(status, string.IsNullOrEmpty(s.Name) ? "(이름 없음)" : s.Name, s.Cwd,
                    Stamp(s.StartedAt, now), Stamp(s.LastActivity, now), Elapsed(now - s.LastActivity),
                    Mb(s.Memory), TerminalText(s.Terminal), s.Pid.ToString());
                DataGridViewRow row = grid.Rows[i];
                row.Tag = new Row { Session = s, Terminal = s.Terminal };
                if (s.Status == "busy") row.DefaultCellStyle.ForeColor = t.SuccessSoftText;
            }
            foreach (CodeTerminal term in snap.LooseTerminals)
            {
                total += term.Memory;
                int i = grid.Rows.Add("터미널", "(짝 세션 없음)", "", Stamp(term.Start, now), "", "", Mb(term.Memory), TerminalText(term), term.Pid.ToString());
                DataGridViewRow row = grid.Rows[i];
                row.Tag = new Row { Terminal = term };
                row.DefaultCellStyle.ForeColor = t.TextSecondary;
            }
            ApplySort();

            grid.ClearSelection();
            foreach (DataGridViewRow r in grid.Rows)
                if (picked.Contains(PidOf((Row)r.Tag))) r.Selected = true;
            killButton.Enabled = grid.SelectedRows.Count > 0;

            sessionSummary.Text = "세션 " + snap.Sessions.Count + "개, 터미널 포함 메모리 " + (total / (1024 * 1024)).ToString("N0") + " MB";
            sessionPolicy.Text = Cleanup.Summary + ". 정리한 세션은 다시 메시지를 보내면 이어집니다.";
        }

        static int PidOf(Row r) { return r.Session != null ? r.Session.Pid : r.Terminal.Pid; }

        void ApplySort()
        {
            grid.Sort(new RowComparer(sortCol, sortAsc));
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = c.Index == (int)sortCol
                    ? (sortAsc ? SortOrder.Ascending : SortOrder.Descending) : SortOrder.None;
        }

        class RowComparer : System.Collections.IComparer
        {
            readonly Col col;
            readonly bool asc;
            public RowComparer(Col col, bool asc) { this.col = col; this.asc = asc; }

            public int Compare(object x, object y)
            {
                var a = (Row)((DataGridViewRow)x).Tag;
                var b = (Row)((DataGridViewRow)y).Tag;
                // 짝 없는 터미널은 정렬 방향과 상관없이 항상 아래에 둔다.
                if ((a.Session == null) != (b.Session == null)) return a.Session == null ? 1 : -1;
                int r = Key(a).CompareTo(Key(b));
                if (r == 0) r = PidOf(a).CompareTo(PidOf(b));
                return asc ? r : -r;
            }

            IComparable Key(Row r)
            {
                CodeSession s = r.Session;
                CodeTerminal t = r.Terminal;
                switch (col)
                {
                    case Col.Status: return s != null ? (s.Orphan ? "0" : s.Status ?? "") : "";
                    case Col.Name: return s != null ? s.Name ?? "" : "";
                    case Col.Cwd: return s != null ? s.Cwd ?? "" : "";
                    case Col.Start: return s != null ? s.StartedAt : t.Start;
                    case Col.Activity: return s != null ? s.LastActivity : DateTime.MinValue;
                    case Col.Elapsed: return s != null ? DateTime.MaxValue - s.LastActivity : TimeSpan.Zero;
                    case Col.Memory: return s != null ? s.Memory : t.Memory;
                    case Col.Terminal: return t != null ? t.Pid : 0;
                    default: return PidOf(r);
                }
            }
        }

        void KillSelected()
        {
            var rows = new List<Row>();
            bool anyBusy = false, anyLoose = false;
            bool takeTerminal = withTerminal.Checked;
            var names = new StringBuilder();
            DateTime now = DateTime.Now;
            foreach (DataGridViewRow gr in grid.SelectedRows)
            {
                var r = (Row)gr.Tag;
                rows.Add(r);
                if (r.Session != null)
                {
                    if (r.Session.Status == "busy") anyBusy = true;
                    names.Append("- " + (string.IsNullOrEmpty(r.Session.Name) ? "(이름 없음)" : r.Session.Name) +
                        "  (마지막 활동 " + Elapsed(now - r.Session.LastActivity) + " 전)");
                    if (takeTerminal && r.Terminal != null) names.Append(" + 터미널");
                    names.AppendLine();
                }
                else
                {
                    anyLoose = true;
                    names.AppendLine("- 짝 세션 없는 터미널 " + r.Terminal.Name + " [PID " + r.Terminal.Pid + "]");
                }
            }
            if (rows.Count == 0) return;

            string msg = "다음 항목을 끕니다.\n\n" + names +
                "\n세션은 대화 기록이 남고, 다시 메시지를 보내면 이어집니다." +
                "\n명령이 실행 중인 터미널은 끄지 않습니다.";
            if (anyBusy) msg += "\n\n작업 중으로 표시된 세션이 포함되어 있습니다. 진행 중인 작업은 중단됩니다.";
            // 세션의 Claude Code가 한 번 꺼졌다 다시 뜨면 시작 시각이 달라져 짝이 끊긴다.
            // 그래서 짝 없는 터미널도 아직 열려 있는 세션의 터미널일 수 있다.
            if (anyLoose) msg += "\n\n짝 세션 없는 터미널은 다시 뜬 세션의 터미널일 수 있습니다.";

            if (MessageBox.Show(this, msg, "WideAgent", MessageBoxButtons.OKCancel,
                    anyBusy ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;

            var failed = new StringBuilder();
            foreach (Row r in rows)
            {
                string error;
                if (r.Session != null)
                {
                    string label = CodeSessions.Label(r.Session);
                    if (CodeSessions.Kill(r.Session, out error))
                        Log.Write("세션 종료: " + label + ", 마지막 활동 " + r.Session.LastActivity.ToString("MM-dd HH:mm") + ", 상태 " + r.Session.Status);
                    else
                    {
                        Log.Write("세션 종료 실패: " + label + ", " + error);
                        failed.AppendLine("- " + label + ": " + error);
                    }
                }
                if (r.Terminal != null && (r.Session == null || takeTerminal))
                {
                    string label = "터미널 " + r.Terminal.Name + " [PID " + r.Terminal.Pid + "]";
                    if (CodeSessions.KillTerminal(r.Terminal, out error))
                        Log.Write("터미널 종료: " + label);
                    else
                    {
                        Log.Write("터미널 종료 실패: " + label + ", " + error);
                        failed.AppendLine("- " + label + ": " + error);
                    }
                }
            }

            grid.ClearSelection();
            ReloadSessions();
            if (failed.Length > 0)
                MessageBox.Show(this, "끄지 못한 항목이 있습니다.\n\n" + failed, "WideAgent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------- 설정 ----------

        static readonly string[] WidthPresets = { "", "1600px", "2000px", "2560px" };

        SegmentedControl widthSelector, cleanupSelector, themeSelector;
        TextBox widthCustom;
        Label widthDetail, updateStatus;
        ToggleSwitch cleanupTerminal, cleanupNotify, autoStart, checkUpdates;
        bool loadingSettings;

        Control BuildSettingsPage()
        {
            var page = new PageStack("설정", null);

            // 대화창 폭
            widthSelector = new SegmentedControl("기본 1280px", "1600", "2000", "2560", "직접");
            widthCustom = new TextBox { Width = UiDraw.S(130), Margin = new Padding(0, UiDraw.S(5), UiDraw.S(10), 0), Visible = false };
            widthDetail = Detail("");
            widthSelector.SelectedIndexChanged += delegate
            {
                if (loadingSettings) return;
                int i = widthSelector.SelectedIndex;
                bool custom = i >= WidthPresets.Length;
                widthCustom.Visible = custom;
                if (custom)
                {
                    widthCustom.Text = Claude.Width;
                    widthCustom.Focus();
                    widthCustom.SelectAll();
                }
                else SaveWidth(WidthPresets[i]);
            };
            widthCustom.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string v = NormalizeWidth(widthCustom.Text);
                if (v == null) widthDetail.Text = "예: 1800px, 90vw, min(2400px,80vw)";
                else SaveWidth(v);
            };
            var widthPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            widthPanel.Controls.Add(widthCustom);
            widthPanel.Controls.Add(widthSelector);
            page.AddSection("대화창 폭");
            page.Add(RowsCard(new Control[]
            {
                SettingRow("최대 폭", "Claude와 ChatGPT 대화창 본문의 최대 폭입니다. 바꾼 폭은 다음 적용부터 반영됩니다. 직접 입력은 CSS 길이 값을 쓰고 Enter로 저장합니다.", widthPanel, widthDetail)
            }, RowHeight));

            // 자동 정리
            cleanupSelector = new SegmentedControl("끔", "3시간", "4시간", "6시간");
            cleanupSelector.SelectedIndexChanged += delegate
            {
                if (loadingSettings) return;
                int i = cleanupSelector.SelectedIndex;
                Program.SetCleanupHours(i <= 0 ? 0 : Cleanup.HourChoices[i - 1]);
            };
            cleanupTerminal = new ToggleSwitch();
            cleanupTerminal.CheckedChanged += delegate
            {
                if (loadingSettings) return;
                Cleanup.Terminal = cleanupTerminal.Checked;
                Cleanup.Save();
                Program.CleanupSettingsChanged();
            };
            cleanupNotify = new ToggleSwitch();
            cleanupNotify.CheckedChanged += delegate
            {
                if (loadingSettings) return;
                Cleanup.Notify = cleanupNotify.Checked;
                Cleanup.Save();
            };
            page.AddSection("놀고 있는 세션 자동 정리");
            page.Add(RowsCard(new Control[]
            {
                SettingRow("정리 기준", "대기 상태로 이 시간이 지난 Claude Code 세션을 10분마다 끕니다. 작업 중인 세션은 끄지 않습니다. 끈 세션은 다시 메시지를 보내면 이어집니다.", cleanupSelector, null),
                SettingRow("터미널도 같이 정리", "정리한 세션의 Terminal 패널 셸도 함께 끕니다. 명령이 실행 중인 터미널과 짝 세션이 없는 터미널은 끄지 않습니다.", cleanupTerminal, null),
                SettingRow("정리 알림", "세션을 정리하면 트레이 알림으로 개수와 메모리를 알립니다. 알림을 클릭하면 세션 화면이 열립니다.", cleanupNotify, null)
            }, RowHeight));

            // 시작
            autoStart = new ToggleSwitch();
            autoStart.CheckedChanged += delegate
            {
                if (loadingSettings) return;
                try
                {
                    AutoStart.Set(autoStart.Checked);
                    Log.Write("자동 실행 " + (autoStart.Checked ? "켬" : "끔"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "자동 실행 설정 실패: " + ex.Message, "WideAgent");
                    RefreshSettings();
                }
            };
            page.AddSection("시작");
            page.Add(RowsCard(new Control[]
            {
                SettingRow("Windows 시작 시 자동 실행", "Windows에 로그인하면 WideAgent를 트레이로 시작합니다.", autoStart, null)
            }, RowHeight));

            // 화면
            themeSelector = new SegmentedControl("시스템", "라이트", "다크");
            themeSelector.SelectedIndexChanged += delegate
            {
                if (loadingSettings) return;
                ThemePreference p = themeSelector.SelectedIndex == 1 ? ThemePreference.Light
                    : themeSelector.SelectedIndex == 2 ? ThemePreference.Dark : ThemePreference.System;
                AppSettings.Theme = p;
                AppTheme.SetPreference(p);
            };
            page.AddSection("화면");
            page.Add(RowsCard(new Control[]
            {
                SettingRow("테마", "시스템을 고르면 Windows의 라이트/다크 설정을 따라갑니다.", themeSelector, null)
            }, RowHeight));

            // 관리
            ModernButton logButton = Button("열기", false);
            logButton.Click += delegate { Program.OpenLog(); };
            ModernButton folderButton = Button("열기", false);
            folderButton.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(Application.ExecutablePath) + "\""); } catch { }
            };
            page.AddSection("관리");
            page.Add(RowsCard(new Control[]
            {
                SettingRow("로그", "적용 결과와 세션 정리 내역이 기록된 파일입니다.", logButton, null),
                SettingRow("설정 폴더", "WideAgent.exe가 있는 폴더입니다. 설정 파일(WideAgent.settings.txt, WideAgent.width.txt)과 로그가 이곳에 있습니다.", folderButton, null)
            }, RowHeight));

            // 업데이트
            updateStatus = Detail("");
            ModernButton checkButton = Button("업데이트 확인", false);
            checkButton.Click += delegate
            {
                checkButton.Enabled = false;
                updateStatus.Text = "확인하는 중...";
                Updater.Check(delegate (Version latest, string error)
                {
                    checkButton.Enabled = true;
                    if (latest == null) { updateStatus.Text = "확인 실패: " + error; return; }
                    if (latest > Updater.Current)
                    {
                        updateStatus.Text = "새 버전 " + latest.ToString(3) + " 이 있습니다";
                        if (MessageBox.Show(this, "새 버전 " + latest.ToString(3) + " 이 있습니다. 릴리스 페이지를 열까요?",
                                "WideAgent", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            Updater.OpenReleases();
                    }
                    else updateStatus.Text = "최신 버전입니다";
                });
            };
            checkUpdates = new ToggleSwitch();
            checkUpdates.CheckedChanged += delegate
            {
                if (loadingSettings) return;
                AppSettings.CheckUpdates = checkUpdates.Checked;
                Log.Write("업데이트 자동 확인 " + (checkUpdates.Checked ? "켬" : "끔"));
            };
            page.AddSection("업데이트");
            page.Add(RowsCard(new Control[]
            {
                SettingRow("현재 버전 " + Updater.CurrentText, "GitHub Releases에서 새 버전이 있는지 확인합니다. 새 버전이 있으면 릴리스 페이지를 엽니다.", checkButton, updateStatus),
                SettingRow("새 버전 자동 확인", "시작하고 1분 뒤와 그 뒤 하루에 한 번 확인하고, 새 버전이 있으면 트레이로 알립니다.", checkUpdates, null)
            }, RowHeight));

            return page.Root;
        }

        void RefreshSettings()
        {
            loadingSettings = true;
            try
            {
                int wi = Array.IndexOf(WidthPresets, Claude.Width);
                widthSelector.SetSilently(wi >= 0 ? wi : WidthPresets.Length);
                widthCustom.Visible = wi < 0;
                if (wi < 0) widthCustom.Text = Claude.Width;
                widthDetail.Text = "";

                int ci = Array.IndexOf(Cleanup.HourChoices, Cleanup.Hours);
                cleanupSelector.SetSilently(Cleanup.Hours <= 0 ? 0 : ci >= 0 ? ci + 1 : 0);
                cleanupTerminal.Checked = Cleanup.Terminal;
                cleanupNotify.Checked = Cleanup.Notify;

                autoStart.Checked = AutoStart.Enabled;
                themeSelector.SetSilently(AppTheme.Preference == ThemePreference.Light ? 1 : AppTheme.Preference == ThemePreference.Dark ? 2 : 0);
                checkUpdates.Checked = AppSettings.CheckUpdates;
            }
            finally { loadingSettings = false; }
        }

        void SaveWidth(string value)
        {
            Program.SetWidth(value);
            widthDetail.Text = "다음 적용부터 반영됩니다";
        }

        // 숫자만 넣으면 px 를 붙인다. 그 밖에는 CSS 길이로 보이는 값만 받는다.
        static string NormalizeWidth(string raw)
        {
            string v = (raw ?? "").Trim();
            if (v == "") return null;
            if (Regex.IsMatch(v, @"^\d{3,4}$")) return v + "px";
            if (Regex.IsMatch(v, @"^[0-9a-z.,()%\s+\-*/]+$", RegexOptions.IgnoreCase)) return v;
            return null;
        }

        // ---------- 표시 ----------

        static string TerminalText(CodeTerminal t)
        {
            if (t == null) return "";
            return (t.Running ? "실행 중 " : "유휴 ") + t.Pid;
        }

        static string Mb(long bytes) { return (bytes / (1024 * 1024)).ToString("N0") + " MB"; }

        static string Stamp(DateTime t, DateTime now)
        {
            if (t == DateTime.MinValue) return "";
            return t.Date == now.Date ? t.ToString("HH:mm") : t.ToString("MM-dd HH:mm");
        }

        public static string Elapsed(TimeSpan d)
        {
            if (d.TotalMinutes < 1) return "방금";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "분";
            if (d.TotalDays < 1) return (int)d.TotalHours + "시간 " + d.Minutes + "분";
            return (int)d.TotalDays + "일 " + d.Hours + "시간";
        }
    }
}
