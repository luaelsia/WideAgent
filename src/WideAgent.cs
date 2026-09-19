// WideAgent - Claude와 ChatGPT 데스크톱의 대화창 폭을 넓게 유지한다.
//
// 앱 본문 폭은 <html> 의 data-transcript-width 속성으로 정해지며 기본값 s(약 840px)로 고정된다.
// CSS에는 l(1280px)도 정의되어 있으나 설정으로 노출되지 않는다.
//
// 전제 조건은 developer_settings.json 의 {"allowDevTools": true} 하나뿐이다.
// 예전에는 DevTools 스니펫에 코드를 등록해 두고 그것을 실행했으나, 앱 업데이트로
// 저장소가 이관되면서 스니펫이 사라진 적이 있다. 그래서 코드를 이 실행 파일 안에
// 넣고 Console에 직접 붙여넣는 방식으로 바꿨다. 잃어버릴 등록물이 없다.
//
// 적용하는 동안 DevTools 창은 화면 밖으로 옮겨 두어 보이지 않게 하고, 대신 포커스를
// 뺏지 않는 오버레이로 진행 상황을 알린다. 포커스 이동 자체는 피할 수 없다.
// 다른 앱 창에 키를 보내려면 그 창이 포그라운드여야 하고, PostMessage 로 합성한
// 입력은 Chromium이 무시하기 때문이다.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WideAgent
{
    static class Native
    {
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

        public struct RECT { public int Left, Top, Right, Bottom; }

        public const int SW_RESTORE = 9;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_IME_CONTROL = 0x0283;
        public const int IMC_SETOPENSTATUS = 0x0006;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        // 한글 IME를 영문 모드로 돌린다. 키로 텍스트를 입력할 때 한글로 들어가는 것을 막는다.
        public static void ImeOff(IntPtr target)
        {
            try
            {
                IntPtr ime = ImmGetDefaultIMEWnd(target);
                if (ime != IntPtr.Zero)
                    SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, IntPtr.Zero);
            }
            catch { }
        }

        public static void MoveTo(IntPtr h, int x, int y)
        {
            try { SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }
            catch { }
        }
    }

    class Win
    {
        public IntPtr Handle;
        public string Title;
        public long Area;
        public uint Pid;
    }

    enum AppKind
    {
        Claude,
        ChatGPT
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static string Path;

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);

                    var info = new FileInfo(Path);
                    if (info.Length > 200000)
                    {
                        string[] lines = File.ReadAllLines(Path, Encoding.UTF8);
                        int keep = Math.Min(300, lines.Length);
                        string[] tail = new string[keep];
                        Array.Copy(lines, lines.Length - keep, tail, 0, keep);
                        File.WriteAllLines(Path, tail, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }
    }

    // 포커스를 가져가지 않는 진행 표시 창.
    // DevTools를 화면 밖에 숨겨 두기 때문에, 지금 어디까지 진행됐는지 이것으로 알린다.
    class Overlay : Form
    {
        Label title, status, warning;
        Panel barTrack, barFill;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE - 클릭해도 포커스를 뺏지 않는다
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW - Alt+Tab 목록에 뜨지 않는다
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(520, 196);
            BackColor = Color.FromArgb(26, 26, 28);
            Opacity = 0.96;

            title = new Label();
            title.Text = "WideAgent";
            title.ForeColor = Color.FromArgb(150, 150, 155);
            title.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            title.SetBounds(28, 22, 464, 20);
            Controls.Add(title);

            status = new Label();
            status.ForeColor = Color.FromArgb(238, 238, 242);
            status.Font = new Font("Segoe UI", 16f, FontStyle.Regular);
            status.SetBounds(28, 46, 464, 34);
            Controls.Add(status);

            barTrack = new Panel();
            barTrack.BackColor = Color.FromArgb(56, 56, 60);
            barTrack.SetBounds(28, 94, 464, 5);
            Controls.Add(barTrack);

            barFill = new Panel();
            barFill.BackColor = Color.FromArgb(217, 119, 87);   // Claude 주황
            barFill.SetBounds(0, 0, 0, 5);
            barTrack.Controls.Add(barFill);

            // 적용 중에 키보드나 마우스를 건드리면 키 입력이 엉뚱한 창으로 가거나
            // 포커스가 넘어가 그 시점부터 실패한다. 그래서 눈에 띄게 경고한다.
            warning = new Label();
            warning.Text = "키보드와 마우스를 건드리지 마세요";
            warning.ForeColor = Color.FromArgb(235, 178, 90);
            warning.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            warning.SetBounds(28, 114, 464, 46);
            Controls.Add(warning);
        }

        public void SetHeadline(string text)
        {
            title.Text = text;
            Refresh();
        }

        // 마지막 재시도까지 실패했을 때. 사유와 함께 다시 시도하는 방법을 알려준다.
        public void ShowGuidance(string reason, string guidance)
        {
            status.Text = reason;
            status.ForeColor = Color.FromArgb(240, 120, 110);
            barFill.BackColor = Color.FromArgb(200, 80, 70);
            barFill.Width = barTrack.Width;
            warning.Text = guidance;
            warning.ForeColor = Color.FromArgb(200, 200, 208);
            Refresh();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(72, 72, 78)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        // 화면 한가운데에 띄운다. 모니터가 여럿이면 Claude 창이 있는 화면을 기준으로 한다.
        public void PlaceNear(IntPtr anchor)
        {
            Screen scr = anchor != IntPtr.Zero ? Screen.FromHandle(anchor) : Screen.PrimaryScreen;
            Rectangle a = scr.WorkingArea;
            Location = new Point(a.Left + (a.Width - Width) / 2, a.Top + (a.Height - Height) / 2);
        }

        public void Step(string text, double progress, bool error)
        {
            status.Text = text;
            status.ForeColor = error ? Color.FromArgb(240, 120, 110) : Color.FromArgb(238, 238, 242);
            barFill.BackColor = error ? Color.FromArgb(200, 80, 70) : Color.FromArgb(217, 119, 87);

            if (error)
            {
                warning.Text = "적용에 실패했습니다";
                warning.ForeColor = Color.FromArgb(240, 120, 110);
            }
            else if (progress >= 1)
            {
                warning.Text = "이제 사용하셔도 됩니다";
                warning.ForeColor = Color.FromArgb(120, 200, 140);
            }
            else
            {
                warning.Text = "키보드와 마우스를 건드리지 마세요";
                warning.ForeColor = Color.FromArgb(235, 178, 90);
            }

            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;
            barFill.Width = (int)(barTrack.Width * progress);
            Refresh();
        }
    }

    static class Claude
    {
        static AppKind target = AppKind.Claude;

        public static string TargetName
        {
            get { return target == AppKind.Claude ? "Claude" : "ChatGPT"; }
        }

        static string ProcessName
        {
            get { return target == AppKind.Claude ? "claude" : "ChatGPT"; }
        }

        static string DevToolsShortcut
        {
            get { return target == AppKind.Claude ? "^%i" : "^+i"; }
        }

        // 본문 폭 단계: s(약 840px) / m / l(1280px). 앱 기본값은 s 고정이다.
        public const string Mode = "l";

        // 1280px 상한을 넘기고 싶을 때만 채운다. exe 옆의 WideAgent.width.txt 로 바꿀 수 있다.
        public static string Width = "";

        // 진행 상황 보고용. (문구, 0~1 진행률, 오류여부)
        public static Action<string, double, bool> Progress;

        // 단계별 소요 시간 계측. 튜닝할 때 어디서 시간이 새는지 보려고 남긴다.
        static Stopwatch clock;
        static StringBuilder timing;
        static long lastMark;

        public static string Timing { get { return timing == null ? "" : timing.ToString(); } }

        static void Mark(string phase)
        {
            if (clock == null) return;
            long now = clock.ElapsedMilliseconds;
            timing.Append(phase).Append('=').Append(now - lastMark).Append("ms ");
            lastMark = now;
        }

        static void Report(string text, double p) { if (Progress != null) Progress(text, p, false); }
        static string Fail(string text, double p) { if (Progress != null) Progress(text, p, true); return text; }

        // 대기하면서도 오버레이가 다시 그려지도록 메시지를 처리한다.
        static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        static bool IsDevToolsTitle(string title)
        {
            return title.IndexOf("DevTools", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.StartsWith("Developer Tools", StringComparison.OrdinalIgnoreCase);
        }

        public static List<Win> GetWindows()
        {
            var pids = new HashSet<uint>();
            foreach (Process p in Process.GetProcessesByName(ProcessName))
            {
                pids.Add((uint)p.Id);
                p.Dispose();
            }

            var found = new List<Win>();
            if (pids.Count == 0) return found;

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                int len = Native.GetWindowTextLength(h);
                var sb = new StringBuilder(len + 1);
                Native.GetWindowText(h, sb, sb.Capacity);
                Native.RECT rect;
                long area = 0;
                if (Native.GetWindowRect(h, out rect))
                    area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
                found.Add(new Win { Handle = h, Title = sb.ToString(), Area = area, Pid = pid });
                return true;
            }, IntPtr.Zero);

            return found;
        }

        public static Win MainWindow()
        {
            Win best = null;
            foreach (Win w in GetWindows())
                if (!IsDevToolsTitle(w.Title) && (best == null || w.Area > best.Area)) best = w;
            return best;
        }

        public static Win MainWindow(AppKind kind)
        {
            AppKind before = target;
            target = kind;
            Win main = MainWindow();
            target = before;
            return main;
        }

        public static Win DevToolsWindow()
        {
            foreach (Win w in GetWindows())
                if (IsDevToolsTitle(w.Title)) return w;
            return null;
        }

        public static bool IsReady(AppKind kind)
        {
            AppKind before = target;
            target = kind;
            bool ready = MainWindow() != null;
            target = before;
            return ready;
        }

        // Windows는 백그라운드 프로세스가 창을 함부로 앞으로 내보내는 것을 막는다.
        // 현재 포그라운드 창과 대상 창의 입력 스레드에 우리를 붙이면 그 제한이 풀린다.
        public static bool SetFront(IntPtr h)
        {
            for (int i = 0; i < 5; i++)
            {
                if (Native.GetForegroundWindow() == h) return true;
                if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);

                IntPtr fg = Native.GetForegroundWindow();
                uint dummy;
                uint fgThread = Native.GetWindowThreadProcessId(fg, out dummy);
                uint tgtThread = Native.GetWindowThreadProcessId(h, out dummy);
                uint ourThread = Native.GetCurrentThreadId();

                bool a1 = false, a2 = false;
                if (fgThread != ourThread) a1 = Native.AttachThreadInput(ourThread, fgThread, true);
                if (tgtThread != ourThread) a2 = Native.AttachThreadInput(ourThread, tgtThread, true);

                Native.BringWindowToTop(h);
                Native.SetForegroundWindow(h);

                if (a1) Native.AttachThreadInput(ourThread, fgThread, false);
                if (a2) Native.AttachThreadInput(ourThread, tgtThread, false);

                Pump(120);
            }
            return Native.GetForegroundWindow() == h;
        }

        // WM_CLOSE 를 보내고 실제로 사라질 때까지 기다린다.
        static bool CloseDevTools(int timeoutMs)
        {
            Win d = DevToolsWindow();
            if (d == null) return true;
            Native.SendMessage(d.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Pump(50);
                if (DevToolsWindow() == null) return true;
            }
            return false;
        }

        // 포커스가 여전히 Claude 쪽(메인 창이든 DevTools든)에 있는지.
        // 아니면 사용자가 다른 프로그램을 쓰고 있다는 뜻이다.
        public const string FocusMsg = "다른 창을 사용 중이라 중단됨";

        // 자동으로 도는 경우엔 ChatGPT를 껐다 켜지 않는다. 사용자가 방금 열어서 쓰려는
        // 참인데 앱이 사라졌다 돌아오면 놀란다. 붙을 수 없으면 알리기만 하고 물러난다.
        public const string RestartNeededMsg = "재시작이 필요함";
        public static bool AllowChatGPTRestart = true;

        // 알려진 CSS 변수 이름(1층)으로는 폭이 변하지 않아, 스타일시트에서 찾아낸
        // 이름(2층)으로 적용했을 때 실제로 쓴 이름을 담아 둔다. ChatGPT 앱이 변수
        // 이름을 바꿨다는 뜻이다. 알리지 않으면 사용자도 만든 사람도 한참 뒤에야 안다.
        public static string FallbackNames;

        // 이번 적용에서 실제로 앱을 띄웠는지. 우리가 만든 프로세스 교체를
        // 감시 루프가 '새로 실행됨'으로 오해하지 않게 하려고 본다.
        public static bool Restarted;

        static bool IsTargetForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            foreach (Win w in GetWindows())
                if (w.Handle == fg) return true;
            return false;
        }

        static bool IsOffScreen(IntPtr h)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;
            Rectangle vs = SystemInformation.VirtualScreen;
            return r.Left >= vs.Right || r.Top >= vs.Bottom || r.Right <= vs.Left || r.Bottom <= vs.Top;
        }

        static Win WaitForDevTools(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Win w = DevToolsWindow();
                if (w != null) return w;

                // 사용자가 다른 프로그램으로 넘어갔으면 Ctrl+Alt+I 가 Claude에 닿지 않은 것이다.
                // 끝까지 기다려 봐야 소용없으니 일찍 접는다.
                if (sw.ElapsedMilliseconds > 1200 && !IsTargetForeground())
                {
                    focusLost = true;
                    return null;
                }
                Pump(50);
            }
            return null;
        }

        // Console에 붙여넣을 코드. data-transcript-width 를 바꾸고, 앱이 다시 그리면서
        // 되돌리는 것을 MutationObserver로 막는다. 감시자는 중복 등록되지 않게 재사용한다.
        static string SafeWidth()
        {
            return (Width ?? "").Replace("'", "").Replace("\r", "").Replace("\n", "").Trim();
        }

        // 요청한 폭을 px 로 풀어 두고(want), 그 폭을 쓰는 요소가 몇 개인지 세는 코드
        // (scan)를 만든다. 적용과 확인이 같은 자를 쓰도록 한 곳에 둔다.
        //
        // 코드가 실행된 것과 폭이 변한 것은 다르다. 변수 이름이 바뀌면 없는 변수에 값을
        // 넣는 꼴이 되는데, CSS 는 그것을 오류로 보지 않고 조용히 무시한다. 넣기만 해서는
        // 성공과 구별되지 않아 폭을 직접 재는 이 검사가 필요하다.
        //
        // 세는 것은 문서 전체다. 대화 본문이 main 안에 있으리라고 보고 거기만 훑었더니,
        // 앱이 화면을 갈아 끼우는 동안 main 이 거의 비어 있어 아무것도 못 재는 때가 있었다.
        // 대신 적용 전후의 개수를 비교해서, 우리가 바꾼 것만 성공으로 센다.
        //
        // scan 이 -1 이면 잴 가치가 없는 페이지다. innerWidth 검사가 인라인 시각화
        // 샌드박스(1px)를 걸러 낸다.
        static string MeasureJs(string w)
        {
            return
                "const W='" + w + "'||'1280px';" +
                "const p=document.createElement('div');" +
                "p.style.cssText='position:fixed;left:-9999px;top:0;visibility:hidden;max-width:'+W;" +
                "document.body.appendChild(p);const want=getComputedStyle(p).maxWidth;p.remove();" +
                "const scan=()=>{if(innerWidth<400||want==='none'||!document.querySelector('main'))return -1;" +
                "const es=document.querySelectorAll('*');const n=Math.min(es.length,6000);let c=0;" +
                "for(let i=0;i<n;i++){if(getComputedStyle(es[i]).maxWidth===want)c++;}return c;};";
        }

        static string BuildPayload()
        {
            string w = SafeWidth();
            if (target == AppKind.ChatGPT)
            {
                return
                    "(()=>{" + MeasureJs(w) +
                    "const SEL=':root,body,[data-codex-window-type=\"electron\"],[class*=\"--thread-content-max-width\"]';" +
                    "const css=ns=>ns.map(n=>n+':'+W+' !important').join(';');" +
                    // 있던 것은 지우고 시작한다. 남겨 두면 적용 전 개수가 이미 올라가 있어
                    // 전후 비교가 무의미해진다. 이 사이에 화면이 다시 그려지지는 않는다.
                    "const old=document.getElementById('wide-chatgpt');if(old)old.remove();" +
                    "const before=scan();" +
                    "if(before<0)return 'wide-chatgpt failed';" +
                    "const s=document.createElement('style');s.id='wide-chatgpt';document.head.appendChild(s);" +
                    "const base=SEL+'{'+css(['--thread-content-max-width'])+'}';" +
                    "s.textContent=base;document.documentElement.dataset.wideChatgpt='1';" +
                    "if(scan()>before)return 'wide-chatgpt applied L1';" +
                    // 2층. 알려진 이름이 먹지 않았으니 스타일시트에서 후보를 찾아 전부 건다.
                    // 이름만 바뀌고 구조가 그대로면 여기서 살아난다.
                    "const found=new Set();" +
                    "for(const sh of document.styleSheets){try{for(const ru of sh.cssRules){" +
                    "const st=ru.style;if(!st)continue;" +
                    "for(let i=0;i<st.length;i++){const q=st[i];" +
                    "if(q.slice(0,2)==='--'&&q.indexOf('max')>=0&&q.indexOf('width')>=0" +
                    "&&/thread|conversation|chat|message|content/.test(q))found.add(q);}" +
                    "}}catch(e){}}" +
                    "if(found.size){const ns=[...found];" +
                    "s.textContent=SEL+',*{'+css(ns)+'}';" +
                    "if(scan()>before)return 'wide-chatgpt applied L2 '+ns.join(' ');" +
                    // 2층도 아니면 넓히지도 못하면서 온 문서의 변수만 건드린 꼴이다. 되돌린다.
                    "s.textContent=base;}" +
                    // 여기까지 왔다는 것은 폭을 쓰는 요소가 애초에 화면에 없었다는 뜻이다.
                    // 대화창이 아직 비어 있는 때가 그렇다. 넣어 두기는 했으나 확인은 못 했다.
                    // 실패로 단정하면 멀쩡한 창에 대고 실패를 알리게 된다.
                    "return before===0?'wide-chatgpt applied blind':'wide-chatgpt failed';})()";
            }

            return
                "(()=>{const M='" + Mode + "',W='" + w + "';" +
                "const r=document.documentElement;" +
                "const a=()=>{if(r.dataset.transcriptWidth!==M)r.dataset.transcriptWidth=M};a();" +
                "if(window.__wideChatObs)window.__wideChatObs.disconnect();" +
                "window.__wideChatObs=new MutationObserver(a);" +
                "window.__wideChatObs.observe(r,{attributes:true,attributeFilter:['data-transcript-width']});" +
                "const o=document.getElementById('wide-chat');if(o)o.remove();" +
                "if(W){const s=document.createElement('style');s.id='wide-chat';" +
                "s.textContent='*{--max-content-width:'+W+' !important}';document.head.appendChild(s);}" +
                "return 'wide-chat applied: '+M+(W?' @ '+W:' (1280px)');})()";
        }

        // 적용 결과를 실제로 확인한다.
        //
        // 창 제목(document.title)으로 표식을 남기는 방법을 먼저 시도했으나, Electron이
        // 창 제목을 메인 프로세스에서 관리해서 페이지가 바꿔도 반영되지 않았다.
        // DevTools 콘솔의 copy() 명령은 값을 시스템 클립보드에 넣어 주므로,
        // 그것을 바깥에서 읽는 방식으로 바꿨다.
        static bool ReadBackMode(int timeoutMs)
        {
            string command = target == AppKind.Claude
                ? "copy(document.documentElement.dataset.transcriptWidth||'none')"
                : "copy(document.documentElement.dataset.wideChatgpt||'none')";
            string expected = target == AppKind.Claude ? Mode : "1";
            if (!Paste(command)) return false;
            Pump(60);
            if (!Send("{ENTER}")) return false;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Pump(30);
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string v = Clipboard.GetText().Trim();
                        if (v == expected) return true;
                        if (v == "none" || v == "s" || v == "m") return false;
                    }
                }
                catch { }
            }
            return false;
        }

        // 키를 보내는 동안 대상 창이 계속 앞에 있어야 한다. 사용자가 중간에 다른 창을
        // 클릭하면 남은 키 입력이 그 프로그램으로 들어가 버린다. 코드 한 덩어리가
        // 엉뚱한 곳에 붙여넣어지는 셈이라 실패보다 이쪽이 위험하다.
        // 그래서 매 입력 직전에 확인하고, 어긋나면 즉시 멈춘다.
        static IntPtr guardWnd;
        static bool focusLost;

        static bool Guard()
        {
            if (guardWnd != IntPtr.Zero && Native.GetForegroundWindow() != guardWnd)
            {
                focusLost = true;
                return false;
            }
            return true;
        }

        static bool Send(string keys)
        {
            if (!Guard()) return false;
            SendKeys.SendWait(keys);
            return true;
        }

        // 텍스트는 타이핑하지 않고 붙여넣는다. 키로 치면 한글 IME가 켜져 있을 때
        // 한글로 들어가 버리기 때문이다.
        static bool Paste(string text)
        {
            if (!Guard()) return false;
            Clipboard.SetText(text);
            Pump(25);
            return Send("^v");
        }

        // Claude의 설정 폴더를 찾는다.
        // MSIX(스토어) 설치본은 앱 데이터가 패키지 가상 경로에 들어간다.
        // 예전 버전은 %APPDATA%\Claude 를 썼고, 업데이트 때 가상 경로로 이관됐다.
        static string FindUserDataDir()
        {
            var candidates = new List<string>();
            try
            {
                string pkgs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(pkgs))
                    foreach (string d in Directory.GetDirectories(pkgs, "Claude_*"))
                        candidates.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude"));
            }
            catch { }
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"));

            // config.json 이 있는 폴더가 실제로 쓰이는 곳이다
            foreach (string c in candidates)
                if (File.Exists(Path.Combine(c, "config.json"))) return c;
            foreach (string c in candidates)
                if (Directory.Exists(c)) return c;
            return null;
        }

        // DevTools를 열 수 있게 하는 앱 자체의 스위치를 켠다.
        // 이게 없으면 Ctrl+Alt+I 를 눌러도 아무 반응이 없다. 처음 쓰는 사람은
        // 당연히 꺼져 있으므로 프로그램이 직접 만들어 준다.
        // 앱이 이 파일을 누를 때마다 새로 읽으므로 Claude를 재시작할 필요는 없다.
        // 성공하면 null, 실패하면 사유를 돌려준다.
        public static string EnsureDevToolsAllowed()
        {
            string dir = FindUserDataDir();
            if (dir == null) return "Claude 설정 폴더를 찾지 못함";

            string file = Path.Combine(dir, "developer_settings.json");
            try
            {
                if (File.Exists(file))
                {
                    string t = File.ReadAllText(file);
                    if (t.IndexOf("allowDevTools", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        t.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0)
                        return null;                      // 이미 켜져 있다
                    File.Copy(file, file + ".bak", true); // 남의 설정을 지우지 않도록 백업
                }
                File.WriteAllText(file, "{\r\n  \"allowDevTools\": true\r\n}\r\n", new UTF8Encoding(false));
                Log.Write("DevTools 허용 설정 생성: " + file);
                return null;
            }
            catch (Exception ex)
            {
                return "DevTools 허용 설정 실패: " + ex.Message;
            }
        }

        // 코드를 붙여넣어 실행하고 실제로 반영됐는지까지 확인한다.
        static bool TryPayload(int verifyMs)
        {
            if (!Paste(BuildPayload())) return false;
            Pump(70);
            if (!Send("{ENTER}")) return false;
            Pump(70);
            return ReadBackMode(verifyMs);
        }

        // 커맨드 메뉴로 Console 패널을 연다.
        // '>' 접두어를 반드시 붙인다. 이게 없으면 커맨드 메뉴는 명령이 아니라
        // 파일 검색으로 동작해서 아무것도 실행되지 않는다.
        static bool OpenConsolePanel()
        {
            if (!Send("{ESC}")) return false;
            Pump(50);
            if (!Send("^+p")) return false;
            Pump(160);
            if (!Send("^a")) return false;
            Pump(30);
            if (!Paste(">Show Console")) return false;
            Pump(120);
            if (!Send("{ENTER}")) return false;
            Pump(180);
            return true;
        }

        // DevTools를 Console 패널로 남겨 두었다는 기록. 이게 있어야 지름길을 쓴다.
        // Elements 패널에 코드를 붙여넣으면 DOM에 노드가 삽입되므로 함부로 생략하면 안 된다.
        static string StatePath
        {
            get
            {
                string name = target == AppKind.Claude ? "WideAgent.state" : "WideAgent.chatgpt.state";
                return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), name);
            }
        }

        static bool ConsoleRemembered
        {
            get { try { return File.Exists(StatePath); } catch { return false; } }
        }

        static void RememberConsole()
        {
            try { File.WriteAllText(StatePath, "console"); } catch { }
        }

        // Console의 최초 붙여넣기 차단을 푼다. 이 문구만은 타이핑해야 해서 IME를 영문으로 돌린다.
        static bool AllowPasting(IntPtr dev)
        {
            Native.ImeOff(dev);
            Pump(60);
            if (!Send("allow pasting")) return false;
            Pump(120);
            if (!Send("{ENTER}")) return false;
            Pump(200);
            return true;
        }

        static string DebugPortPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WideAgent.chatgpt.port"); }
        }

        static int ReadSavedDebugPort()
        {
            try
            {
                int port;
                if (File.Exists(DebugPortPath) && int.TryParse(File.ReadAllText(DebugPortPath).Trim(), out port))
                    return port;
            }
            catch { }
            return 0;
        }

        static int FindFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        static string ReadDebugTargets(int port)
        {
            try
            {
                using (var web = new WebClient())
                {
                    web.Proxy = null;
                    return web.DownloadString("http://127.0.0.1:" + port + "/json/list");
                }
            }
            catch { return null; }
        }

        // 저장된 포트가 아직 그 ChatGPT의 것인지 확인한다.
        // 포트 번호는 재사용되므로, 응답이 왔다는 것만으로는 부족하다.
        // 디버깅 대상 목록의 형태까지 확인해야 엉뚱한 로컬 서버를 붙잡지 않는다.
        static bool HasDebugTargets(int port)
        {
            string targets = ReadDebugTargets(port);
            return !String.IsNullOrEmpty(targets) &&
                   targets.IndexOf("webSocketDebuggerUrl", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string JsonString(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        // 어느 층으로 성공했는지 담는다. 1층은 알려진 변수 이름, 2층은 스타일시트에서
        // 찾아낸 이름이다. 2층까지 갔다는 것은 ChatGPT 앱이 바뀌었다는 뜻이라 구분해 둔다.
        class CdpResult
        {
            public int Level1;   // 알려진 이름으로 넓어진 것을 확인한 페이지 수
            public int Level2;   // 찾아낸 이름으로 넓어진 것을 확인한 페이지 수
            public int Blind;    // 대화창이긴 한데 잴 것이 없어 확인은 못 한 페이지 수
            public string Names = "";
            public bool Ok { get { return Level1 + Level2 + Blind > 0; } }
        }

        // 페이지 하나에 코드를 넣고 응답 전문을 돌려준다. 못 붙으면 null.
        static string EvaluateOnTarget(int port, string url, string expression)
        {
            try
            {
                using (var ws = new ClientWebSocket())
                {
                    ws.Options.SetRequestHeader("Origin", "http://localhost:" + port);
                    var connect = ws.ConnectAsync(new Uri(url), CancellationToken.None);
                    if (!connect.Wait(3000) || ws.State != WebSocketState.Open) return null;

                    string request = "{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{" +
                        "\"expression\":" + JsonString(expression) + ",\"returnByValue\":true}}";
                    byte[] bytes = Encoding.UTF8.GetBytes(request);
                    var send = ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                        true, CancellationToken.None);
                    if (!send.Wait(3000)) return null;

                    byte[] buffer = new byte[16384];
                    var response = new StringBuilder();
                    bool complete = false;
                    while (!complete)
                    {
                        var receive = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (!receive.Wait(3000)) break;
                        WebSocketReceiveResult result = receive.Result;
                        response.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        complete = result.EndOfMessage;
                    }
                    return response.ToString();
                }
            }
            catch { return null; }
        }

        static List<string> DebugTargetUrls(int port)
        {
            var urls = new List<string>();
            string targets = ReadDebugTargets(port);
            if (String.IsNullOrEmpty(targets)) return urls;

            MatchCollection matches = Regex.Matches(targets,
                "\\\"webSocketDebuggerUrl\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
            foreach (Match match in matches)
                urls.Add(match.Groups[1].Value.Replace("\\/", "/"));
            return urls;
        }

        // 열려 있는 페이지 전부에 적용한다. 첫 성공에서 멈추지 않는다.
        //
        // 예전에는 처음 성공한 페이지에서 곧바로 끝냈다. 그러다 ChatGPT 앱이 대화창
        // 말고도 인라인 시각화 샌드박스를 띄우기 시작했는데, 그 페이지가 목록 앞자리를
        // 차지하면서 1px 짜리 샌드박스에 폭을 적용해 놓고 성공했다고 보고하는 일이
        // 벌어졌다. 로그에는 OK 가 찍히는데 대화창은 그대로였다.
        //
        // 이제는 폭이 실제로 변한 것을 확인한 페이지만 센다. 분리 창처럼 대화창이
        // 여럿일 때 전부 걸리는 것은 덤이다.
        static CdpResult EvaluateInChatGPT(int port, string expression)
        {
            var result = new CdpResult();
            foreach (string url in DebugTargetUrls(port))
            {
                string text = EvaluateOnTarget(port, url, expression);
                if (text == null) continue;

                if (text.IndexOf("wide-chatgpt applied L1", StringComparison.Ordinal) >= 0)
                {
                    result.Level1++;
                    continue;
                }

                if (text.IndexOf("wide-chatgpt applied blind", StringComparison.Ordinal) >= 0)
                {
                    result.Blind++;
                    continue;
                }

                Match found = Regex.Match(text, "wide-chatgpt applied L2 ([^\\\"]*)");
                if (found.Success)
                {
                    result.Level2++;
                    if (result.Names.Length == 0) result.Names = found.Groups[1].Value.Trim();
                }
            }
            return result;
        }

        // 읽기만 하는 확인용. 한 페이지에서라도 표식이 나오면 된다.
        static bool AnyTargetSays(int port, string expression, string marker)
        {
            foreach (string url in DebugTargetUrls(port))
            {
                string text = EvaluateOnTarget(port, url, expression);
                if (text != null && text.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        static string FindChatGPTExecutable()
        {
            foreach (Process p in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (!String.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return null;
        }

        static int RestartChatGPTWithDebugging()
        {
            string executable = ResolveChatGPTExecutable();
            if (executable == null) return 0;

            Report("ChatGPT 다시 시작하는 중", 0.18);
            Win main = MainWindow();
            if (main != null) Native.SendMessage(main.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Pump(1200);

            foreach (Process p in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    if (!p.HasExited) p.Kill();
                }
                catch { }
                finally { p.Dispose(); }
            }

            var stopped = Stopwatch.StartNew();
            while (stopped.ElapsedMilliseconds < 5000)
            {
                bool running = Process.GetProcessesByName("ChatGPT").Length > 0;
                if (!running) break;
                Pump(100);
            }

            return StartChatGPTWithDebugging(executable);
        }

        // ChatGPT를 새 디버깅 포트와 함께 띄우고, 붙을 수 있게 될 때까지 기다린다.
        // 포트 번호는 파일에 남겨 다음 적용에서 재사용한다.
        static int StartChatGPTWithDebugging(string executable)
        {
            int port = FindFreePort();
            Restarted = true;
            var info = new ProcessStartInfo();
            info.FileName = executable;
            info.Arguments = "--remote-debugging-address=127.0.0.1 --remote-debugging-port=" + port +
                " --remote-allow-origins=http://localhost:" + port;
            info.UseShellExecute = true;
            Process.Start(info);

            var started = Stopwatch.StartNew();
            while (started.ElapsedMilliseconds < 15000)
            {
                Pump(150);
                if (HasDebugTargets(port))
                {
                    try { File.WriteAllText(DebugPortPath, port.ToString(), Encoding.ASCII); } catch { }
                    return port;
                }
            }
            return 0;
        }

        // 설치 경로 기억. ChatGPT는 MSIX 패키지라 실행 파일 경로에 버전이 박혀 있고
        // (WindowsApps\OpenAI.Codex_<버전>_x64__<해시>\app\ChatGPT.exe) 업데이트마다 바뀐다.
        // 그래서 경로를 하드코딩하지 않고, 돌고 있는 프로세스에서 본 값을 캐시해 둔다.
        static string ExecutableCachePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WideAgent.chatgpt.exe.txt"); }
        }

        // 실행 파일을 찾는 순서: 돌고 있는 프로세스 → 캐시 → Windows에 직접 질의.
        //
        // 폴더 목록을 훑어 제일 높은 버전을 고르는 방법은 쓰지 않는다. WindowsApps 에는
        // 아직 등록되지 않은 다음 버전이 미리 내려받아져 함께 놓여 있을 수 있어서,
        // 최신 폴더가 지금 실제로 실행되는 버전이라는 보장이 없다.
        static string ResolveChatGPTExecutable()
        {
            string running = FindChatGPTExecutable();
            if (running != null)
            {
                try { File.WriteAllText(ExecutableCachePath, running, Encoding.UTF8); } catch { }
                return running;
            }

            try
            {
                if (File.Exists(ExecutableCachePath))
                {
                    string cached = File.ReadAllText(ExecutableCachePath, Encoding.UTF8).Trim();
                    if (cached.Length > 0 && File.Exists(cached)) return cached;
                }
            }
            catch { }

            string queried = QueryChatGPTInstallLocation();
            if (queried != null)
            {
                try { File.WriteAllText(ExecutableCachePath, queried, Encoding.UTF8); } catch { }
            }
            return queried;
        }

        // 캐시가 업데이트로 무효가 됐고 앱도 떠 있지 않을 때만 여기까지 온다.
        // 등록된 패키지가 어느 것인지는 Windows만 알고 있으므로 물어본다.
        static string QueryChatGPTInstallLocation()
        {
            try
            {
                var info = new ProcessStartInfo();
                info.FileName = "powershell.exe";
                info.Arguments = "-NoProfile -NonInteractive -Command " +
                    "\"(Get-AppxPackage -Name OpenAI.Codex).InstallLocation\"";
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.CreateNoWindow = true;
                using (Process p = Process.Start(info))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(10000)) return null;
                    foreach (string line in output.Split('\n'))
                    {
                        string dir = line.Trim();
                        if (dir.Length == 0) continue;
                        string exe = Path.Combine(dir, "app", "ChatGPT.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { }
            return null;
        }
        // ── 이미 넓은지 미리 본다 ──────────────────────────────────────────
        //
        // 이미 적용돼 있는데도 오버레이를 띄우고 포커스를 뺏는 것이 제일 거슬린다.
        // 그래서 손대기 전에 먼저 확인하되, 확인 자체가 사용자를 방해하면 안 된다.
        //
        // ChatGPT는 CDP로 값을 그냥 읽으면 되니 진짜 '확인'이다.
        // Claude는 사정이 다르다. 값을 읽으려면 DevTools를 열고 콘솔에 입력해야 해서
        // 확인 비용이 적용 비용과 같다. 그래서 관찰 대신 추론한다 - 적용에 성공했을 때
        // 그 Claude 프로세스의 신원을 적어 두고, 같은 프로세스가 계속 떠 있으면
        // 아직 넓다고 본다. 페이지의 MutationObserver가 앱의 되돌림을 막고 있으므로
        // 그 프로세스가 사는 동안은 유지된다.
        //
        // 추론이라 틀릴 수 있다. 렌더러가 페이지를 다시 읽으면(앱 업데이트, 크래시 복구,
        // 로그아웃) 관찰자가 사라지고 기본값으로 돌아가는데 프로세스는 그대로다.
        // 그때는 좁아진 것을 보고 트레이에서 직접 적용하면 된다. 사용자가 명시적으로
        // 요청한 경우(메뉴, 더블클릭)에는 이 건너뛰기를 적용하지 않는 이유다.

        static string AppliedRecordPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WideAgent.claude.applied"); }
        }

        // 프로세스 신원. PID만으로는 부족하다. 번호는 재사용되므로 시작 시각을 같이 묶는다.
        static string ClaudeProcessIdentity()
        {
            AppKind before = target;
            target = AppKind.Claude;
            Win main = MainWindow();
            target = before;
            if (main == null) return null;
            try
            {
                using (Process p = Process.GetProcessById((int)main.Pid))
                    return p.Id + ":" + p.StartTime.Ticks;
            }
            catch { return null; }
        }

        static void RememberClaudeApplied()
        {
            try
            {
                string id = ClaudeProcessIdentity();
                if (id != null) File.WriteAllText(AppliedRecordPath, id, Encoding.ASCII);
            }
            catch { }
        }

        static bool ClaudeLooksApplied()
        {
            try
            {
                if (!File.Exists(AppliedRecordPath)) return false;
                string id = ClaudeProcessIdentity();
                return id != null && id == File.ReadAllText(AppliedRecordPath).Trim();
            }
            catch { return false; }
        }

        // 이쪽은 추론이 아니라 실제로 읽는다. 포커스를 건드리지 않는다.
        static bool ChatGPTLooksApplied()
        {
            int port = ReadSavedDebugPort();
            if (port == 0 || !HasDebugTargets(port)) return false;
            // 표식(dataset)만 보면 안 된다. 표식은 남아 있는데 폭은 안 먹는 상태가
            // 있을 수 있다. ChatGPT 가 변수 이름을 바꾸면 딱 그렇게 된다. 적용할 때와
            // 같은 자로 폭을 직접 재서, 그 경우 '넓다'고 판단하지 않게 한다.
            return AnyTargetSays(port,
                "(()=>{" + MeasureJs(SafeWidth()) +
                "return (document.getElementById('wide-chatgpt')&&scan()>0)" +
                "?'wide-chatgpt present':'wide-chatgpt absent';})()",
                "wide-chatgpt present");
        }

        // 오버레이를 띄우기 전에 묻는다. 여기서 false면 사용자는 아무것도 보지 못한다.
        public static bool NeedsApply(AppKind kind)
        {
            return kind == AppKind.Claude ? !ClaudeLooksApplied() : !ChatGPTLooksApplied();
        }


        // 바로가기 아이콘을 가져오려고 밖에서도 쓴다.
        public static string ChatGPTExecutable { get { return ResolveChatGPTExecutable(); } }

        // 바로가기용 진입점. ChatGPT가 꺼져 있으면 처음부터 디버깅 포트를 달고 띄운다.
        // 이 경로로 들어오면 껐다 켜는 일이 아예 생기지 않는다.
        public static string LaunchChatGPT()
        {
            target = AppKind.ChatGPT;
            clock = Stopwatch.StartNew();
            timing = new StringBuilder();
            lastMark = 0;
            Restarted = false;
            FallbackNames = null;

            if (MainWindow() == null)
            {
                string executable = ResolveChatGPTExecutable();
                if (executable == null) return Fail("ChatGPT 설치 위치를 찾지 못함", 1);

                Report("ChatGPT 여는 중", 0.2);
                if (StartChatGPTWithDebugging(executable) == 0)
                    return Fail("ChatGPT를 디버깅 모드로 띄우지 못함", 1);
                Mark("launch");
            }

            // 여기서부터는 평소와 같다. 위에서 띄웠다면 포트가 이미 살아 있으므로
            // ApplyChatGPT 는 재시작을 건너뛰고 곧장 붙는다.
            return ApplyChatGPT();
        }

        static string ApplyChatGPT()
        {
            int port = ReadSavedDebugPort();
            if (port == 0 || !HasDebugTargets(port))
            {
                // 붙을 연결이 없다. 여기서부터는 앱을 껐다 켜야만 되는데,
                // 자동으로 도는 중이면 그건 하지 않는다.
                if (!AllowChatGPTRestart) return Fail(RestartNeededMsg, 1);
                port = RestartChatGPTWithDebugging();
                Mark("restart");
            }
            if (port == 0) return Fail("ChatGPT를 디버깅 모드로 다시 시작하지 못함", 1);

            Report("ChatGPT 폭 적용 중", 0.75);
            var ready = Stopwatch.StartNew();
            while (ready.ElapsedMilliseconds < 12000)
            {
                CdpResult r = EvaluateInChatGPT(port, BuildPayload());
                if (r.Ok)
                {
                    Mark("cdp-apply");

                    // 1층이 한 곳도 먹지 않고 2층으로만 살아났다면 앱이 바뀐 것이다.
                    // 넓어지기는 했으니 사용자에게는 성공이지만, 그대로 두면 다음 변화
                    // 때 조용히 죽는다. 찾아낸 이름을 남겨 두고 알린다.
                    if (r.Level1 == 0 && r.Level2 > 0)
                    {
                        FallbackNames = r.Names;
                        Log.Write("ChatGPT 1층 실패 - '--thread-content-max-width' 가 먹지 않음. " +
                                  "찾아낸 이름으로 적용함: " + r.Names);
                    }

                    Report("완료", 1);
                    return "OK";
                }
                Pump(250);
            }
            return Fail("ChatGPT 연결은 열렸지만 폭 적용에 실패함", 1);
        }

        public static string Apply(AppKind kind)
        {
            target = kind;
            clock = Stopwatch.StartNew();
            timing = new StringBuilder();
            lastMark = 0;
            Restarted = false;
            FallbackNames = null;

            if (target == AppKind.ChatGPT) return ApplyChatGPT();

            Win main = MainWindow();
            if (main == null) return Fail(TargetName + " 창 없음", 1);

            Report("창 활성화", 0.08);
            // 창을 앞으로 못 가져오는 것은 결국 포커스 경합이다. 사용자가 뭔가 하는 중이라는
            // 뜻이므로 실패로 끝내지 말고 재시도 대상으로 넘긴다.
            if (!SetFront(main.Handle)) return Fail(FocusMsg, 1);
            Mark("front");

            Win dev = DevToolsWindow();
            bool weOpenedIt = false;
            bool ok = false;
            IntPtr hidden = IntPtr.Zero;
            focusLost = false;

            try
            {
                if (dev == null)
                {
                    // 처음 쓰는 사람은 DevTools가 막혀 있다. 열기 전에 스위치부터 켠다.
                    if (target == AppKind.Claude)
                    {
                        string setupErr = EnsureDevToolsAllowed();
                        if (setupErr != null) return Fail(setupErr, 1);
                        Mark("setup");
                    }

                    Report("DevTools 여는 중", 0.2);
                    SendKeys.SendWait(DevToolsShortcut);
                    dev = WaitForDevTools(6000);
                    Mark("devtools-open");
                    weOpenedIt = true;
                    if (dev != null)
                    {
                        // 우리가 연 창이므로 화면 밖으로 치워 보이지 않게 한다.
                        // 포그라운드 상태는 유지되어 키 입력은 정상적으로 들어간다.
                        Rectangle vs = SystemInformation.VirtualScreen;
                        Native.MoveTo(dev.Handle, vs.Right + 80, vs.Top + 80);
                        hidden = dev.Handle;
                    }
                    Pump(160);
                    Mark("settle");
                }
                else if (IsOffScreen(dev.Handle))
                {
                    // 화면 밖에 있는 DevTools는 이전 실행이 남긴 우리 창이다.
                    // (화면 밖으로 옮기는 건 이 프로그램뿐이다) 그러니 우리가 책임지고 닫는다.
                    // 사용자가 직접 연 창은 화면 안에 있으므로 건드리지 않는다.
                    weOpenedIt = true;
                    hidden = dev.Handle;
                }
                if (dev == null)
                    return Fail(focusLost || !IsTargetForeground()
                        ? FocusMsg
                        : target == AppKind.Claude
                            ? "DevTools가 열리지 않음 (developer_settings.json 확인)"
                            : "ChatGPT DevTools가 열리지 않음 (Ctrl+Shift+I 확인)", 1);

                if (!SetFront(dev.Handle)) return Fail(FocusMsg, 1);
                Pump(120);
                Mark("dev-front");

                // 이 시점부터 보내는 모든 키는 이 창이 앞에 있을 때만 나간다
                guardWnd = dev.Handle;

                string clipBackup = null;
                try { if (Clipboard.ContainsText()) clipBackup = Clipboard.GetText(); }
                catch { }

                try
                {
                    // 커맨드 메뉴로 Console 패널을 연다.
                    // '>' 접두어를 반드시 붙인다. 이게 없으면 커맨드 메뉴는 명령이 아니라
                    // 파일 검색으로 동작해서 아무것도 실행되지 않는다.
                    Report("적용 중", 0.5);

                    // 1차 - 지름길. DevTools는 마지막에 쓰던 패널을 기억하므로, 우리가 직접 연
                    // 창이고 지난번에 Console로 끝냈다면 커맨드 메뉴를 거칠 필요가 없다.
                    // 평소에는 여기서 끝난다.
                    if (weOpenedIt && ConsoleRemembered)
                    {
                        ok = TryPayload(800);
                        Mark("fast");
                    }

                    // 2차 - 지름길을 못 쓰거나 실패했으면 커맨드 메뉴로 Console을 연다.
                    if (!ok)
                    {
                        Report("콘솔 여는 중", 0.7);
                        OpenConsolePanel();
                        Mark("console");
                        ok = TryPayload(1200);
                        Mark("payload");
                    }

                    // 3차 - 그래도 안 되면 붙여넣기가 차단된 상태로 본다.
                    // 이 문구는 타이핑해야 해서 1초 가까이 걸리므로 마지막에만 쓴다.
                    if (!ok && !focusLost)
                    {
                        Report("차단 해제 후 재시도", 0.85);
                        AllowPasting(dev.Handle);
                        ok = TryPayload(1800);
                        Mark("allow-retry");
                    }

                    // 사용자가 다른 창을 클릭해서 중단된 경우다. 남은 키가 엉뚱한 곳으로
                    // 새지는 않았으니, 포커스를 되찾아 한 번만 다시 시도한다.
                    if (!ok && focusLost)
                    {
                        Report("포커스를 되찾는 중", 0.5);
                        focusLost = false;
                        guardWnd = IntPtr.Zero;
                        if (SetFront(dev.Handle))
                        {
                            Pump(200);
                            guardWnd = dev.Handle;
                            if (OpenConsolePanel()) ok = TryPayload(1500);
                        }
                        Mark("refocus");
                    }

                    if (ok) { RememberConsole(); RememberClaudeApplied(); }
                }
                finally
                {
                    if (clipBackup != null) { try { Clipboard.SetText(clipBackup); } catch { } }
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.Message, 1);
            }
            finally
            {
                guardWnd = IntPtr.Zero;   // 정리 단계의 키 입력까지 막지 않도록 해제

                // 우리가 연 DevTools는 어떤 경로로 끝나든 반드시 닫는다.
                // 화면 밖에 숨겨 둔 창이 남으면 사용자가 찾을 수 없다.
                if (weOpenedIt)
                {
                    try
                    {
                        // 닫기는 끈질기게 한다. 화면 밖에 숨겨 둔 창이 남으면 사용자가
                        // 찾을 수도 닫을 수도 없기 때문이다.
                        // 1차: WM_CLOSE (포커스와 무관하게 동작)
                        // 2차: 창을 앞으로 가져와 Alt+F4
                        // 3차: 그래도 안 닫히면 화면 안으로 되돌려 최소한 보이게는 한다
                        if (!CloseDevTools(1000))
                        {
                            Win d2 = DevToolsWindow();
                            if (d2 != null && SetFront(d2.Handle)) SendKeys.SendWait("%{F4}");
                            if (!CloseDevTools(800) && hidden != IntPtr.Zero)
                            {
                                Native.MoveTo(hidden, 100, 100);
                                Log.Write("경고: DevTools를 닫지 못해 화면 안으로 되돌렸다");
                            }
                        }
                        Mark("close");
                    }
                    catch { }
                    Win m = MainWindow();
                    if (m != null) SetFront(m.Handle);
                }
            }

            if (!ok)
                return Fail(focusLost ? FocusMsg : "적용 확인 실패 (코드가 실행되지 않음)", 1);

            Report("완료", 1);
            return "OK";
        }
    }

    static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "WideAgent";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                    {
                        if (k == null) return false;
                        object v = k.GetValue(Name);
                        return v != null && v.ToString().IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (k == null) return;
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
            }
        }
    }

    static class Program
    {
        static NotifyIcon tray;
        static ToolStripMenuItem autoStartItem;
        static System.Windows.Forms.Timer timer;
        static Overlay overlay;
        static bool wasClaudeReady;
        static bool wasChatGPTReady;
        static AppKind activeTarget = AppKind.Claude;
        static AppKind retryTarget = AppKind.Claude;
        static bool busy;
        static bool exiting;

        // 우리가 ChatGPT를 껐다 켜면 프로세스가 잠깐 사라졌다 돌아온다. 감시 루프가
        // 그것을 '사용자가 새로 실행함'으로 오해해 한 번 더 적용하는 것을 막는다.
        static DateTime chatGPTSelfRestartUntil = DateTime.MinValue;
        const int SelfRestartQuietMs = 20000;

        // 풍선 알림을 재시작 버튼으로 쓴다. 지금 뜬 알림이 그 제안인지 구분하는 표식.
        static bool restartOffered;

        // 2층 적용 알림을 이미 띄웠는지. 그리고 그 알림이 지금 로그 열기 버튼인지.
        static bool fallbackNotified;
        static bool logOffered;

        // 사용자가 다른 창을 쓰고 있어서 실패한 경우는 잠시 뒤에 다시 하면 대개 된다.
        // 사용자를 방해하지 않으려고 바로 재시도하지 않고 간격을 두고 몇 번만 시도한다.
        const int MaxRetries = 3;

        // 재시도까지 약 3초. 실패 표시(0.8초) → 다음 점검 → 안내 카운트다운(2초) 순으로 흐른다.
        const int RetryNoticeMs = 2000;
        const int RetryGapMs = 500;
        const int RetryTotalSec = 3;
        const string RetryGuide = "다시 시도하려면 트레이 아이콘을 더블클릭하세요";

        static DateTime retryAt = DateTime.MinValue;
        static bool retryPending;
        static int attempt;          // 0 = 첫 시도, 1~3 = 재시도 회차

        [STAThread]
        static void Main(string[] args)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            Log.Path = Path.Combine(dir, "WideAgent.log");

            // 미처 잡지 못한 예외까지 로그로 남긴다. 기본 동작인 오류 대화상자는
            // 상주 프로그램에 어울리지 않는다. 사용자는 창을 띄운 적이 없는데
            // 낯선 .NET 오류 창을 보게 되고, 닫으면 그대로 종료된다.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s2, ThreadExceptionEventArgs te)
            {
                Log.Write("처리되지 않은 예외: " + te.Exception.Message);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s2, UnhandledExceptionEventArgs ue)
            {
                var ex = ue.ExceptionObject as Exception;
                Log.Write("치명적 예외: " + (ex != null ? ex.Message : "알 수 없음"));
            };

            // 폭을 1280px 이상으로 바꾸고 싶으면 exe 옆에 WideAgent.width.txt 를 만들고
            // '2000px' 이나 'min(2400px,80vw)' 같은 CSS 길이 값을 한 줄로 적으면 된다.
            try
            {
                string wf = Path.Combine(dir, "WideAgent.width.txt");
                if (File.Exists(wf)) Claude.Width = File.ReadAllText(wf).Trim();
            }
            catch { }

            Application.EnableVisualStyles();

            bool once = false;
            bool chatGPTOnly = false;
            bool launchChatGPT = false;
            int startupDelay = 0;
            foreach (string a in args)
            {
                if (a == "--apply") once = true;
                if (a == "--chatgpt") { once = true; chatGPTOnly = true; }
                if (a.StartsWith("--delay=") && !Int32.TryParse(a.Substring(8), out startupDelay)) startupDelay = 0;
                if (a == "--launch-chatgpt") launchChatGPT = true;
            }

            // 바로가기로 들어온 경우. ChatGPT를 우리가 직접 띄우므로 껐다 켜는 일이 없다.
            if (launchChatGPT)
            {
                overlay = new Overlay();
                Claude.Progress = OnProgress;
                activeTarget = AppKind.ChatGPT;
                ShowOverlay();
                overlay.SetHeadline("WideAgent - ChatGPT");
                string lr = Claude.LaunchChatGPT();
                Log.Write("--launch-chatgpt: " + lr + "  [" + Claude.Timing + "]");
                HideOverlay(lr == "OK" ? 300 : 2600);
                return;
            }

            if (once)
            {
                overlay = new Overlay();
                Claude.Progress = OnProgress;
                if (startupDelay > 0)
                {
                    var delay = Stopwatch.StartNew();
                    while (delay.ElapsedMilliseconds < startupDelay)
                    {
                        Application.DoEvents();
                        Thread.Sleep(25);
                    }
                }
                bool applied = false;
                AppKind[] kinds = chatGPTOnly
                    ? new AppKind[] { AppKind.ChatGPT }
                    : new AppKind[] { AppKind.Claude, AppKind.ChatGPT };
                foreach (AppKind kind in kinds)
                {
                    if (!Claude.IsReady(kind)) continue;
                    applied = true;
                    activeTarget = kind;
                    ShowOverlay();
                    overlay.SetHeadline("WideAgent - " + AppName(kind));
                    string r1 = Claude.Apply(kind);
                    Log.Write("--apply " + AppName(kind) + ": " + r1 + "  [" + Claude.Timing + "]");
                    HideOverlay(r1 == "OK" ? 300 : 2200);
                }
                if (!applied)
                {
                    ShowOverlay();
                    overlay.ShowGuidance("지원 앱 창 없음", "Claude 또는 ChatGPT를 먼저 실행하세요");
                    HideOverlay(2200);
                }
                return;
            }

            bool isNew;
            using (var mutex = new Mutex(true, "WideAgent_SingleInstance", out isNew))
            {
                if (!isNew)
                {
                    MessageBox.Show("이미 실행 중입니다. 트레이 아이콘을 확인하세요.",
                        "WideAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                overlay = new Overlay();
                Claude.Progress = OnProgress;
                BuildTray();

                wasClaudeReady = Claude.IsReady(AppKind.Claude);
                wasChatGPTReady = Claude.IsReady(AppKind.ChatGPT);
                if (wasClaudeReady)
                {
                    Log.Write("시작 - Claude가 이미 실행 중이라 적용을 시도한다");
                    RunApply(AppKind.Claude, 1500, "Claude 준비 대기", false);
                }
                if (wasChatGPTReady)
                {
                    Log.Write("시작 - ChatGPT가 이미 실행 중이라 적용을 시도한다");
                    RunApply(AppKind.ChatGPT, 1500, "ChatGPT 준비 대기", false);
                }
                if (!wasClaudeReady && !wasChatGPTReady)
                {
                    Log.Write("시작 - Claude 또는 ChatGPT 실행 대기 중");
                }

                timer = new System.Windows.Forms.Timer();
                timer.Interval = 1000;   // 재시도가 제때 걸리도록 점검 주기를 짧게 둔다
                timer.Tick += Poll;
                timer.Start();

                Application.Run();

                tray.Visible = false;
                tray.Dispose();
                GC.KeepAlive(mutex);
            }
        }

        static void OnProgress(string text, double p, bool error)
        {
            if (overlay == null) return;
            overlay.Step(text, p, error);
        }

        static string AppName(AppKind kind)
        {
            return kind == AppKind.Claude ? "Claude" : "ChatGPT";
        }

        static void ShowOverlay()
        {
            Win m = Claude.MainWindow(activeTarget);
            overlay.PlaceNear(m != null ? m.Handle : IntPtr.Zero);
            overlay.Step("준비 중", 0.02, false);
            overlay.Show();
            overlay.Refresh();
        }

        // 화면 없이 기다린다. 메시지는 계속 처리해서 트레이가 굳지 않게 한다.
        static void Idle(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
        }

        // NotifyIcon.Text 는 63자를 넘기면 예외를 던진다. 적용 결과 문자열이 길어지면
        // (예: "DevTools가 열리지 않음 (developer_settings.json 확인)") 여기서 터져서
        // 정작 중요한 오류 안내가 통째로 날아간다. 그래서 무조건 잘라서 넣는다.
        const int TrayTextMax = 63;

        static void SetTrayText(string text)
        {
            try
            {
                if (text == null) text = "";
                if (text.Length > TrayTextMax) text = text.Substring(0, TrayTextMax - 1) + "…";
                tray.Text = text;
            }
            catch { }
        }

        // 앱을 껐다 켜야 넓힐 수 있는 상태임을 알린다. 포커스를 뺏지 않아 쓰던 일이 끊기지 않는다.
        //
        // 풍선 알림에는 버튼을 달 수 없다. 대신 알림 자체가 클릭 대상이므로 그것을
        // 재시작 버튼으로 쓴다(BalloonTipClicked).
        //
        // 문구에 "직접 껐다 켜도 소용없다"를 반드시 넣는다. 없으면 '아 재시작하면
        // 되는구나' 하고 평범한 아이콘으로 다시 열게 되는데, 그래도 플래그가 없으니
        // 또 같은 알림이 뜬다. 그 반복에 빠지는 것이 이 알림의 제일 큰 위험이다.
        //
        // 근본 해결책인 바로가기는 처음부터 알린다. 두 번째부터 알릴 이유가 없다.
        // 한 번 겪을 일을 굳이 두 번 겪게 만드는 셈이다.
        static void NotifyRestartNeeded()
        {
            try
            {
                tray.BalloonTipTitle = "WideAgent - ChatGPT";
                tray.BalloonTipText =
                    "앱을 다시 시작해야 넓어집니다. 이 알림을 클릭하면 지금 다시 시작합니다.\r\n" +
                    "직접 껐다 켜는 것으로는 넓어지지 않습니다.\r\n" +
                    "트레이 메뉴에서 'ChatGPT (Wide)' 바로가기를 만들면 이 과정이 없습니다.";
                tray.BalloonTipIcon = ToolTipIcon.Info;
                restartOffered = true;
                tray.ShowBalloonTip(10000);
            }
            catch { }
        }

        // 2층으로 살아났을 때 띄운다. 넓어지긴 했으니 오류 아이콘은 쓰지 않는다.
        // 사용자에게는 '지금은 됐지만 앱이 바뀌었다'는 예고이고, 만든 사람에게는
        // 고치러 돌아올 신호다. 클릭하면 로그가 열린다. 찾아낸 이름이 거기 있다.
        //
        // 한 세션에 한 번만 띄운다. 적용할 때마다 뜨면 그냥 성가신 알림이 된다.
        static void NotifyFallbackUsed(string names)
        {
            try
            {
                tray.BalloonTipTitle = "WideAgent - ChatGPT 앱이 바뀌었습니다";
                tray.BalloonTipText =
                    "평소 쓰던 CSS 변수가 더 이상 먹지 않아 다른 이름을 찾아 넓혔습니다.\r\n" +
                    "지금은 정상이지만 다음 업데이트에서 안 될 수 있습니다.\r\n" +
                    "이 알림을 클릭하면 로그가 열립니다. (WideAgent 갱신이 필요합니다)";
                tray.BalloonTipIcon = ToolTipIcon.Warning;
                logOffered = true;
                tray.ShowBalloonTip(15000);
                Log.Write("알림 표시 - ChatGPT CSS 변수 이름이 바뀐 것으로 보임");
            }
            catch { }
        }

        // 적용 직후에 부른다. 알릴 일이 없으면 아무것도 하지 않는다.
        static void ReportFallbackIfAny()
        {
            string names = Claude.FallbackNames;
            if (names == null || fallbackNotified) return;
            fallbackNotified = true;
            NotifyFallbackUsed(names);
        }

        static void HideOverlay(int lingerMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < lingerMs)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            overlay.Hide();
        }

        // 바탕 화면에 ChatGPT 대체 바로가기를 만든다.
        //
        // MSIX 패키지 앱이라 시작 메뉴 항목은 편집할 수 있는 .lnk 가 아니라 AUMID 로 뜬다.
        // 그 경로로는 명령줄 인자를 붙일 방법이 없어서, 인자를 붙일 수 있는 우리 바로가기를
        // 따로 만들어 준다. 대상은 ChatGPT.exe 가 아니라 WideAgent.exe 다. 그래야 패키지
        // 업데이트로 설치 경로가 바뀌어도 바로가기를 다시 만들 필요가 없다.
        static void CreateChatGPTShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string link = Path.Combine(desktop, "ChatGPT (Wide).lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    MessageBox.Show("바로가기를 만들 수 없습니다 (Windows Script Host 없음)", "WideAgent");
                    return;
                }

                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { link });
                Type t = sc.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc,
                    new object[] { Application.ExecutablePath });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc,
                    new object[] { "--launch-chatgpt" });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                    new object[] { "ChatGPT를 넓은 대화창으로 연다" });

                string exe = Claude.ChatGPTExecutable;
                if (exe != null)
                    t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc,
                        new object[] { exe + ",0" });

                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Log.Write("바로가기 생성: " + link);

                MessageBox.Show(
                    "바탕 화면에 'ChatGPT (Wide)' 바로가기를 만들었습니다.\n\n" +
                    "이 바로가기로 ChatGPT를 열면 앱을 껐다 켜지 않고 바로 폭이 적용됩니다.\n" +
                    "우클릭해서 작업 표시줄이나 시작 메뉴에 고정해 두고 쓰면 됩니다.",
                    "WideAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Write("바로가기 생성 실패: " + ex.Message);
                MessageBox.Show("바로가기 생성 실패: " + ex.Message, "WideAgent");
            }
        }

        // 종료는 한 곳에서만 한다. 트레이 아이콘을 먼저 내리고(고스트 아이콘 방지),
        // 메시지 루프가 어떤 이유로든 끝나지 않더라도 프로세스는 반드시 내려가게 한다.
        static void Shutdown()
        {
            Log.Write("종료");
            try { if (timer != null) timer.Stop(); } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            try { if (overlay != null) { overlay.Hide(); overlay.Dispose(); } } catch { }
            Application.Exit();
            Environment.Exit(0);
        }

        static void BuildTray()
        {
            var menu = new ContextMenuStrip();

            var applyClaudeItem = new ToolStripMenuItem("Claude에 지금 적용");
            applyClaudeItem.Click += delegate
            {
                attempt = 0;
                retryPending = false;
                RunApply(AppKind.Claude, 0, null, true);
            };
            menu.Items.Add(applyClaudeItem);

            var applyChatGPTItem = new ToolStripMenuItem("ChatGPT에 지금 적용");
            applyChatGPTItem.Click += delegate
            {
                attempt = 0;
                retryPending = false;
                RunApply(AppKind.ChatGPT, 0, null, true);
            };
            menu.Items.Add(applyChatGPTItem);

            menu.Items.Add(new ToolStripSeparator());

            autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행");
            autoStartItem.CheckOnClick = true;
            autoStartItem.Checked = AutoStart.Enabled;
            autoStartItem.Click += delegate
            {
                try
                {
                    AutoStart.Set(autoStartItem.Checked);
                    Log.Write("자동 실행 " + (autoStartItem.Checked ? "켬" : "끔"));
                }
                catch (Exception ex)
                {
                    autoStartItem.Checked = AutoStart.Enabled;
                    MessageBox.Show("자동 실행 설정 실패: " + ex.Message, "WideAgent");
                }
            };
            menu.Items.Add(autoStartItem);

            var shortcutItem = new ToolStripMenuItem("ChatGPT 넓게 실행 바로가기 만들기");
            shortcutItem.Click += delegate { CreateChatGPTShortcut(); };
            menu.Items.Add(shortcutItem);

            var logItem = new ToolStripMenuItem("로그 열기");
            logItem.Click += delegate
            {
                try
                {
                    if (!File.Exists(Log.Path)) Log.Write("로그 열기");
                    Process.Start("notepad.exe", Log.Path);
                }
                catch { }
            };
            menu.Items.Add(logItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("종료");
            // 트레이 메뉴에서 Application.Exit() 을 그냥 부르면 종료되지 않는 때가 있다.
            // ContextMenuStrip 의 드롭다운도 내부적으로 Form 이라, 클릭을 처리하는 도중에는
            // 닫히기를 거부할 수 있고 그러면 Application.Exit() 이 통째로 취소된다.
            // 메뉴를 먼저 닫고, 적용 중이면 그것이 끝난 뒤로 미룬다.
            exitItem.Click += delegate
            {
                menu.Close();
                exiting = true;
                if (!busy) Shutdown();
            };
            menu.Items.Add(exitItem);

            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Text = "WideAgent";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate
            {
                attempt = 0;
                retryPending = false;
                AppKind kind = Claude.IsReady(AppKind.ChatGPT) ? AppKind.ChatGPT : AppKind.Claude;
                RunApply(kind, 0, null, true);
            };

            // 알림을 클릭하면 그 자리에서 재시작하고 넓힌다. 알림이 곧 재시작 버튼이다.
            //
            // 알림이 닫힐 때 표식을 지우지는 않는다. 시간이 지나 알림 센터로 넘어간
            // 뒤에 눌러도 동작해야 하기 때문이다. 표식은 실제로 적용에 성공했을 때 지운다.
            tray.BalloonTipClicked += delegate
            {
                // 2층 알림이었다면 로그를 연다. 찾아낸 변수 이름이 거기 적혀 있다.
                if (logOffered)
                {
                    logOffered = false;
                    try { Process.Start("notepad.exe", Log.Path); }
                    catch { }
                    return;
                }

                if (!restartOffered) return;
                restartOffered = false;
                Log.Write("알림 클릭 - ChatGPT 재시작 요청");
                attempt = 0;
                retryPending = false;
                RunApply(AppKind.ChatGPT, 0, null, true);
            };
        }

        // 1초마다 도는 감시 루프다. 여기서 예외가 새어 나가면 WinForms 가 오류
        // 대화상자를 띄우고 상주가 끝난다. 트레이 프로그램이 말없이 사라지면
        // 사용자는 이유를 알 수 없으므로, 무슨 일이 있어도 다음 점검은 계속 돈다.
        static void Poll(object sender, EventArgs e)
        {
            try { PollOnce(); }
            catch (Exception ex) { Log.Write("점검 예외: " + ex.Message); }
        }

        static void PollOnce()
        {
            if (busy) return;
            bool claudeReady = Claude.IsReady(AppKind.Claude);
            bool chatGPTReady = Claude.IsReady(AppKind.ChatGPT);

            if (retryPending && DateTime.Now >= retryAt)
            {
                retryPending = false;
                Log.Write(AppName(retryTarget) + " 재시도 " + attempt + "/" + MaxRetries);
                RunApply(retryTarget, RetryNoticeMs, "곧 다시 시도합니다", true);
            }
            else
            {
                if (claudeReady && !wasClaudeReady)
                {
                    Log.Write("Claude 실행 감지");
                    attempt = 0;
                    RunApply(AppKind.Claude, 2500, "Claude 로딩 대기", false);
                }
                if (chatGPTReady && !wasChatGPTReady && DateTime.Now >= chatGPTSelfRestartUntil)
                {
                    Log.Write("ChatGPT 실행 감지");
                    attempt = 0;
                    RunApply(AppKind.ChatGPT, 2500, "ChatGPT 로딩 대기", false);
                }
            }
            wasClaudeReady = claudeReady;
            wasChatGPTReady = chatGPTReady;
        }

        // force = 사용자가 직접 요청한 것(메뉴, 더블클릭). 이때는 확인 없이 무조건 적용한다.
        // 화면이 좁아 보여서 누른 것이므로, 기록을 믿고 건너뛰면 안 된다.
        static void RunApply(AppKind kind, int delayMs, string waitLabel, bool force)
        {
            if (busy) return;
            busy = true;
            activeTarget = kind;
            try
            {
                // 자동으로 도는 경우엔 오버레이를 띄우기 전에 조용히 살핀다.
                // 앱이 막 떴다면 페이지가 자리 잡을 때까지 기다려야 하는데,
                // 이 기다림도 화면 없이 한다. 필요 없는 일로 밝혀지면 아무것도 보이지 않는다.
                if (!force)
                {
                    if (delayMs > 0) Idle(delayMs);
                    if (!Claude.NeedsApply(kind))
                    {
                        Log.Write(AppName(kind) + " 이미 넓음 - 건너뜀");
                        SetTrayText("WideAgent - " + AppName(kind) + " - 이미 넓음");
                        attempt = 0;
                        retryPending = false;
                        return;
                    }

                    // ChatGPT는 연결만 살아 있으면 키보드도 포커스도 쓰지 않는다.
                    // 그러니 오버레이를 띄울 이유가 없다. 조용히 끝낸다.
                    // 연결이 없으면 껐다 켜야만 붙는데, 자동으로는 하지 않고 알리기만 한다.
                    if (kind == AppKind.ChatGPT)
                    {
                        string rq;
                        Claude.AllowChatGPTRestart = false;
                        try { rq = Claude.Apply(kind); }
                        finally { Claude.AllowChatGPTRestart = true; }

                        if (rq == Claude.RestartNeededMsg)
                        {
                            Log.Write("ChatGPT 재시작이 필요함 - 자동으로는 하지 않음");
                            SetTrayText("WideAgent - ChatGPT - 알림을 클릭하면 넓어집니다");
                            NotifyRestartNeeded();
                        }
                        else
                        {
                            Log.Write("ChatGPT 적용 결과: " + rq + "  [" + Claude.Timing + "]");
                            SetTrayText("WideAgent - ChatGPT - " + rq);
                            restartOffered = false;
                            ReportFallbackIfAny();
                        }
                        attempt = 0;
                        retryPending = false;
                        return;
                    }
                }

                ShowOverlay();
                overlay.SetHeadline(attempt > 0
                    ? "WideAgent - " + AppName(kind) + " 재시도 (" + attempt + "/" + MaxRetries + ")"
                    : "WideAgent - " + AppName(kind));

                if (force && delayMs > 0)
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < delayMs)
                    {
                        int left = (int)Math.Ceiling((delayMs - sw.ElapsedMilliseconds) / 1000.0);
                        overlay.Step(waitLabel + " · " + left + "초",
                            0.02 + 0.04 * sw.ElapsedMilliseconds / delayMs, false);
                        Application.DoEvents();
                        Thread.Sleep(30);
                    }
                }

                string r = Claude.Apply(kind);
                if (Claude.Restarted)
                    chatGPTSelfRestartUntil = DateTime.Now.AddMilliseconds(SelfRestartQuietMs);
                Log.Write(AppName(kind) + " 적용 결과: " + r + "  [" + Claude.Timing + "]");
                SetTrayText("WideAgent - " + AppName(kind) + " - " + r);
                ReportFallbackIfAny();

                if (r == "OK")
                {
                    if (kind == AppKind.ChatGPT) restartOffered = false;
                    attempt = 0;
                    retryPending = false;
                    HideOverlay(300);
                }
                else if (r == Claude.FocusMsg && attempt < MaxRetries)
                {
                    // 사용자가 뭔가 하는 중이다. 조금 기다렸다가 다시.
                    attempt++;
                    retryPending = true;
                    retryTarget = kind;
                    retryAt = DateTime.Now.AddMilliseconds(RetryGapMs);
                    overlay.Step(r + " · " + RetryTotalSec + "초 뒤 재시도", 1, true);
                    HideOverlay(800);
                }
                else
                {
                    // 마지막 재시도까지 실패했다. 어떻게 다시 시도하는지 알려 주고 물러난다.
                    attempt = 0;
                    retryPending = false;
                    overlay.ShowGuidance(r, RetryGuide);
                    Log.Write("포기 - " + RetryGuide);
                    HideOverlay(6000);
                }
            }
            catch (Exception ex)
            {
                Log.Write("예외: " + ex.Message);
                try { HideOverlay(2000); } catch { }
            }
            finally
            {
                busy = false;
                if (exiting) Shutdown();
            }
        }
    }
}


