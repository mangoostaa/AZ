using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BFEsp
{
    // Overlay Win32 puro (sin WinForms): topmost, click-through, color-key magenta.
    // WCA_EXCLUDEFROMCAPTURE -> no aparece en capturas (OBS display, Discord, etc), pero sí en tu monitor.
    public static class Overlay
    {
        public class EspEntry
        {
            public float X, Y, W, H;      // caja 2D en pantalla
            public float Hp;               // 0..1
            public string Name = "";
            public string Weapon = "";
            public float Dist;
            public uint BoxColor;         // COLORREF 0x00BBGGRR
            public bool Team;
        }

        private const uint KEY = 0x00FF00FF; // magenta = transparente

        // ---- escena compartida (Unity escribe / overlay lee) ----
        private static readonly object _gate = new object();
        private static EspEntry[] _entries = Array.Empty<EspEntry>();
        private static string _hud = "";
        private static bool _fovOn; private static int _fovR;
        private static volatile bool _dirty = true;   // repintar solo cuando cambie la escena

        public static void SetScene(List<EspEntry> entries, string hud, bool fov, int fovRadius)
        {
            lock (_gate)
            {
                _entries = entries != null ? entries.ToArray() : Array.Empty<EspEntry>();
                _hud = hud ?? ""; _fovOn = fov; _fovR = fovRadius;
                _dirty = true;   // hay datos nuevos → el hilo del overlay repinta
            }
        }

        public static bool CaptureExcluded { get; private set; }

        public static void Start(Action<string> log)
        {
            if (_thread != null && _thread.IsAlive) return;
            _log = log ?? _log;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "BfOverlay" };
            _thread.Start();
        }
        public static void Stop()
        {
            _running = false;
            try { _thread?.Join(1500); } catch { }
            _thread = null;
        }

        private static Action<string> _log = s => { };
        private static volatile bool _running;
        private static Thread _thread;

        // ---------------- win32 ----------------
        private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        private static readonly WndProc _proc = WndProcImpl;
        private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l) { return DefWindowProcW(h, m, w, l); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSW
        {
            public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
            public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
        private delegate bool EnumCb(IntPtr h, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASSW c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint mn, uint mx, uint rm);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string m);

        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint c);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr b);
        [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int w, uint c);
        [DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr dc, int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern bool Ellipse(IntPtr dc, int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int e, int o, int weight, uint it, uint un, int st, int cs, uint op, uint cp, uint q, uint pf, string face);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr dc, uint c);
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint flags);

        private static uint CR(int r, int g, int b) { return (uint)(r | (g << 8) | (b << 16)); }

        private static IntPtr _hwnd, _bmp;
        private static int _w, _h, _gx, _gy; private static bool _bmpOk;
        private static float _lastTop;
        private static bool _classOk;
        private static bool _loggedOnce;
        private static IntPtr _fnt = IntPtr.Zero;   // fuente cacheada: NO crear/destruir cada frame

        private static void Run()
        {
            // la ventana del juego puede no existir todavía al arrancar → reintentar hasta encontrarla
            IntPtr game = IntPtr.Zero;
            while (_running && game == IntPtr.Zero)
            {
                game = FindGameWindow();
                if (game == IntPtr.Zero) Thread.Sleep(1000);
            }
            if (game == IntPtr.Zero) { _log("overlay: shutdown antes de encontrar la ventana"); return; }
            _log("overlay: ventana del juego encontrada hwnd=0x" + game.ToInt64().ToString("X"));
            try
            {
                if (!_classOk)
                {
                    var wc = new WNDCLASSW { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = "BfOverlayWin" };
                    RegisterClassW(ref wc); _classOk = true;
                }
                uint ex = 0x80000 | 0x20 | 0x8 | 0x80 | 0x08000000;
                _hwnd = CreateWindowExW(ex, "BfOverlayWin", "", 0x80000000 | 0x10000000, 0, 0, 64, 64, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                SetLayeredWindowAttributes(_hwnd, KEY, 255, 1 /*LWA_COLORKEY*/);

                CaptureExcluded = SetWindowDisplayAffinity(_hwnd, 0x11);
                _log("overlay: EXCLUDEFROMCAPTURE=" + (CaptureExcluded ? "OK (invisible al grabar)" : "FALLO (Win10 2004+ necesario)"));

                // fuente una sola vez, reutilizada en todos los frames
                _fnt = CreateFontW(-13, 0, 0, 0, 700, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 5 /*CLEARTYPE*/, 0, "Arial");

                while (_running)
                {
                    if (!IsWindow(game))   // ventana recreada / partida cerrada
                    {
                        game = FindGameWindow();
                        if (game == IntPtr.Zero) { Thread.Sleep(1000); continue; }
                    }
                    while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1)) DispatchMessageW(ref m);
                    Track(game);
                    Render();   // solo pinta si _dirty (escena nueva / resize)
                    if (Time32() - _lastTop > 3000) { _lastTop = Time32(); SetWindowPos(_hwnd, (IntPtr)(-1), 0, 0, 0, 0, 0x13); }
                    Thread.Sleep(15);   // antes 8 → max ~60 Hz de bucle, y con dirty-flag el trabajo real es 20 Hz
                }
                if (_fnt != IntPtr.Zero) { DeleteObject(_fnt); _fnt = IntPtr.Zero; }
                DestroyWindow(_hwnd);
            }
            catch (Exception e) { _log("overlay ex: " + e.Message); }
            finally { _running = false; _hwnd = IntPtr.Zero; }
        }
        private static uint Time32() { return (uint)Environment.TickCount; }

        private static void Track(IntPtr game)
        {
            GetClientRect(game, out var cr);
            var p = new POINT();
            ClientToScreen(game, ref p);
            int w = Math.Max(cr.R - cr.L, 8), h = Math.Max(cr.B - cr.T, 8);
            if (w != _w || h != _h || p.X != _gx || p.Y != _gy)
            {
                _w = w; _h = h; _gx = p.X; _gy = p.Y; _bmpOk = false;
                SetWindowPos(_hwnd, (IntPtr)(-1), p.X, p.Y, w, h, 0x40 /*SWP_SHOWWINDOW*/);
                _dirty = true;   // resize/move → repintar con el nuevo tamaño
            }
        }

        // elegir la ventana MÁS GRANDE de nuestro proceso, saltando la consola de MelonLoader
        private static uint _wantPid; private static long _bestArea; private static IntPtr _found;
        private static readonly EnumCb _findCb = FindCb;
        private static IntPtr FindGameWindow()
        {
            try
            {
                _wantPid = (uint)Process.GetCurrentProcess().Id; _found = IntPtr.Zero; _bestArea = 0;
                EnumWindows(_findCb, IntPtr.Zero);
                return _found;
            }
            catch { return IntPtr.Zero; }
        }
        // UN solo FindCb: siempre devuelve true (seguir iterando) y se queda con la ventana más grande
        private static bool FindCb(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != _wantPid) return true;
            // saltar la consola de MelonLoader
            var cn = new StringBuilder(64);
            GetClassNameW(h, cn, 64);
            if (cn.ToString() == "ConsoleWindowClass") return true;
            GetClientRect(h, out var r);
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > _bestArea && r.R - r.L > 300) { _bestArea = area; _found = h; }
            return true;
        }

        // ---------------- render GDI ----------------
        private static void Render()
        {
            if (_hwnd == IntPtr.Zero || _w <= 8) return;
            if (!_dirty) return;   // nada cambió → 0 trabajo, 0 BitBlt, 0 GDI allocations

            IntPtr dc = GetDC(_hwnd); if (dc == IntPtr.Zero) return;
            IntPtr mdc = CreateCompatibleDC(dc);
            if (!_bmpOk) { if (_bmp != IntPtr.Zero) DeleteObject(_bmp); _bmp = CreateCompatibleBitmap(dc, _w, _h); _bmpOk = true; }
            SelectObject(mdc, _bmp);

            if (_fnt == IntPtr.Zero) _fnt = CreateFontW(-13, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Arial");
            IntPtr of = SelectObject(mdc, _fnt);
            SetBkMode(mdc, 1 /*TRANSPARENT*/);

            // fondo = color key (transparente)
            IntPtr bg = CreateSolidBrush(KEY);
            var full = new RECT { L = 0, T = 0, R = _w, B = _h };
            FillRect(mdc, ref full, bg); DeleteObject(bg);

            EspEntry[] es; string hud; bool fov; int fovR;
            lock (_gate) { es = _entries; hud = _hud; fov = _fovOn; fovR = _fovR; }

            if (!string.IsNullOrEmpty(hud)) Txt(mdc, hud, 10, 8, 600, CR(255, 255, 255));

            for (int i = 0; i < es.Length; i++)
            {
                var e = es[i];
                int x = (int)e.X, y = (int)e.Y, w = Math.Max((int)e.W, 6), h = Math.Max((int)e.H, 10);
                Box(mdc, x - 1, y - 1, x + w + 1, y + h + 1, CR(0, 0, 0), 1);          // contorno negro
                Box(mdc, x, y, x + w, y + h, e.BoxColor, 2);                        // caja
                // vida vertical
                Fill(mdc, x - 7, y - 1, 4, h + 2, CR(20, 20, 20));
                int fh = (int)(h * Math.Max(0f, Math.Min(1f, e.Hp)));
                Fill(mdc, x - 7, y + (h - fh), 4, fh, CR((int)(255 * (1 - e.Hp)), (int)(255 * e.Hp), 30));
                // nombre / arma+dist
                Txt(mdc, e.Name, x + w / 2 - 120, y - 18, 240, CR(255, 220, 90));
                string sub = e.Dist.ToString("F0") + "m" + (e.Weapon.Length > 0 ? "  " + e.Weapon : "");
                Txt(mdc, sub, x + w / 2 - 120, y + h + 2, 240, CR(180, 220, 255));
            }

            if (fov && fovR > 2)
            {
                var pen = CreatePen(0, 1, CR(255, 255, 255));
                IntPtr op = SelectObject(mdc, pen); IntPtr ob = SelectObject(mdc, GetStockObject(5 /*NULL_BRUSH*/));
                Ellipse(mdc, _w / 2 - fovR, _h / 2 - fovR, _w / 2 + fovR, _h / 2 + fovR);
                SelectObject(mdc, ob); SelectObject(mdc, op); DeleteObject(pen);
            }

            SelectObject(mdc, of);
            BitBlt(dc, 0, 0, _w, _h, mdc, 0, 0, 0x00CC0020 /*SRCCOPY*/);
            DeleteDC(mdc); ReleaseDC(_hwnd, dc);
            _dirty = false;   // pintado; no repetir hasta la próxima escena nueva
        }

        private static void Box(IntPtr dc, int l, int t, int r, int b, uint col, int wpx)
        {
            var pen = CreatePen(0, wpx, col);
            IntPtr op = SelectObject(dc, pen); IntPtr ob = SelectObject(dc, GetStockObject(5));
            Rectangle(dc, l, t, r, b);
            SelectObject(dc, ob); SelectObject(dc, op); DeleteObject(pen);
        }
        private static void Fill(IntPtr dc, int x, int y, int w, int h, uint col)
        {
            if (w <= 0 || h <= 0) return;
            var br = CreateSolidBrush(col);
            var r = new RECT { L = x, T = y, R = x + w, B = y + h };
            FillRect(dc, ref r, br); DeleteObject(br);
        }
        private static void Txt(IntPtr dc, string s, int x, int y, int w, uint col)
        {
            if (string.IsNullOrEmpty(s)) return;
            SetTextColor(dc, col);
            var r = new RECT { L = x, T = y, R = x + w, B = y + 16 };
            DrawTextW(dc, s, s.Length, ref r, 0x821 /*CENTER|SINGLELINE|NOPREFIX*/);
        }
    }
}
