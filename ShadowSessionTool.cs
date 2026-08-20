using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShadowSessionTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
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
            WTSUserName = 5
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

        private const string AppVersion = "1.1.0";
        private const string UpdateVersionUrl = "https://raw.githubusercontent.com/Pro100NeFarT/ShadowSessionTool/main/version.txt";
        private const string UpdateExeUrl = "https://raw.githubusercontent.com/Pro100NeFarT/ShadowSessionTool/main/ShadowSessionTool.exe";

        private Button btnThemeLight;
        private Button btnThemeDark;
        private Button btnThemeBlue;

        private ListView lvSessions;
        private Button btnRefresh;
        private Button btnConnect;
        private Button btnDisconnect;
        private Button btnDisconnectAll;
        private Panel pnlIndicator;
        private Label lblStatus;
        private Button btnEnablePolicy;
        private Label lblHint;
        private Label lblSelect;
        private TextBox txtSearch;

        private ContextMenuStrip ctxMenu;
        private ToolStripMenuItem miCtxConnect;
        private ToolStripMenuItem miCtxDisconnect;
        private ToolStripMenuItem miCtxMessage;
        private ToolStripMenuItem miCtxMessageAll;

        private List<RdpSession> _allSessions = new List<RdpSession>();
        private int _sortColumn = -1;
        private SortOrder _sortOrder = SortOrder.None;
        private readonly int _ownSessionId = Process.GetCurrentProcess().SessionId;

        public MainForm()
        {
            InitializeComponent();
            Shown += (s, e) =>
            {
                SendMessage(txtSearch.Handle, EM_SETCUEBANNER, IntPtr.Zero, "Пошук за користувачем...");
                ApplyTheme(LoadSavedTheme());
                RefreshSessions();
                RefreshPolicyStatus();
                CheckForUpdatesAsync();
            };
        }

        private void InitializeComponent()
        {
            Text = "Засіб тіньових сеансів";
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(700, 630);
            MinimumSize = new Size(660, 550);
            StartPosition = FormStartPosition.CenterScreen;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // без іконки застосунок все одно працює коректно
            }

            btnThemeLight = new Button { Text = "Світла", Size = new Size(70, 22), Location = new Point(466, 8), Font = new Font("Segoe UI", 8F) };
            btnThemeLight.Click += (s, e) => ApplyTheme(AppTheme.Light);

            btnThemeDark = new Button { Text = "Темна", Size = new Size(70, 22), Location = new Point(542, 8), Font = new Font("Segoe UI", 8F) };
            btnThemeDark.Click += (s, e) => ApplyTheme(AppTheme.Dark);

            btnThemeBlue = new Button { Text = "Синя", Size = new Size(70, 22), Location = new Point(618, 8), Font = new Font("Segoe UI", 8F) };
            btnThemeBlue.Click += (s, e) => ApplyTheme(AppTheme.Blue);

            lblSelect = new Label
            {
                Text = "Виберіть сеанс для підключення:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(12, 45)
            };

            btnRefresh = new Button { Text = "Оновити", Size = new Size(110, 28), Location = new Point(438, 40) };
            btnRefresh.Click += (s, e) => { RefreshSessions(); RefreshPolicyStatus(); };

            btnConnect = new Button { Text = "Підключитися", Size = new Size(130, 28), Location = new Point(558, 40), Enabled = false };
            btnConnect.Click += BtnConnect_Click;

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
                MaximumSize = new Size(480, 0),
                Location = new Point(36, 78),
                Text = "Перевірка..."
            };

            btnEnablePolicy = new Button
            {
                Text = "Увімкнути дозвіл",
                Size = new Size(160, 28),
                Location = new Point(528, 74),
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
                Size = new Size(140, 28),
                Location = new Point(272, 152),
                Enabled = false
            };
            btnDisconnect.Click += BtnDisconnect_Click;

            btnDisconnectAll = new Button
            {
                Text = "Завершити відключені",
                Size = new Size(190, 28),
                Location = new Point(420, 152)
            };
            btnDisconnectAll.Click += BtnDisconnectAll_Click;

            txtSearch = new TextBox
            {
                Location = new Point(12, 188),
                Size = new Size(170, 23)
            };
            txtSearch.TextChanged += (s, e) => ApplyFilter();

            miCtxConnect = new ToolStripMenuItem("Підключитися");
            miCtxConnect.Click += BtnConnect_Click;
            miCtxDisconnect = new ToolStripMenuItem("Завершити сеанс");
            miCtxDisconnect.Click += BtnDisconnect_Click;
            miCtxMessage = new ToolStripMenuItem("Надіслати повідомлення");
            miCtxMessage.Click += MiCtxMessage_Click;
            miCtxMessageAll = new ToolStripMenuItem("Надіслати повідомлення всім");
            miCtxMessageAll.Click += MiCtxMessageAll_Click;

            ctxMenu = new ContextMenuStrip();
            ctxMenu.Items.Add(miCtxConnect);
            ctxMenu.Items.Add(miCtxDisconnect);
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
                Location = new Point(12, 218),
                Size = new Size(676, ClientSize.Height - 218 - 12),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ContextMenuStrip = ctxMenu
            };
            lvSessions.Columns.Add("Користувач", 170);
            lvSessions.Columns.Add("ID сеансу", 90);
            lvSessions.Columns.Add("Стан", 130);
            lvSessions.Columns.Add("Ім'я сеансу", 150);
            lvSessions.DoubleClick += (s, e) => BtnConnect_Click(s, e);
            lvSessions.SelectedIndexChanged += (s, e) => UpdateActionButtons();
            lvSessions.ColumnClick += LvSessions_ColumnClick;
            lvSessions.MouseDown += LvSessions_MouseDown;

            Controls.Add(btnThemeLight);
            Controls.Add(btnThemeDark);
            Controls.Add(btnThemeBlue);
            Controls.Add(lblSelect);
            Controls.Add(btnRefresh);
            Controls.Add(btnConnect);
            Controls.Add(pnlIndicator);
            Controls.Add(lblStatus);
            Controls.Add(btnEnablePolicy);
            Controls.Add(lblHint);
            Controls.Add(btnDisconnect);
            Controls.Add(btnDisconnectAll);
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
            miCtxDisconnect.Enabled = (selCount >= 1);
            miCtxDisconnect.Text = selCount > 1
                ? string.Format("Завершити сеанси ({0})", selCount)
                : "Завершити сеанс";

            miCtxMessage.Enabled = (selCount >= 1);
            miCtxMessage.Text = selCount > 1
                ? string.Format("Надіслати повідомлення ({0})", selCount)
                : "Надіслати повідомлення";
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
            btnConnect.Enabled = (selCount == 1) && !IsOwnSessionSelected();
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

            try
            {
                Process.Start("mstsc.exe", string.Format("/shadow:{0} /control /noConsentPrompt", id));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося запустити підключення: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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

            string message = ShowInputDialog(prompt, "Надіслати повідомлення");
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
                "Надіслати повідомлення всім");
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

        private string ShowInputDialog(string prompt, string title)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(400, 168);
                dlg.Font = Font;
                dlg.BackColor = BackColor;

                Label lbl = new Label
                {
                    Text = prompt,
                    AutoSize = true,
                    MaximumSize = new Size(376, 0),
                    Location = new Point(12, 12),
                    ForeColor = lblSelect.ForeColor
                };

                TextBox txt = new TextBox
                {
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    Location = new Point(12, 40),
                    Size = new Size(376, 80),
                    BackColor = txtSearch.BackColor,
                    ForeColor = txtSearch.ForeColor
                };

                Button ok = new Button
                {
                    Text = "Надіслати",
                    DialogResult = DialogResult.OK,
                    Location = new Point(216, 128),
                    Size = new Size(80, 28),
                    FlatStyle = btnConnect.FlatStyle,
                    BackColor = btnConnect.BackColor,
                    ForeColor = btnConnect.ForeColor
                };
                ok.FlatAppearance.BorderColor = btnConnect.FlatAppearance.BorderColor;

                Button cancel = new Button
                {
                    Text = "Скасувати",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(308, 128),
                    Size = new Size(80, 28),
                    FlatStyle = btnConnect.FlatStyle,
                    BackColor = btnConnect.BackColor,
                    ForeColor = btnConnect.ForeColor
                };
                cancel.FlatAppearance.BorderColor = btnConnect.FlatAppearance.BorderColor;

                dlg.Controls.Add(lbl);
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
            return AppTheme.Light;
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

            txtSearch.BackColor = textBoxBack;
            txtSearch.ForeColor = textBoxFore;

            lvSessions.BackColor = listBack;
            lvSessions.ForeColor = listFore;

            Button[] buttons = { btnRefresh, btnConnect, btnEnablePolicy, btnDisconnect, btnDisconnectAll, btnThemeLight, btnThemeDark, btnThemeBlue };
            foreach (Button btn in buttons)
            {
                btn.FlatStyle = btnStyle;
                btn.BackColor = controlBack;
                btn.ForeColor = controlFore;
                btn.FlatAppearance.BorderColor = btnBorder;
            }

            btnThemeLight.Font = new Font(btnThemeLight.Font, theme == AppTheme.Light ? FontStyle.Bold : FontStyle.Regular);
            btnThemeDark.Font = new Font(btnThemeDark.Font, theme == AppTheme.Dark ? FontStyle.Bold : FontStyle.Regular);
            btnThemeBlue.Font = new Font(btnThemeBlue.Font, theme == AppTheme.Blue ? FontStyle.Bold : FontStyle.Regular);

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

        private void RefreshSessions()
        {
            _allSessions = GetRdpSessions();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            lvSessions.Items.Clear();
            string filter = txtSearch.Text.Trim();

            foreach (RdpSession s in _allSessions)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    s.UserName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isOwn = false;
                int id;
                if (int.TryParse(s.Id, out id) && id == _ownSessionId) isOwn = true;

                ListViewItem item = new ListViewItem(s.UserName);
                item.SubItems.Add(s.Id);
                item.SubItems.Add(s.State);
                item.SubItems.Add(isOwn ? s.SessionName + " (поточний)" : s.SessionName);
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

                    result.Add(new RdpSession
                    {
                        UserName = userName,
                        SessionName = stationName,
                        Id = si.SessionID.ToString(),
                        State = TranslateState(si.State),
                        RawState = si.State
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

        private void CheckForUpdatesAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string remoteVersion = DownloadString(UpdateVersionUrl).Trim();
                    Version remote, local;
                    if (Version.TryParse(remoteVersion, out remote) && Version.TryParse(AppVersion, out local) && remote > local)
                    {
                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke(new Action<string>(PromptUpdate), remoteVersion);
                        }
                    }
                }
                catch
                {
                    // немає інтернету, репозиторій недоступний тощо - тихо ігноруємо
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

                int pid = Process.GetCurrentProcess().Id;
                string batPath = Path.Combine(Path.GetTempPath(), "ssttupdate_" + Guid.NewGuid().ToString("N") + ".bat");

                StringBuilder bat = new StringBuilder();
                bat.AppendLine("@echo off");
                bat.AppendLine(":wait");
                bat.AppendLine(string.Format("tasklist /fi \"PID eq {0}\" | find \"{0}\" >nul", pid));
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
