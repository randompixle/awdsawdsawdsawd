using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

class XPAgent
{
    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint WM_HOTKEY = 0x0312;
    const int HOTKEY_ID = 1;

    static string Host = Environment.GetEnvironmentVariable("XP_HOST") ?? "192.168.232.1";
    static int Port = int.Parse(Environment.GetEnvironmentVariable("XP_PORT") ?? "5000");
    static int IntervalMs = int.Parse(Environment.GetEnvironmentVariable("XP_INTERVAL_MS") ?? "1000");
    static int CmdPort = int.Parse(Environment.GetEnvironmentVariable("XP_CMD_PORT") ?? "6001");
    static volatile bool Paused = false;

    [STAThread]
    static void Main()
    {
        Console.WriteLine("XPAgent starting. Host={0}:{1}", Host, Port);
        StartHotkeyListener();
        StartCommandListener();
        while (true)
        {
            try
            {
                if (Paused)
                {
                    Thread.Sleep(IntervalMs);
                    continue;
                }
                byte[] png = CaptureScreenPng();
                string response = PostFrame(png);
                if (!ExecuteActionsJson(response))
                {
                    ExecuteActionsText(response);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
            Thread.Sleep(IntervalMs);
        }
    }

    static void StartCommandListener()
    {
        Thread t = new Thread(() =>
        {
            TcpListener listener = new TcpListener(IPAddress.Any, CmdPort);
            listener.Start();
            Console.WriteLine("Command listener on port {0}", CmdPort);
            while (true)
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            ExecuteActionsText(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Cmd listener error: " + ex.Message);
                    Thread.Sleep(200);
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static void StartHotkeyListener()
    {
        Thread t = new Thread(() =>
        {
            if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, 0, (uint)Keys.F12))
            {
                Console.WriteLine("Hotkey registration failed.");
                return;
            }
            Console.WriteLine("Press F12 to toggle pause.");
            try
            {
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) != 0)
                {
                    if (msg.message == WM_HOTKEY && msg.wParam == (UIntPtr)HOTKEY_ID)
                    {
                        Paused = !Paused;
                        Console.WriteLine(Paused ? "Paused." : "Resumed.");
                    }
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static byte[] CaptureScreenPng()
    {
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        using (Bitmap bmp = new Bitmap(bounds.Width, bounds.Height))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static string PostFrame(byte[] png)
    {
        string url = "http://" + Host + ":" + Port + "/frame";
        string boundary = "----XPBOUNDARY" + DateTime.Now.Ticks.ToString("x");
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        string title = GetActiveWindowTitle();
        byte[] widthField = Encoding.ASCII.GetBytes(
            "--" + boundary + "\r\n" +
            "Content-Disposition: form-data; name=\"width\"\r\n\r\n" +
            bounds.Width.ToString() + "\r\n"
        );
        byte[] heightField = Encoding.ASCII.GetBytes(
            "--" + boundary + "\r\n" +
            "Content-Disposition: form-data; name=\"height\"\r\n\r\n" +
            bounds.Height.ToString() + "\r\n"
        );
        byte[] titleField = Encoding.ASCII.GetBytes(
            "--" + boundary + "\r\n" +
            "Content-Disposition: form-data; name=\"active_title\"\r\n\r\n" +
            title + "\r\n"
        );
        byte[] header = Encoding.ASCII.GetBytes(
            "--" + boundary + "\r\n" +
            "Content-Disposition: form-data; name=\"frame\"; filename=\"screen.png\"\r\n" +
            "Content-Type: image/png\r\n\r\n"
        );
        byte[] footer = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "POST";
        req.ContentType = "multipart/form-data; boundary=" + boundary;
        req.KeepAlive = false;

        using (Stream reqStream = req.GetRequestStream())
        {
            reqStream.Write(widthField, 0, widthField.Length);
            reqStream.Write(heightField, 0, heightField.Length);
            reqStream.Write(titleField, 0, titleField.Length);
            reqStream.Write(header, 0, header.Length);
            reqStream.Write(png, 0, png.Length);
            reqStream.Write(footer, 0, footer.Length);
        }

        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
        {
            return reader.ReadToEnd();
        }
    }

    class ActionItem
    {
        public string cmd;
        public int x;
        public int y;
        public string button;
        public string text;
        public string key;
        public int ms;
    }

    class ActionEnvelope
    {
        public List<ActionItem> actions;
    }

    static bool ExecuteActionsJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ActionEnvelope env = ser.Deserialize<ActionEnvelope>(text);
            if (env == null || env.actions == null) return false;
            foreach (ActionItem item in env.actions)
            {
                ExecuteActionItem(item);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void ExecuteActionItem(ActionItem item)
    {
        if (item == null || item.cmd == null) return;
        string cmd = item.cmd.ToLower();
        switch (cmd)
        {
            case "move":
                SetCursorPos(item.x, item.y);
                break;
            case "click":
                if (!string.IsNullOrEmpty(item.button) && item.button.ToLower().StartsWith("right"))
                    RightClick();
                else
                    LeftClick();
                break;
            case "dblclick":
                LeftClick();
                Thread.Sleep(80);
                LeftClick();
                break;
            case "rclick":
                RightClick();
                break;
            case "type":
                if (!string.IsNullOrEmpty(item.text)) SendKeys.SendWait(item.text);
                break;
            case "key":
                if (!string.IsNullOrEmpty(item.key)) SendSpecialKey(item.key);
                break;
            case "sleep":
                if (item.ms > 0) Thread.Sleep(item.ms);
                break;
            case "focus":
                if (!string.IsNullOrEmpty(item.text)) FocusWindowByTitle(item.text);
                break;
            case "noop":
            default:
                break;
        }
    }

    static void ExecuteActionsText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string[] lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLower();
            string arg = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "move":
                    {
                        string[] xy = arg.Split(' ');
                        if (xy.Length >= 2)
                        {
                            int x = int.Parse(xy[0]);
                            int y = int.Parse(xy[1]);
                            SetCursorPos(x, y);
                        }
                        break;
                    }
                case "click":
                    {
                        if (arg.ToLower().StartsWith("right"))
                            RightClick();
                        else
                            LeftClick();
                        break;
                    }
                case "dblclick":
                    {
                        LeftClick();
                        Thread.Sleep(80);
                        LeftClick();
                        break;
                    }
                case "rclick":
                    RightClick();
                    break;
                case "type":
                    if (arg.Length > 0) SendKeys.SendWait(arg);
                    break;
                case "key":
                    SendSpecialKey(arg);
                    break;
                case "sleep":
                    {
                        int ms;
                        if (int.TryParse(arg, out ms)) Thread.Sleep(ms);
                        break;
                    }
                case "focus":
                    if (arg.Length > 0) FocusWindowByTitle(arg);
                    break;
                case "noop":
                default:
                    break;
            }
        }
    }

    static void LeftClick()
    {
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    static void RightClick()
    {
        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
    }

    static void SendSpecialKey(string name)
    {
        string key = name.ToUpper();
        if (key == "ENTER") TapKey(0x0D);
        else if (key == "TAB") TapKey(0x09);
        else if (key == "ESC") TapKey(0x1B);
        else if (key == "ALT+F4") { KeyDown(0x12); TapKey(0x73); KeyUp(0x12); }
        else if (key == "CTRL+L") { KeyDown(0x11); TapKey(0x4C); KeyUp(0x11); }
        else if (key == "WIN+R") { KeyDown(0x5B); TapKey(0x52); KeyUp(0x5B); }
    }

    static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static void KeyDown(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
    }

    static void KeyUp(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static string GetActiveWindowTitle()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        StringBuilder sb = new StringBuilder(256);
        int len = GetWindowText(hwnd, sb, sb.Capacity);
        if (len <= 0) return "";
        return sb.ToString();
    }

    static void FocusWindowByTitle(string needle)
    {
        string target = needle.ToLower();
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;
            StringBuilder sb = new StringBuilder(256);
            int len = GetWindowText(hWnd, sb, sb.Capacity);
            if (len <= 0) return true;
            string title = sb.ToString();
            if (title.ToLower().Contains(target))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            SetForegroundWindow(found);
            Thread.Sleep(200);
        }
    }
}
