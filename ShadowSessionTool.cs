using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.DirectoryServices;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShadowSessionTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], ServerCacheCleanup.AutoRunArg, StringComparison.OrdinalIgnoreCase))
            {
                ServerCacheCleanup.Run(true);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    internal enum AppTheme
    {
        Light,
        Dark,
        Blue
    }

    internal static class Wts
    {
        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WtsInfoClass wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        internal static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool WTSSendMessage(IntPtr hServer, int sessionId, string pTitle, int titleLength, string pMessage, int messageLength, int style, int timeout, out int pResponse, bool bWait);

        internal enum WtsInfoClass
        {
            WTSUserName = 5,
            WTSWinStationName = 6
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WTS_SESSION_INFO
        {
            public int SessionID;
            public IntPtr pWinStationName;
            public WtsConnectState State;
        }
    }

    internal class RdpSession
    {
        public string UserName;
        public string SessionName;
        public string Id;
        public string State;
        public WtsConnectState RawState;
        public int OneCCount;
        public string Description;
    }

    internal class ListViewItemComparer : System.Collections.IComparer
    {
        private readonly int _column;
        private readonly SortOrder _order;

        public ListViewItemComparer(int column, SortOrder order)
        {
            _column = column;
            _order = order;
        }

        public int Compare(object x, object y)
        {
            ListViewItem itemX = (ListViewItem)x;
            ListViewItem itemY = (ListViewItem)y;

            string textX = itemX.SubItems.Count > _column ? itemX.SubItems[_column].Text : "";
            string textY = itemY.SubItems.Count > _column ? itemY.SubItems[_column].Text : "";

            int result;
            int numX, numY;
            if (int.TryParse(textX, out numX) && int.TryParse(textY, out numY))
            {
                result = numX.CompareTo(numY);
            }
            else
            {
                result = string.Compare(textX, textY, StringComparison.CurrentCultureIgnoreCase);
            }

            return _order == SortOrder.Descending ? -result : result;
        }
    }

    internal static class ServerCacheCleanup
    {
        internal const string TaskName = "ShadowSessionTool_CleanServerCache1C";
        internal const string AutoRunArg = "/autoclean1c";

        private static readonly string[] ServiceNames =
        {
            "1C:Enterprise 8.3 Server Agent (x86-64)",
            "BAF Server Agent (x86-64)"
        };

        private static readonly Dictionary<string, string> ServiceSrvInfoPaths = new Dictionary<string, string>
        {
            { "1C:Enterprise 8.3 Server Agent (x86-64)", @"C:\Program Files\1cv8\srvinfo" },
            { "BAF Server Agent (x86-64)", @"C:\Program Files\BAF\srvinfo" }
        };

        private static readonly Regex GuidRegex = new Regex(
            "^[a-f0-9]{8}-([a-f0-9]{4}-){3}[a-f0-9]{12}$", RegexOptions.IgnoreCase);

        internal class CleanupResult
        {
            public readonly List<string> ServicesProcessed = new List<string>();
            public readonly List<string> FoldersDeleted = new List<string>();
            public readonly List<string> Errors = new List<string>();
        }

        internal static CleanupResult Run(bool writeLog)
        {
            CleanupResult result = new CleanupResult();
            List<string> stoppedServices = new List<string>();

            foreach (string svcName in ServiceNames)
            {
                ServiceController sc = TryGetService(svcName);
                if (sc == null) continue; // служба не встановлена на цьому сервері

                try
                {
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        // встановлена, але зараз не запущена - не чіпаємо й не запускаємо її потім
                        continue;
                    }

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    stoppedServices.Add(svcName);
                    result.ServicesProcessed.Add(svcName + ": зупинено");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Не вдалося зупинити службу \"" + svcName + "\": " + ex.Message);
                }
            }

            if (stoppedServices.Count == 0)
            {
                result.Errors.Add("Жодна відома служба 1С/BAF наразі не запущена на цьому сервері.");
                if (writeLog) WriteLog(result, stoppedServices);
                return result;
            }

            System.Threading.Thread.Sleep(7000);

            foreach (string svcName in stoppedServices)
            {
                string root;
                if (!ServiceSrvInfoPaths.TryGetValue(svcName, out root)) continue;
                if (!Directory.Exists(root)) continue;

                try
                {
                    string[] allDirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
                    foreach (string dir in allDirs)
                    {
                        string name = Path.GetFileName(dir);
                        bool matches = GuidRegex.IsMatch(name) ||
                            name.StartsWith("snccnt", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "STT", StringComparison.OrdinalIgnoreCase);
                        if (!matches) continue;
                        if (!Directory.Exists(dir)) continue;

                        try
                        {
                            Directory.Delete(dir, true);
                            result.FoldersDeleted.Add(dir);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add("Не вдалося видалити \"" + dir + "\": " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Помилка обходу \"" + root + "\": " + ex.Message);
                }
            }

            foreach (string svcName in stoppedServices)
            {
                ServiceController sc = TryGetService(svcName);
                if (sc == null) continue;

                try
                {
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                    }
                    result.ServicesProcessed.Add(svcName + ": запущено");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Не вдалося запустити службу \"" + svcName + "\": " + ex.Message);
                }
            }

            if (writeLog) WriteLog(result, stoppedServices);
            return result;
        }

        private static ServiceController TryGetService(string name)
        {
            try
            {
                ServiceController sc = new ServiceController(name);
                ServiceControllerStatus probe = sc.Status;
                return sc;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteLog(CleanupResult result, List<string> stoppedServices)
        {
            try
            {
                string dir = @"C:\Scripts\1C_Maintenance\Logs";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "Cleanup_" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(ts + " [INFO] Запущено ShadowSessionTool: очищення серверного кешу 1С/BAF");
                foreach (string s in result.ServicesProcessed) sb.AppendLine(ts + " [INFO] " + s);
                foreach (string f in result.FoldersDeleted) sb.AppendLine(ts + " [INFO] Видалено: " + f);
                foreach (string e in result.Errors) sb.AppendLine(ts + " [ERROR] " + e);
                sb.AppendLine(ts + " [INFO] Завершено");

                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // логування не критичне
            }
        }
    }

    internal class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private const int EM_SETCUEBANNER = 0x1501;

        private const string RegPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        private const string RegName = "Shadow";
        private const int DesiredValue = 2;
        private const string UserRegPath = @"Software\ShadowSessionTool";

        private const string AppVersion = "1.5.0";

        private static readonly string[] MessageTemplates =
        {
            "Необхідно завершити роботу в 1С/BAS для оновлення.",
            "Можна працювати.",
            "Сервер буде перезавантажений через X хвилин."
        };
        private const string UpdateVersionUrl = "https://raw.githubusercontent.com/Pro100NeFarT/ShadowSessionTool/main/version.txt";
        private const string UpdateExeUrl = "https://raw.githubusercontent.com/Pro100NeFarT/ShadowSessionTool/main/ShadowSessionTool.exe";

        private Label lblVersion;
        private Button btnThemeLight;
        private Button btnThemeDark;
        private Button btnThemeBlue;
        private ToolTip themeToolTip;
        private AppTheme _currentTheme = AppTheme.Blue;

        private ListView lvSessions;
        private Button btnRefresh;
        private Button btnDisconnect;
        private Button btnDisconnectAll;
        private Button btnReboot;
        private ContextMenuStrip rebootMenu;
        private Button btnServerCache;
        private ContextMenuStrip serverCacheMenu;
        private Panel pnlIndicator;
        private Label lblStatus;
        private Button btnEnablePolicy;
        private Label lblHint;
        private Label lblSelect;
        private TextBox txtSearch;
        private Label lblExternalIpCaption;
        private Label lblExternalIp;
        private readonly List<Label> _localIpLabels = new List<Label>();

        private ContextMenuStrip ctxMenu;
        private ToolStripMenuItem miCtxConnect;
        private ToolStripMenuItem miCtxTakeOver;
        private ToolStripMenuItem miCtxDisconnect;
        private ToolStripMenuItem miCtxEnd1C;
        private ToolStripMenuItem miCtxClearCache1C;
        private ToolStripMenuItem miCtxMessage;
        private ToolStripMenuItem miCtxMessageAll;

        private List<RdpSession> _allSessions = new List<RdpSession>();
        private readonly Dictionary<string, string> _descriptionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int _sortColumn = -1;
        private SortOrder _sortOrder = SortOrder.None;
        private readonly int _ownSessionId = Process.GetCurrentProcess().SessionId;
        private string _pendingUpdateVersion;
        private string _externalIp;

        public MainForm()
        {
            InitializeComponent();
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5)
                {
                    RefreshSessions();
                    RefreshPolicyStatus();
                }
            };
            Shown += (s, e) =>
            {
                SendMessage(txtSearch.Handle, EM_SETCUEBANNER, IntPtr.Zero, "Пошук за користувачем/описом...");
                ApplyTheme(LoadSavedTheme());
                RefreshSessions();
                RefreshPolicyStatus();
                CheckForUpdatesAsync();
                ShowLocalIp();
                FetchExternalIpAsync();
            };
        }

        private void InitializeComponent()
        {
            Text = "Засіб тіньових сеансів";
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(700, 674);
            MinimumSize = new Size(660, 594);
            StartPosition = FormStartPosition.CenterScreen;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // без іконки застосунок все одно працює коректно
            }

            lblVersion = new Label
            {
                Text = "v" + AppVersion,
                AutoSize = true,
                Location = new Point(12, 12),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8F, FontStyle.Underline)
            };
            lblVersion.Click += (s, e) =>
            {
                if (_pendingUpdateVersion != null) PromptUpdate(_pendingUpdateVersion);
                else CheckForUpdatesAsync(true);
            };

            themeToolTip = new ToolTip();

            btnThemeLight = new Button { Text = "", Size = new Size(20, 20), Location = new Point(608, 9), BackColor = Color.White, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnThemeLight.FlatAppearance.BorderColor = Color.Gray;
            btnThemeLight.Click += (s, e) => ApplyTheme(AppTheme.Light);
            themeToolTip.SetToolTip(btnThemeLight, "Світла тема");

            btnThemeDark = new Button { Text = "", Size = new Size(20, 20), Location = new Point(638, 9), BackColor = Color.FromArgb(32, 32, 32), FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnThemeDark.FlatAppearance.BorderColor = Color.Gray;
            btnThemeDark.Click += (s, e) => ApplyTheme(AppTheme.Dark);
            themeToolTip.SetToolTip(btnThemeDark, "Темна тема");

            btnThemeBlue = new Button { Text = "", Size = new Size(20, 20), Location = new Point(668, 9), BackColor = Color.FromArgb(70, 130, 220), FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnThemeBlue.FlatAppearance.BorderColor = Color.Gray;
            btnThemeBlue.Click += (s, e) => ApplyTheme(AppTheme.Blue);
            themeToolTip.SetToolTip(btnThemeBlue, "Синя тема");

            lblSelect = new Label
            {
                Text = "Виберіть сеанс для підключення:",
                AutoSize = true,
                MaximumSize = new Size(140, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(12, 35)
            };

            const int btnW = 165, btnH = 28, colB = 348, colRight = 523;

            btnRefresh = new Button { Text = "", Size = new Size(40, 28), Location = new Point(298, 40), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnRefresh.Click += (s, e) => { RefreshSessions(); RefreshPolicyStatus(); };
            themeToolTip.SetToolTip(btnRefresh, "Оновити (F5)");

            btnServerCache = new Button
            {
                Text = "Обслуговування 1С ▾",
                Size = new Size(btnW, btnH),
                Location = new Point(colB, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            ToolStripMenuItem miDoServerCleanup = new ToolStripMenuItem("Очистити серверний кеш 1С");
            miDoServerCleanup.Click += MiServerCacheCleanup_Click;
            ToolStripMenuItem miScheduleServerCleanup = new ToolStripMenuItem("Запланувати очищення...");
            miScheduleServerCleanup.Click += MiScheduleCacheCleanup_Click;
            ToolStripMenuItem miCancelScheduleServerCleanup = new ToolStripMenuItem("Скасувати заплановане");
            miCancelScheduleServerCleanup.Click += MiCancelScheduledCleanup_Click;

            serverCacheMenu = new ContextMenuStrip();
            serverCacheMenu.Items.Add(miDoServerCleanup);
            serverCacheMenu.Items.Add(new ToolStripSeparator());
            serverCacheMenu.Items.Add(miScheduleServerCleanup);
            serverCacheMenu.Items.Add(miCancelScheduleServerCleanup);

            btnServerCache.Click += (s, e) => serverCacheMenu.Show(btnServerCache, new Point(0, btnServerCache.Height));

            btnReboot = new Button
            {
                Text = "Перезавантаження ▾",
                Size = new Size(btnW, btnH),
                Location = new Point(colRight, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            pnlIndicator = new Panel
            {
                Size = new Size(16, 16),
                Location = new Point(12, 80),
                BackColor = Color.Gray,
                BorderStyle = BorderStyle.FixedSingle
            };

            lblStatus = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(470, 0),
                Location = new Point(36, 78),
                Text = "Перевірка..."
            };

            btnEnablePolicy = new Button
            {
                Text = "Увімкнути дозвіл",
                Size = new Size(btnW, btnH),
                Location = new Point(colRight, 74),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Visible = false
            };
            btnEnablePolicy.Click += BtnEnablePolicy_Click;

            lblHint = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                MaximumSize = new Size(676, 0),
                Location = new Point(12, 114),
                Text = "Політика: \"Встановити правила віддаленого керування сеансами користувачів служб віддалених робочих столів\" -> \"Повний контроль без дозволу користувача\"."
            };

            btnDisconnect = new Button
            {
                Text = "Завершити сеанс",
                Size = new Size(btnW, btnH),
                Location = new Point(272, 225),
                Enabled = false
            };
            btnDisconnect.Click += BtnDisconnect_Click;

            btnDisconnectAll = new Button
            {
                Text = "Завершити відключені",
                Size = new Size(btnW, btnH),
                Location = new Point(447, 225)
            };
            btnDisconnectAll.Click += BtnDisconnectAll_Click;

            lblExternalIpCaption = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 8F),
                Location = new Point(145, 152),
                Text = "Зовнішній IP:"
            };

            lblExternalIp = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 8F),
                Cursor = Cursors.Hand,
                Location = new Point(145, 168),
                Text = "..."
            };
            lblExternalIp.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_externalIp)) CopyToClipboard(_externalIp);
            };

            ToolStripMenuItem miRebootSchedule = new ToolStripMenuItem("Запланувати...");
            miRebootSchedule.Click += BtnScheduleReboot_Click;
            ToolStripMenuItem miRebootCancel = new ToolStripMenuItem("Скасувати");
            miRebootCancel.Click += BtnCancelReboot_Click;

            rebootMenu = new ContextMenuStrip();
            rebootMenu.Items.Add(miRebootSchedule);
            rebootMenu.Items.Add(miRebootCancel);

            btnReboot.Click += (s, e) => rebootMenu.Show(btnReboot, new Point(0, btnReboot.Height));

            txtSearch = new TextBox
            {
                Location = new Point(12, 228),
                Size = new Size(170, 23)
            };
            txtSearch.TextChanged += (s, e) => ApplyFilter();

            miCtxConnect = new ToolStripMenuItem("Підключитися");
            miCtxConnect.Click += BtnConnect_Click;
            miCtxTakeOver = new ToolStripMenuItem("Перейняти сеанс (за паролем)");
            miCtxTakeOver.Click += MiCtxTakeOver_Click;
            miCtxDisconnect = new ToolStripMenuItem("Завершити сеанс");
            miCtxDisconnect.Click += BtnDisconnect_Click;
            miCtxEnd1C = new ToolStripMenuItem("Завершити 1С/BAS");
            miCtxEnd1C.Click += MiCtxEnd1C_Click;
            miCtxClearCache1C = new ToolStripMenuItem("Очистити кеш 1С/BAS");
            miCtxClearCache1C.Click += MiCtxClearCache1C_Click;
            miCtxMessage = new ToolStripMenuItem("Надіслати повідомлення");
            miCtxMessage.Click += MiCtxMessage_Click;
            miCtxMessageAll = new ToolStripMenuItem("Надіслати повідомлення всім");
            miCtxMessageAll.Click += MiCtxMessageAll_Click;

            ctxMenu = new ContextMenuStrip();
            ctxMenu.Items.Add(miCtxConnect);
            ctxMenu.Items.Add(miCtxTakeOver);
            ctxMenu.Items.Add(miCtxDisconnect);
            ctxMenu.Items.Add(miCtxEnd1C);
            ctxMenu.Items.Add(miCtxClearCache1C);
            ctxMenu.Items.Add(new ToolStripSeparator());
            ctxMenu.Items.Add(miCtxMessage);
            ctxMenu.Items.Add(miCtxMessageAll);
            ctxMenu.Opening += CtxMenu_Opening;

            lvSessions = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = true,
                Location = new Point(12, 258),
                Size = new Size(676, ClientSize.Height - 258 - 12),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ContextMenuStrip = ctxMenu
            };
            lvSessions.Columns.Add("Користувач", 170);
            lvSessions.Columns.Add("ID сеансу", 90);
            lvSessions.Columns.Add("Стан", 130);
            lvSessions.Columns.Add("Ім'я сеансу", 150);
            lvSessions.Columns.Add("1С/BAS", 90);
            lvSessions.Columns.Add("Опис", 160);
            lvSessions.DoubleClick += (s, e) => BtnConnect_Click(s, e);
            lvSessions.SelectedIndexChanged += (s, e) => UpdateActionButtons();
            lvSessions.ColumnClick += LvSessions_ColumnClick;
            lvSessions.MouseDown += LvSessions_MouseDown;

            Controls.Add(lblVersion);
            Controls.Add(btnThemeLight);
            Controls.Add(btnThemeDark);
            Controls.Add(btnThemeBlue);
            Controls.Add(lblSelect);
            Controls.Add(btnRefresh);
            Controls.Add(btnServerCache);
            Controls.Add(pnlIndicator);
            Controls.Add(lblStatus);
            Controls.Add(btnEnablePolicy);
            Controls.Add(lblHint);
            Controls.Add(btnDisconnect);
            Controls.Add(btnDisconnectAll);
            Controls.Add(lblExternalIpCaption);
            Controls.Add(lblExternalIp);
            Controls.Add(btnReboot);
            Controls.Add(txtSearch);
            Controls.Add(lvSessions);
        }

        private void LvSessions_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            ListViewItem item = lvSessions.GetItemAt(e.X, e.Y);
            if (item == null)
            {
                lvSessions.SelectedItems.Clear();
            }
            else if (!item.Selected)
            {
                foreach (ListViewItem sel in lvSessions.SelectedItems) sel.Selected = false;
                item.Selected = true;
                item.Focused = true;
            }
        }

        private void CtxMenu_Opening(object sender, CancelEventArgs e)
        {
            int selCount = lvSessions.SelectedItems.Count;

            miCtxConnect.Enabled = (selCount == 1) && !IsOwnSessionSelected();
            miCtxTakeOver.Enabled = (selCount == 1) && !IsOwnSessionSelected();
            miCtxDisconnect.Enabled = (selCount >= 1);
            miCtxDisconnect.Text = selCount > 1
                ? string.Format("Завершити сеанси ({0})", selCount)
                : "Завершити сеанс";

            miCtxMessage.Enabled = (selCount >= 1);
            miCtxMessage.Text = selCount > 1
                ? string.Format("Надіслати повідомлення ({0})", selCount)
                : "Надіслати повідомлення";

            int oneCTotal = 0;
            foreach (ListViewItem item in lvSessions.SelectedItems)
            {
                int c;
                if (int.TryParse(item.SubItems[4].Text, out c)) oneCTotal += c;
            }
            miCtxEnd1C.Enabled = oneCTotal > 0;
            miCtxEnd1C.Text = oneCTotal > 0
                ? string.Format("Завершити 1С/BAS ({0})", oneCTotal)
                : "Завершити 1С/BAS";

            miCtxClearCache1C.Enabled = (selCount >= 1);
        }

        private void LvSessions_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == _sortColumn)
            {
                _sortOrder = (_sortOrder == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
            }
            else
            {
                _sortColumn = e.Column;
                _sortOrder = SortOrder.Ascending;
            }

            lvSessions.ListViewItemSorter = new ListViewItemComparer(_sortColumn, _sortOrder);
            lvSessions.Sort();
        }

        private bool IsOwnSessionSelected()
        {
            if (lvSessions.SelectedItems.Count != 1) return false;
            int id;
            if (!int.TryParse(lvSessions.SelectedItems[0].SubItems[1].Text, out id)) return false;
            return id == _ownSessionId;
        }

        private void UpdateActionButtons()
        {
            int selCount = lvSessions.SelectedItems.Count;
            btnDisconnect.Enabled = (selCount >= 1);
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count != 1)
            {
                MessageBox.Show(this, "Виберіть рівно один сеанс для підключення.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string id = lvSessions.SelectedItems[0].SubItems[1].Text;
            if (!Regex.IsMatch(id, @"^\d+$"))
            {
                MessageBox.Show(this, "Не вдалося визначити ID сеансу.", "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int idNum;
            if (int.TryParse(id, out idNum) && idNum == _ownSessionId)
            {
                MessageBox.Show(this, "Не можна підключитися до власного сеансу.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int? policyValue = GetShadowPolicyValue();
            bool noConsent = policyValue.HasValue && policyValue.Value == DesiredValue;
            string args = noConsent
                ? string.Format("/shadow:{0} /control /noConsentPrompt", id)
                : string.Format("/shadow:{0} /control", id);

            try
            {
                Process.Start("mstsc.exe", args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося запустити підключення: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MiCtxTakeOver_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count != 1)
            {
                MessageBox.Show(this, "Виберіть рівно один сеанс.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int id;
            if (!int.TryParse(lvSessions.SelectedItems[0].SubItems[1].Text, out id)) return;

            if (id == _ownSessionId)
            {
                MessageBox.Show(this, "Не можна перейняти власний сеанс.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string userName = lvSessions.SelectedItems[0].Text;

            DialogResult confirm = MessageBox.Show(this,
                string.Format("Ваш поточний сеанс буде замінено сеансом користувача \"{0}\" (як команда \"Підключити\" в Диспетчері завдань). " +
                    "Це не тіньовий перегляд — ваш власний робочий стіл стане недоступний, поки ви не повернетесь назад. Продовжити?", userName),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            string password = ShowInputDialog(string.Format("Пароль користувача \"{0}\":", userName), "Перейняти сеанс", true);
            if (password == null) return;

            string ownStation = GetSessionStationName(_ownSessionId);
            if (string.IsNullOrEmpty(ownStation))
            {
                MessageBox.Show(this, "Не вдалося визначити назву поточного сеансу.", "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("tscon.exe",
                    string.Format("{0} /dest:{1} /password:{2}", id, ownStation, password))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    if (p.ExitCode != 0)
                    {
                        string err = p.StandardError.ReadToEnd().Trim();
                        MessageBox.Show(this,
                            "Не вдалося перейняти сеанс (код " + p.ExitCode + ")." + (string.IsNullOrEmpty(err) ? "" : "\n" + err),
                            "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося перейняти сеанс: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string GetSessionStationName(int sessionId)
        {
            IntPtr buffer;
            int bytesReturned;
            string result = "";

            if (Wts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, Wts.WtsInfoClass.WTSWinStationName, out buffer, out bytesReturned))
            {
                if (buffer != IntPtr.Zero)
                {
                    result = Marshal.PtrToStringUni(buffer);
                    Wts.WTSFreeMemory(buffer);
                }
            }

            return result;
        }

        private void BtnDisconnect_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count == 0) return;

            List<int> ids = new List<int>();
            List<string> names = new List<string>();
            foreach (ListViewItem item in lvSessions.SelectedItems)
            {
                int id;
                if (int.TryParse(item.SubItems[1].Text, out id))
                {
                    ids.Add(id);
                    names.Add(item.Text);
                }
            }

            if (ids.Count == 0) return;

            string confirmText = ids.Count == 1
                ? string.Format("Завершити сеанс користувача \"{0}\" (ID {1})?", names[0], ids[0])
                : string.Format("Завершити {0} вибраних сеансів?", ids.Count);

            DialogResult confirm = MessageBox.Show(this, confirmText, "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            LogoffSessions(ids);
            RefreshSessions();
        }

        private void BtnDisconnectAll_Click(object sender, EventArgs e)
        {
            List<int> ids = new List<int>();
            foreach (RdpSession s in _allSessions)
            {
                if (s.RawState != WtsConnectState.Disconnected) continue;
                int id;
                if (int.TryParse(s.Id, out id)) ids.Add(id);
            }

            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Відключених сеансів не знайдено.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                string.Format("Завершити всі відключені сеанси ({0} шт.)?", ids.Count),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            LogoffSessions(ids);
            RefreshSessions();
        }

        private void LogoffSessions(List<int> ids)
        {
            List<string> failed = new List<string>();
            foreach (int id in ids)
            {
                if (!Wts.WTSLogoffSession(IntPtr.Zero, id, false))
                {
                    failed.Add(id.ToString());
                }
            }

            if (failed.Count > 0)
            {
                MessageBox.Show(this, "Не вдалося завершити сеанс(и) з ID: " + string.Join(", ", failed.ToArray()), "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static List<Process> GetOneCProcessesForSessions(List<int> sessionIds)
        {
            List<Process> processes = new List<Process>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (Is1CProcess(p.ProcessName) && sessionIds.Contains(p.SessionId)) processes.Add(p);
                }
                catch
                {
                    // процес міг завершитись між переліком і зверненням до нього
                }
            }
            return processes;
        }

        private static List<string> KillProcesses(List<Process> processes)
        {
            List<string> failed = new List<string>();
            foreach (Process p in processes)
            {
                try
                {
                    p.Kill();
                }
                catch (Exception ex)
                {
                    failed.Add(p.Id + " (" + ex.Message + ")");
                }
            }
            return failed;
        }

        private void MiCtxEnd1C_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count == 0) return;

            List<int> ids = new List<int>();
            foreach (ListViewItem item in lvSessions.SelectedItems)
            {
                int id;
                if (int.TryParse(item.SubItems[1].Text, out id)) ids.Add(id);
            }
            if (ids.Count == 0) return;

            List<Process> processes = GetOneCProcessesForSessions(ids);

            if (processes.Count == 0)
            {
                MessageBox.Show(this, "У вибраних сеансах немає запущених процесів 1С/BAS.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                string.Format("Завершити {0} процес(и) 1С/BAS у вибраних сеансах? Незбережені дані користувачів буде втрачено.", processes.Count),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            List<string> failed = KillProcesses(processes);

            if (failed.Count > 0)
            {
                MessageBox.Show(this, "Не вдалося завершити процес(и): " + string.Join(", ", failed.ToArray()), "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            RefreshSessions();
        }

        private void MiCtxClearCache1C_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count == 0) return;

            List<int> ids = new List<int>();
            List<string> userNames = new List<string>();
            foreach (ListViewItem item in lvSessions.SelectedItems)
            {
                int id;
                if (int.TryParse(item.SubItems[1].Text, out id))
                {
                    ids.Add(id);
                    userNames.Add(item.Text);
                }
            }
            if (ids.Count == 0) return;

            DialogResult confirm = MessageBox.Show(this,
                string.Format(
                    "Для {0} буде виконано:\n" +
                    "1. Завершення процесів 1С/BAS\n" +
                    "2. Очищення кешу 1С (Config, ConfigSave, DBNameCache, SICache, vrs-cache) — інформаційні бази й налаштування обладнання не зачіпаються\n" +
                    "3. Повідомлення \"Можна працювати\"\n\nПродовжити?",
                    string.Join(", ", userNames.ToArray())),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            List<Process> processes = GetOneCProcessesForSessions(ids);
            List<string> failedKill = KillProcesses(processes);
            if (processes.Count > 0) System.Threading.Thread.Sleep(1500);

            List<string> cacheErrors = new List<string>();
            foreach (string userName in userNames)
            {
                try
                {
                    ClearOneCCache(userName);
                }
                catch (Exception ex)
                {
                    cacheErrors.Add(userName + ": " + ex.Message);
                }
            }

            SendMessageToSessions(ids, "Можна працювати.");
            RefreshSessions();

            if (failedKill.Count > 0 || cacheErrors.Count > 0)
            {
                StringBuilder msg = new StringBuilder();
                if (failedKill.Count > 0) msg.AppendLine("Не вдалося завершити процес(и): " + string.Join(", ", failedKill.ToArray()));
                if (cacheErrors.Count > 0) msg.AppendLine("Помилки очищення кешу: " + string.Join("; ", cacheErrors.ToArray()));
                MessageBox.Show(this, msg.ToString(), "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                MessageBox.Show(this, "Готово: сеанси 1С завершено, кеш очищено, користувачів повідомлено.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static readonly string[] OneCVersionFolders = { "1Cv8", "1Cv82" };
        private static readonly string[] OneCCacheFoldersToDelete = { "Config", "ConfigSave", "DBNameCache", "SICache", "vrs-cache" };

        private static void ClearOneCCache(string userName)
        {
            int slashIdx = userName.IndexOf('\\');
            string plainUserName = slashIdx >= 0 ? userName.Substring(slashIdx + 1) : userName;

            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") + "\\";
            string profileRoot = Path.Combine(systemDrive, "Users", plainUserName);

            string[] roots =
            {
                Path.Combine(profileRoot, "AppData", "Local", "1C"),
                Path.Combine(profileRoot, "AppData", "Roaming", "1C")
            };

            foreach (string root in roots)
            {
                foreach (string verFolder in OneCVersionFolders)
                {
                    string versionPath = Path.Combine(root, verFolder);
                    if (!Directory.Exists(versionPath)) continue;

                    foreach (string dbDir in Directory.GetDirectories(versionPath))
                    {
                        foreach (string cacheFolder in OneCCacheFoldersToDelete)
                        {
                            string target = Path.Combine(dbDir, cacheFolder);
                            if (Directory.Exists(target))
                            {
                                try { Directory.Delete(target, true); }
                                catch { /* пропускаємо окрему теку, якщо вона зайнята */ }
                            }
                        }
                    }
                }
            }
        }

        private void MiServerCacheCleanup_Click(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(this,
                "Це зупинить службу сервера 1С/BAF і очистить серверний кеш (тека srvinfo).\n\n" +
                "1С стане недоступним для ВСІХ користувачів сервера на деякий час, доки служба не запуститься знову " +
                "(це відрізняється від \"Очистити кеш 1С/BAS\" у контекстному меню, яка чистить кеш лише одного вибраного користувача).\n\n" +
                "Усім активним сеансам буде надіслано попередження і 30-секундний відлік перед початком. Продовжити?",
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            List<int> ids = new List<int>();
            foreach (RdpSession s in _allSessions)
            {
                int id;
                if (int.TryParse(s.Id, out id) && id != _ownSessionId) ids.Add(id);
            }

            if (ids.Count > 0)
            {
                SendMessageToSessions(ids, "Через 30 секунд розпочнеться технічне обслуговування сервера 1С/BAF. Будь ласка, збережіть роботу.");
            }

            if (ShowCountdownDialog(30)) return;

            btnServerCache.Enabled = false;
            Cursor = Cursors.WaitCursor;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                ServerCacheCleanup.CleanupResult result = ServerCacheCleanup.Run(true);

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        Cursor = Cursors.Default;
                        btnServerCache.Enabled = true;
                        if (ids.Count > 0) SendMessageToSessions(ids, "Можна працювати.");
                        ShowServerCleanupResult(result);
                        RefreshSessions();
                    }));
                }
            });
        }

        private void ShowServerCleanupResult(ServerCacheCleanup.CleanupResult result)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string s in result.ServicesProcessed) sb.AppendLine(s);
            if (result.ServicesProcessed.Count > 0)
            {
                sb.AppendLine(string.Format("Видалено тек кешу: {0}", result.FoldersDeleted.Count));
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Помилки:");
                foreach (string err in result.Errors) sb.AppendLine(err);
            }
            if (sb.Length == 0) sb.Append("Готово.");

            MessageBox.Show(this, sb.ToString(), "Очищення серверного кешу 1С",
                MessageBoxButtons.OK, result.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private bool ShowCountdownDialog(int seconds)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "Технічне обслуговування 1С/BAF";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ControlBox = false;
                dlg.ClientSize = new Size(340, 130);
                dlg.Font = Font;
                dlg.BackColor = BackColor;

                Label lbl = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(316, 0),
                    Location = new Point(12, 15),
                    ForeColor = lblSelect.ForeColor,
                    Text = "Попередження надіслано. Очищення почнеться через:"
                };

                Label lblCountdown = new Label
                {
                    AutoSize = true,
                    Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                    Location = new Point(12, 45),
                    ForeColor = lblSelect.ForeColor,
                    Text = seconds.ToString()
                };

                Button cancelBtn = new Button
                {
                    Text = "Скасувати",
                    Size = new Size(100, 28),
                    Location = new Point(228, 90),
                    DialogResult = DialogResult.Cancel,
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                cancelBtn.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                dlg.Controls.Add(lbl);
                dlg.Controls.Add(lblCountdown);
                dlg.Controls.Add(cancelBtn);
                dlg.CancelButton = cancelBtn;

                int remaining = seconds;
                Timer timer = new Timer { Interval = 1000 };
                timer.Tick += (s, e) =>
                {
                    remaining--;
                    lblCountdown.Text = remaining.ToString();
                    if (remaining <= 0)
                    {
                        timer.Stop();
                        dlg.DialogResult = DialogResult.OK;
                        dlg.Close();
                    }
                };
                timer.Start();

                DialogResult result = dlg.ShowDialog(this);
                timer.Stop();
                timer.Dispose();

                return result != DialogResult.OK;
            }
        }

        private class CacheScheduleResult
        {
            public bool Weekly;
            public string Time;
        }

        private void MiScheduleCacheCleanup_Click(object sender, EventArgs e)
        {
            CacheScheduleResult sr = ShowScheduleCacheDialog();
            if (sr == null) return;

            RunHidden("schtasks.exe", string.Format("/delete /tn \"{0}\" /f", ServerCacheCleanup.TaskName));

            string exePath = Application.ExecutablePath;
            string freq = sr.Weekly ? "WEEKLY" : "DAILY";
            string createArgs = string.Format(
                "/create /tn \"{0}\" /tr \"\\\"{1}\\\" {2}\" /sc {3} /st {4} /ru SYSTEM /rl HIGHEST /f",
                ServerCacheCleanup.TaskName, exePath, ServerCacheCleanup.AutoRunArg, freq, sr.Time);

            int exitCode = RunHidden("schtasks.exe", createArgs);

            if (exitCode == 0)
            {
                MessageBox.Show(this,
                    string.Format("Завдання заплановано: {0}, о {1}. Виконується без попереджень користувачам, як окремий процес (SYSTEM). Логи — у C:\\Scripts\\1C_Maintenance\\Logs.",
                        sr.Weekly ? "щотижня" : "щодня", sr.Time),
                    "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "Не вдалося створити завдання в Планувальнику. Перевірте права адміністратора.",
                    "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MiCancelScheduledCleanup_Click(object sender, EventArgs e)
        {
            int exitCode = RunHidden("schtasks.exe", string.Format("/delete /tn \"{0}\" /f", ServerCacheCleanup.TaskName));
            if (exitCode == 0)
            {
                MessageBox.Show(this, "Заплановане очищення скасовано.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "Запланованого завдання не знайдено.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static int RunHidden(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    return p.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }

        private CacheScheduleResult ShowScheduleCacheDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "Запланувати очищення серверного кешу 1С";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(340, 190);
                dlg.Font = Font;
                dlg.BackColor = BackColor;

                RadioButton radDaily = new RadioButton
                {
                    Text = "Щодня",
                    Checked = true,
                    Location = new Point(12, 15),
                    AutoSize = true,
                    ForeColor = lblSelect.ForeColor
                };
                RadioButton radWeekly = new RadioButton
                {
                    Text = "Щотижня",
                    Location = new Point(12, 42),
                    AutoSize = true,
                    ForeColor = lblSelect.ForeColor
                };

                Label lblTime = new Label
                {
                    Text = "О годині:",
                    Location = new Point(12, 78),
                    AutoSize = true,
                    ForeColor = lblSelect.ForeColor
                };
                DateTimePicker dtpTime = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Custom,
                    CustomFormat = "HH:mm",
                    ShowUpDown = true,
                    Value = DateTime.Today.AddHours(3),
                    Location = new Point(90, 75),
                    Size = new Size(80, 23)
                };

                Label lblWarn = new Label
                {
                    Text = "Без попереджень користувачам — виконується як окремий фоновий процес.",
                    ForeColor = Color.DimGray,
                    Location = new Point(12, 108),
                    MaximumSize = new Size(316, 0),
                    AutoSize = true
                };

                Button ok = new Button
                {
                    Text = "Запланувати",
                    DialogResult = DialogResult.OK,
                    Location = new Point(152, 150),
                    Size = new Size(90, 28),
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                ok.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                Button cancel = new Button
                {
                    Text = "Скасувати",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(248, 150),
                    Size = new Size(80, 28),
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                cancel.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                dlg.Controls.Add(radDaily);
                dlg.Controls.Add(radWeekly);
                dlg.Controls.Add(lblTime);
                dlg.Controls.Add(dtpTime);
                dlg.Controls.Add(lblWarn);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;

                return new CacheScheduleResult { Weekly = radWeekly.Checked, Time = dtpTime.Value.ToString("HH:mm") };
            }
        }

        private class RebootScheduleResult
        {
            public int DelayMinutes;
            public bool Force;
        }

        private void BtnScheduleReboot_Click(object sender, EventArgs e)
        {
            RebootScheduleResult result = ShowScheduleRebootDialog();
            if (result == null) return;

            DateTime targetTime = DateTime.Now.AddMinutes(result.DelayMinutes);
            string message = string.Format("Сервер буде перезавантажений через {0} хв. (орієнтовно о {1}).", result.DelayMinutes, targetTime.ToString("HH:mm"));

            DialogResult confirm = MessageBox.Show(this,
                string.Format("Заплановано перезавантаження через {0} хв. (о {1}).\nБуде надіслано повідомлення всім активним сеансам:\n\n\"{2}\"\n\nПродовжити?",
                    result.DelayMinutes, targetTime.ToString("HH:mm"), message),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            List<int> ids = new List<int>();
            foreach (RdpSession s in _allSessions)
            {
                int id;
                if (int.TryParse(s.Id, out id) && id != _ownSessionId) ids.Add(id);
            }
            if (ids.Count > 0) SendMessageToSessions(ids, message);

            try
            {
                int seconds = result.DelayMinutes * 60;
                string args = string.Format("/r /t {0} /c \"{1}\"{2}",
                    seconds, message.Replace("\"", "'"), result.Force ? " /f" : "");

                ProcessStartInfo psi = new ProcessStartInfo("shutdown.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                Text = string.Format("Засіб тіньових сеансів — перезавантаження о {0}", targetTime.ToString("HH:mm"));
                MessageBox.Show(this, "Перезавантаження заплановано.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося запланувати перезавантаження: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnCancelReboot_Click(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(this, "Скасувати заплановане перезавантаження сервера?", "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("shutdown.exe", "/a")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0)
                    {
                        Text = "Засіб тіньових сеансів";

                        List<int> ids = new List<int>();
                        foreach (RdpSession s in _allSessions)
                        {
                            int id;
                            if (int.TryParse(s.Id, out id) && id != _ownSessionId) ids.Add(id);
                        }
                        if (ids.Count > 0) SendMessageToSessions(ids, "Заплановане перезавантаження сервера скасовано.");

                        MessageBox.Show(this, "Перезавантаження скасовано.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(this, "Не знайдено запланованого перезавантаження (або не вдалося скасувати).", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Помилка скасування: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private RebootScheduleResult ShowScheduleRebootDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "Запланувати перезавантаження";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(360, 236);
                dlg.Font = Font;
                dlg.BackColor = BackColor;

                RadioButton radRelative = new RadioButton
                {
                    Text = "Через хвилин:",
                    Checked = true,
                    Location = new Point(12, 15),
                    AutoSize = true,
                    ForeColor = lblSelect.ForeColor
                };
                NumericUpDown numMinutes = new NumericUpDown
                {
                    Minimum = 1,
                    Maximum = 1440,
                    Value = 15,
                    Location = new Point(160, 12),
                    Size = new Size(70, 23)
                };

                RadioButton radAbsolute = new RadioButton
                {
                    Text = "На годину:",
                    Location = new Point(12, 48),
                    AutoSize = true,
                    ForeColor = lblSelect.ForeColor
                };
                DateTimePicker dtpTime = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Custom,
                    CustomFormat = "HH:mm",
                    ShowUpDown = true,
                    Value = DateTime.Now.AddMinutes(15),
                    Location = new Point(160, 46),
                    Size = new Size(80, 23),
                    Enabled = false
                };

                radRelative.CheckedChanged += (s, e) =>
                {
                    numMinutes.Enabled = radRelative.Checked;
                    dtpTime.Enabled = !radRelative.Checked;
                };

                CheckBox chkForce = new CheckBox
                {
                    Text = "Примусово закривати програми користувачів",
                    Location = new Point(12, 84),
                    AutoSize = true,
                    MaximumSize = new Size(336, 0),
                    ForeColor = lblSelect.ForeColor
                };

                Label lblWarn = new Label
                {
                    Text = "Усім активним сеансам (крім вашого) буде надіслано повідомлення з часом перезавантаження.",
                    ForeColor = Color.DimGray,
                    Location = new Point(12, 128),
                    MaximumSize = new Size(336, 0),
                    AutoSize = true
                };

                Button ok = new Button
                {
                    Text = "Запланувати",
                    DialogResult = DialogResult.OK,
                    Location = new Point(172, 196),
                    Size = new Size(90, 28),
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                ok.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                Button cancel = new Button
                {
                    Text = "Скасувати",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(268, 196),
                    Size = new Size(90, 28),
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                cancel.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                dlg.Controls.Add(radRelative);
                dlg.Controls.Add(numMinutes);
                dlg.Controls.Add(radAbsolute);
                dlg.Controls.Add(dtpTime);
                dlg.Controls.Add(chkForce);
                dlg.Controls.Add(lblWarn);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;

                int minutes;
                if (radRelative.Checked)
                {
                    minutes = (int)numMinutes.Value;
                }
                else
                {
                    DateTime target = DateTime.Today.Add(dtpTime.Value.TimeOfDay);
                    if (target <= DateTime.Now) target = target.AddDays(1);
                    minutes = (int)Math.Ceiling((target - DateTime.Now).TotalMinutes);
                    if (minutes < 1) minutes = 1;
                }

                return new RebootScheduleResult { DelayMinutes = minutes, Force = chkForce.Checked };
            }
        }

        private void MiCtxMessage_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count == 0) return;

            List<int> ids = new List<int>();
            foreach (ListViewItem item in lvSessions.SelectedItems)
            {
                int id;
                if (int.TryParse(item.SubItems[1].Text, out id)) ids.Add(id);
            }
            if (ids.Count == 0) return;

            string prompt = ids.Count == 1
                ? "Текст повідомлення:"
                : string.Format("Текст повідомлення для вибраних сеансів ({0}):", ids.Count);

            string message = ShowInputDialog(prompt, "Надіслати повідомлення", false, true);
            if (string.IsNullOrEmpty(message)) return;

            SendMessageToSessions(ids, message);
        }

        private void MiCtxMessageAll_Click(object sender, EventArgs e)
        {
            List<int> ids = new List<int>();
            foreach (RdpSession s in _allSessions)
            {
                int id;
                if (int.TryParse(s.Id, out id) && id != _ownSessionId) ids.Add(id);
            }

            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Немає інших активних сеансів.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string message = ShowInputDialog(
                string.Format("Текст повідомлення для всіх сеансів, крім вашого ({0}):", ids.Count),
                "Надіслати повідомлення всім", false, true);
            if (string.IsNullOrEmpty(message)) return;

            SendMessageToSessions(ids, message);
        }

        private void SendMessageToSessions(List<int> ids, string message)
        {
            const string title = "Повідомлення від адміністратора";
            int titleBytes = (title.Length + 1) * 2;
            int messageBytes = (message.Length + 1) * 2;

            List<string> failed = new List<string>();
            foreach (int id in ids)
            {
                int response;
                bool ok = Wts.WTSSendMessage(IntPtr.Zero, id, title, titleBytes, message, messageBytes, 0, 0, out response, false);
                if (!ok) failed.Add(id.ToString());
            }

            if (failed.Count > 0)
            {
                MessageBox.Show(this, "Не вдалося надіслати повідомлення сеансу(ам) з ID: " + string.Join(", ", failed.ToArray()), "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string ShowInputDialog(string prompt, string title, bool isPassword = false, bool showTemplates = false)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.Font = Font;
                dlg.BackColor = BackColor;

                int y = 12;

                Label lbl = new Label
                {
                    Text = prompt,
                    AutoSize = true,
                    MaximumSize = new Size(376, 0),
                    Location = new Point(12, y),
                    ForeColor = lblSelect.ForeColor
                };
                y += lbl.PreferredHeight + 6;

                ComboBox cmbTemplate = null;
                if (showTemplates)
                {
                    cmbTemplate = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Location = new Point(12, y),
                        Size = new Size(376, 23),
                        BackColor = txtSearch.BackColor,
                        ForeColor = txtSearch.ForeColor
                    };
                    cmbTemplate.Items.Add("(без шаблону)");
                    foreach (string t in MessageTemplates) cmbTemplate.Items.Add(t);
                    cmbTemplate.SelectedIndex = 0;
                    y += 23 + 8;
                }

                int txtHeight = isPassword ? 23 : 80;
                TextBox txt = new TextBox
                {
                    Multiline = !isPassword,
                    UseSystemPasswordChar = isPassword,
                    ScrollBars = isPassword ? ScrollBars.None : ScrollBars.Vertical,
                    Location = new Point(12, y),
                    Size = new Size(376, txtHeight),
                    BackColor = txtSearch.BackColor,
                    ForeColor = txtSearch.ForeColor
                };
                y += txtHeight + 12;

                if (cmbTemplate != null)
                {
                    ComboBox cmbRef = cmbTemplate;
                    cmbRef.SelectedIndexChanged += (s, e) =>
                    {
                        if (cmbRef.SelectedIndex > 0) txt.Text = MessageTemplates[cmbRef.SelectedIndex - 1];
                    };
                }

                Button ok = new Button
                {
                    Text = isPassword ? "OK" : "Надіслати",
                    DialogResult = DialogResult.OK,
                    Location = new Point(216, y),
                    Size = new Size(80, 28),
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                ok.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                Button cancel = new Button
                {
                    Text = "Скасувати",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(308, y),
                    Size = new Size(80, 28),
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                cancel.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                dlg.ClientSize = new Size(400, y + 28 + 12);

                dlg.Controls.Add(lbl);
                if (cmbTemplate != null) dlg.Controls.Add(cmbTemplate);
                dlg.Controls.Add(txt);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : null;
            }
        }

        private void BtnEnablePolicy_Click(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(this,
                "Буде увімкнено локальну групову політику \"Встановити правила віддаленого керування сеансами користувачів служб віддалених робочих столів\" " +
                "зі значенням \"Повний контроль без дозволу користувача\".\n\n" +
                "Це дозволить підключатися до сеансів інших користувачів без запиту їхньої згоди. Продовжити?",
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegPath))
                {
                    key.SetValue(RegName, DesiredValue, RegistryValueKind.DWord);
                }
                RefreshPolicyStatus();
                MessageBox.Show(this, "Параметр успішно увімкнено.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося змінити параметр: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshPolicyStatus()
        {
            int? value = GetShadowPolicyValue();
            if (value.HasValue && value.Value == DesiredValue)
            {
                pnlIndicator.BackColor = Color.ForestGreen;
                lblStatus.Text = "Тіньове підключення без згоди користувача: УВІМКНЕНО";
                btnEnablePolicy.Visible = false;
            }
            else if (!value.HasValue)
            {
                pnlIndicator.BackColor = Color.Firebrick;
                lblStatus.Text = "Тіньове підключення без згоди користувача: НЕ НАЛАШТОВАНО";
                btnEnablePolicy.Visible = true;
            }
            else
            {
                pnlIndicator.BackColor = Color.Firebrick;
                lblStatus.Text = string.Format("Тіньове підключення без згоди користувача: ВИМКНЕНО (значення: {0})", value.Value);
                btnEnablePolicy.Visible = true;
            }
        }

        private int? GetShadowPolicyValue()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegPath))
                {
                    if (key == null) return null;
                    object val = key.GetValue(RegName);
                    if (val == null) return null;
                    return Convert.ToInt32(val);
                }
            }
            catch
            {
                return null;
            }
        }

        private AppTheme LoadSavedTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UserRegPath))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("Theme");
                        if (val != null)
                        {
                            AppTheme parsed;
                            if (Enum.TryParse(val.ToString(), out parsed)) return parsed;
                        }
                    }
                }
            }
            catch
            {
                // ігноруємо, застосуємо тему за замовчуванням
            }
            return AppTheme.Blue;
        }

        private void SaveTheme(AppTheme theme)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UserRegPath))
                {
                    key.SetValue("Theme", theme.ToString());
                }
            }
            catch
            {
                // збереження вподобання не критичне
            }
        }

        private void ApplyTheme(AppTheme theme)
        {
            Color formBack, controlBack, controlFore, textFore, hintFore, listBack, listFore, textBoxBack, textBoxFore, btnBorder;
            FlatStyle btnStyle;

            switch (theme)
            {
                case AppTheme.Dark:
                    formBack = Color.FromArgb(32, 32, 32);
                    controlBack = Color.FromArgb(45, 45, 48);
                    controlFore = Color.White;
                    textFore = Color.White;
                    hintFore = Color.FromArgb(160, 160, 160);
                    listBack = Color.FromArgb(37, 37, 38);
                    listFore = Color.White;
                    textBoxBack = Color.FromArgb(45, 45, 48);
                    textBoxFore = Color.White;
                    btnBorder = Color.FromArgb(80, 80, 80);
                    btnStyle = FlatStyle.Flat;
                    break;

                case AppTheme.Blue:
                    formBack = Color.FromArgb(235, 242, 250);
                    controlBack = Color.FromArgb(214, 231, 247);
                    controlFore = Color.FromArgb(20, 40, 70);
                    textFore = Color.FromArgb(20, 40, 70);
                    hintFore = Color.FromArgb(90, 110, 140);
                    listBack = Color.White;
                    listFore = Color.FromArgb(20, 40, 70);
                    textBoxBack = Color.White;
                    textBoxFore = Color.FromArgb(20, 40, 70);
                    btnBorder = Color.FromArgb(150, 180, 215);
                    btnStyle = FlatStyle.Flat;
                    break;

                default:
                    formBack = SystemColors.Control;
                    controlBack = SystemColors.Control;
                    controlFore = SystemColors.ControlText;
                    textFore = SystemColors.ControlText;
                    hintFore = Color.DimGray;
                    listBack = SystemColors.Window;
                    listFore = SystemColors.WindowText;
                    textBoxBack = SystemColors.Window;
                    textBoxFore = SystemColors.WindowText;
                    btnBorder = SystemColors.ControlDark;
                    btnStyle = FlatStyle.Standard;
                    break;
            }

            BackColor = formBack;

            lblSelect.ForeColor = textFore;
            lblStatus.ForeColor = textFore;
            lblHint.ForeColor = hintFore;
            if (_pendingUpdateVersion == null) lblVersion.ForeColor = hintFore;
            lblExternalIpCaption.ForeColor = hintFore;
            lblExternalIp.ForeColor = hintFore;
            foreach (Label lbl in _localIpLabels) lbl.ForeColor = hintFore;

            txtSearch.BackColor = textBoxBack;
            txtSearch.ForeColor = textBoxFore;

            lvSessions.BackColor = listBack;
            lvSessions.ForeColor = listFore;

            Button[] buttons = { btnRefresh, btnServerCache, btnEnablePolicy, btnDisconnect, btnDisconnectAll, btnReboot };
            foreach (Button btn in buttons)
            {
                btn.FlatStyle = btnStyle;
                btn.BackColor = controlBack;
                btn.ForeColor = controlFore;
                btn.FlatAppearance.BorderColor = btnBorder;
            }

            SetThemeSwatchActive(btnThemeLight, theme == AppTheme.Light, Color.Black);
            SetThemeSwatchActive(btnThemeDark, theme == AppTheme.Dark, Color.White);
            SetThemeSwatchActive(btnThemeBlue, theme == AppTheme.Blue, Color.Black);

            Image oldRefreshIcon = btnRefresh.Image;
            btnRefresh.Image = CreateRefreshIcon(controlFore);
            if (oldRefreshIcon != null) oldRefreshIcon.Dispose();

            _currentTheme = theme;

            try
            {
                SetWindowTheme(lvSessions.Handle, theme == AppTheme.Dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch
            {
                // недоступно на цій версії ОС - не критично, рядки списку все одно перефарбуються
            }

            SaveTheme(theme);
        }

        private static void SetThemeSwatchActive(Button btn, bool active, Color activeBorderColor)
        {
            btn.FlatAppearance.BorderSize = active ? 3 : 1;
            btn.FlatAppearance.BorderColor = active ? activeBorderColor : Color.Gray;
        }

        private static Bitmap CreateRefreshIcon(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(color, 2f))
                {
                    g.DrawArc(pen, 2, 2, 12, 12, -30, 270);
                }
                Point[] arrow = { new Point(13, 1), new Point(15, 6), new Point(10, 5) };
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillPolygon(brush, arrow);
                }
            }
            return bmp;
        }

        private static void CopyToClipboard(string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            try
            {
                Clipboard.SetText(value);
            }
            catch
            {
                // буфер обміну міг бути зайнятий іншим процесом - не критично
            }
        }

        private void RefreshSessions()
        {
            _allSessions = GetRdpSessions();
            ApplyCachedDescriptions(_allSessions);
            ApplyFilter();
            FetchDescriptionsAsync(_allSessions);
        }

        private void ApplyCachedDescriptions(List<RdpSession> sessions)
        {
            foreach (RdpSession s in sessions)
            {
                string cached;
                if (_descriptionCache.TryGetValue(s.UserName, out cached))
                {
                    s.Description = cached;
                }
            }
        }

        private void FetchDescriptionsAsync(List<RdpSession> sessions)
        {
            List<RdpSession> toFetch = new List<RdpSession>();
            foreach (RdpSession s in sessions)
            {
                if (!_descriptionCache.ContainsKey(s.UserName)) toFetch.Add(s);
            }
            if (toFetch.Count == 0) return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, string> fetched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (RdpSession s in toFetch)
                {
                    string desc;
                    if (!fetched.TryGetValue(s.UserName, out desc))
                    {
                        desc = GetUserDescription(s.UserName);
                        fetched[s.UserName] = desc;
                    }
                    s.Description = desc;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        foreach (KeyValuePair<string, string> kv in fetched)
                        {
                            _descriptionCache[kv.Key] = kv.Value;
                        }
                        if (_allSessions == sessions) ApplyFilter();
                    }));
                }
            });
        }

        private void ApplyFilter()
        {
            lvSessions.Items.Clear();
            string filter = txtSearch.Text.Trim();

            foreach (RdpSession s in _allSessions)
            {
                if (!string.IsNullOrEmpty(filter))
                {
                    bool matchesUser = s.UserName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchesDesc = !string.IsNullOrEmpty(s.Description) &&
                        s.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchesUser && !matchesDesc) continue;
                }

                bool isOwn = false;
                int id;
                if (int.TryParse(s.Id, out id) && id == _ownSessionId) isOwn = true;

                ListViewItem item = new ListViewItem(s.UserName);
                item.SubItems.Add(s.Id);
                item.SubItems.Add(s.State);
                item.SubItems.Add(isOwn ? s.SessionName + " (поточний)" : s.SessionName);
                item.SubItems.Add(s.OneCCount.ToString());
                item.SubItems.Add(s.Description);
                lvSessions.Items.Add(item);
            }

            if (lvSessions.ListViewItemSorter != null)
            {
                lvSessions.Sort();
            }

            UpdateActionButtons();
        }

        private static string TranslateState(WtsConnectState state)
        {
            switch (state)
            {
                case WtsConnectState.Active: return "Активний";
                case WtsConnectState.Connected: return "Підключення";
                case WtsConnectState.ConnectQuery: return "Опитування";
                case WtsConnectState.Shadow: return "Тіньовий режим";
                case WtsConnectState.Disconnected: return "Відключено";
                case WtsConnectState.Idle: return "Очікування";
                case WtsConnectState.Listen: return "Прослуховування";
                case WtsConnectState.Reset: return "Скидання";
                case WtsConnectState.Down: return "Недоступно";
                case WtsConnectState.Init: return "Ініціалізація";
                default: return state.ToString();
            }
        }

        private List<RdpSession> GetRdpSessions()
        {
            List<RdpSession> result = new List<RdpSession>();
            IntPtr pSessionInfo = IntPtr.Zero;
            int count = 0;

            if (!Wts.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out pSessionInfo, out count))
            {
                return result;
            }

            try
            {
                Dictionary<int, int> oneCCounts = GetOneCCountsBySession();
                int dataSize = Marshal.SizeOf(typeof(Wts.WTS_SESSION_INFO));
                IntPtr current = pSessionInfo;

                for (int i = 0; i < count; i++)
                {
                    Wts.WTS_SESSION_INFO si = (Wts.WTS_SESSION_INFO)Marshal.PtrToStructure(current, typeof(Wts.WTS_SESSION_INFO));
                    current = (IntPtr)((long)current + dataSize);

                    if (si.State == WtsConnectState.Listen) continue;

                    string userName = GetSessionUserName(si.SessionID);
                    if (string.IsNullOrEmpty(userName)) continue;

                    string stationName = si.pWinStationName != IntPtr.Zero
                        ? Marshal.PtrToStringUni(si.pWinStationName)
                        : "";

                    int oneCCount;
                    oneCCounts.TryGetValue(si.SessionID, out oneCCount);

                    result.Add(new RdpSession
                    {
                        UserName = userName,
                        SessionName = stationName,
                        Id = si.SessionID.ToString(),
                        State = TranslateState(si.State),
                        RawState = si.State,
                        OneCCount = oneCCount,
                        Description = ""
                    });
                }
            }
            finally
            {
                Wts.WTSFreeMemory(pSessionInfo);
            }

            return result;
        }

        private string GetSessionUserName(int sessionId)
        {
            IntPtr buffer;
            int bytesReturned;
            string result = "";

            if (Wts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, Wts.WtsInfoClass.WTSUserName, out buffer, out bytesReturned))
            {
                if (buffer != IntPtr.Zero)
                {
                    result = Marshal.PtrToStringUni(buffer);
                    Wts.WTSFreeMemory(buffer);
                }
            }

            return result;
        }

        private static string GetUserDescription(string userName)
        {
            int slashIdx = userName.IndexOf('\\');
            string plainUserName = slashIdx >= 0 ? userName.Substring(slashIdx + 1) : userName;

            try
            {
                using (DirectoryEntry entry = new DirectoryEntry("WinNT://" + Environment.MachineName + "/" + plainUserName + ",user"))
                {
                    object desc = entry.Properties["Description"].Value;
                    return desc != null ? desc.ToString() : "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool Is1CProcess(string processName)
        {
            return processName.StartsWith("1cv8", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, int> GetOneCCountsBySession()
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (!Is1CProcess(p.ProcessName)) continue;

                    int sessionCount;
                    counts.TryGetValue(p.SessionId, out sessionCount);
                    counts[p.SessionId] = sessionCount + 1;
                }
                catch
                {
                    // процес міг завершитись між переліком і зверненням до нього
                }
            }

            return counts;
        }

        private void CheckForUpdatesAsync(bool manual = false)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string remoteVersion = DownloadString(UpdateVersionUrl).Trim();
                    Version remote = null, local = null;
                    bool parsed = Version.TryParse(remoteVersion, out remote) && Version.TryParse(AppVersion, out local);

                    if (parsed && remote > local)
                    {
                        if (IsHandleCreated && !IsDisposed)
                        {
                            if (manual)
                            {
                                BeginInvoke(new Action<string>(PromptUpdate), remoteVersion);
                            }
                            else
                            {
                                BeginInvoke(new Action<string>(MarkUpdateAvailable), remoteVersion);
                            }
                        }
                    }
                    else if (manual && IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show(this, string.Format("У вас найновіша версія ({0}).", AppVersion),
                                "Перевірка оновлень", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }));
                    }
                }
                catch
                {
                    if (manual && IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show(this, "Не вдалося перевірити оновлення. Перевірте підключення до інтернету.",
                                "Перевірка оновлень", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }));
                    }
                }
            });
        }

        private static string DownloadString(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "ShadowSessionTool");
                return wc.DownloadString(url);
            }
        }

        private void PromptUpdate(string newVersion)
        {
            DialogResult result = MessageBox.Show(this,
                string.Format("Доступна нова версія {0} (поточна {1}). Оновити зараз?", newVersion, AppVersion),
                "Оновлення", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                PerformUpdate();
            }
        }

        private void MarkUpdateAvailable(string newVersion)
        {
            _pendingUpdateVersion = newVersion;
            lblVersion.Text = string.Format("v{0} ↑", AppVersion);
            lblVersion.ForeColor = Color.OrangeRed;
        }

        private void ShowLocalIp()
        {
            List<string> addresses = new List<string>();

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            addresses.Add(addr.Address.ToString());
                        }
                    }
                }
            }
            catch
            {
                // не критично, просто не покажемо локальні адреси
            }

            foreach (Label old in _localIpLabels) Controls.Remove(old);
            _localIpLabels.Clear();

            Font ipFont = new Font("Segoe UI", 8F);
            int y = 152;

            Label caption = new Label
            {
                AutoSize = true,
                Font = ipFont,
                Location = new Point(12, y),
                Text = "Локальний IP:",
                ForeColor = lblHint.ForeColor
            };
            Controls.Add(caption);
            _localIpLabels.Add(caption);
            y += caption.PreferredHeight;

            if (addresses.Count == 0)
            {
                Label none = new Label
                {
                    AutoSize = true,
                    Font = ipFont,
                    Location = new Point(12, y),
                    Text = "невідомо",
                    ForeColor = lblHint.ForeColor
                };
                Controls.Add(none);
                _localIpLabels.Add(none);
            }
            else
            {
                foreach (string addr in addresses)
                {
                    string captured = addr;
                    Label lbl = new Label
                    {
                        AutoSize = true,
                        Font = ipFont,
                        Cursor = Cursors.Hand,
                        Location = new Point(12, y),
                        Text = addr,
                        ForeColor = lblHint.ForeColor
                    };
                    lbl.Click += (s, e) => CopyToClipboard(captured);
                    Controls.Add(lbl);
                    _localIpLabels.Add(lbl);
                    y += lbl.PreferredHeight;
                }
            }
        }

        private void FetchExternalIpAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string ip = null;
                try
                {
                    ip = DownloadString("https://api.ipify.org").Trim();
                }
                catch
                {
                    // немає інтернету або сервіс недоступний
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _externalIp = ip;
                        lblExternalIp.Text = string.IsNullOrEmpty(ip) ? "недоступно" : ip;
                    }));
                }
            });
        }

        private void PerformUpdate()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string dir = Path.GetDirectoryName(exePath);
                string newExePath = Path.Combine(dir, "ShadowSessionTool.exe.new");

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "ShadowSessionTool");
                    wc.DownloadFile(UpdateExeUrl, newExePath);
                }

                string batPath = Path.Combine(Path.GetTempPath(), "ssttupdate_" + Guid.NewGuid().ToString("N") + ".bat");

                StringBuilder bat = new StringBuilder();
                bat.AppendLine("@echo off");
                bat.AppendLine("taskkill /F /IM ShadowSessionTool.exe >nul 2>&1");
                bat.AppendLine(":wait");
                bat.AppendLine("tasklist /fi \"IMAGENAME eq ShadowSessionTool.exe\" | find /I \"ShadowSessionTool.exe\" >nul");
                bat.AppendLine("if not errorlevel 1 (");
                bat.AppendLine("  timeout /t 1 /nobreak >nul");
                bat.AppendLine("  goto wait");
                bat.AppendLine(")");
                bat.AppendLine(string.Format("move /y \"{0}\" \"{1}\"", newExePath, exePath));
                bat.AppendLine(string.Format("start \"\" \"{0}\"", exePath));
                bat.AppendLine("del \"%~f0\"");

                File.WriteAllText(batPath, bat.ToString(), Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + batPath + "\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося оновити застосунок: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
