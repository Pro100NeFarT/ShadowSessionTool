using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            WTSWinStationName = 6,
            WTSDomainName = 7
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
        public string FullName;
        public string DomainName;
    }

    internal class UserAccountInfo
    {
        public string Description = "";
        public string FullName = "";
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

        internal static readonly string[] ServiceNames =
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

        internal static ServiceController TryGetService(string name)
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

        /// <summary>Запускає всі встановлені, але не запущені служби 1С/BAF. Не деструктивна дія - без попереджень.</summary>
        internal static CleanupResult StartServices()
        {
            CleanupResult result = new CleanupResult();
            foreach (string svcName in ServiceNames)
            {
                ServiceController sc = TryGetService(svcName);
                if (sc == null) continue;

                try
                {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        result.ServicesProcessed.Add(svcName + ": вже запущено");
                        continue;
                    }

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                    result.ServicesProcessed.Add(svcName + ": запущено");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Не вдалося запустити службу \"" + svcName + "\": " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Зупиняє всі встановлені й запущені служби 1С/BAF. Виклик попередження активним сеансам - відповідальність UI-рівня.</summary>
        internal static CleanupResult StopServices()
        {
            CleanupResult result = new CleanupResult();
            foreach (string svcName in ServiceNames)
            {
                ServiceController sc = TryGetService(svcName);
                if (sc == null) continue;

                try
                {
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        result.ServicesProcessed.Add(svcName + ": вже зупинено");
                        continue;
                    }

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    result.ServicesProcessed.Add(svcName + ": зупинено");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Не вдалося зупинити службу \"" + svcName + "\": " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Перезапускає встановлені й запущені служби 1С/BAF (без очищення кешу, на відміну від Run()).</summary>
        internal static CleanupResult RestartServices()
        {
            CleanupResult result = new CleanupResult();
            foreach (string svcName in ServiceNames)
            {
                ServiceController sc = TryGetService(svcName);
                if (sc == null) continue;

                try
                {
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        result.ServicesProcessed.Add(svcName + ": не запущено, пропущено");
                        continue;
                    }

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                    result.ServicesProcessed.Add(svcName + ": перезапущено");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Не вдалося перезапустити службу \"" + svcName + "\": " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Статус кожної відомої служби 1С/BAF: "Running"/"Stopped"/... або null, якщо не встановлена.</summary>
        internal static Dictionary<string, string> GetServiceStatuses()
        {
            Dictionary<string, string> statuses = new Dictionary<string, string>();
            foreach (string svcName in ServiceNames)
            {
                ServiceController sc = TryGetService(svcName);
                if (sc == null)
                {
                    statuses[svcName] = null;
                    continue;
                }
                try
                {
                    sc.Refresh();
                    statuses[svcName] = sc.Status.ToString();
                }
                catch
                {
                    statuses[svcName] = null;
                }
            }
            return statuses;
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

    internal class IbaseSection
    {
        public string Name;
        public int StartLine;
        public int EndLine;
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsDatabase { get { return Props.ContainsKey("Connect"); } }

        public string Folder
        {
            get
            {
                string f;
                return Props.TryGetValue("Folder", out f) ? f : "/";
            }
        }
    }

    internal static class IbaseFile
    {
        private static readonly string[] ExtraDbKeys =
        {
            "ClientConnectionSpeed", "App", "WA", "Version", "AppArch",
            "DisableLocalSpeechToText", "DefaultApp", "DefaultVersion", "UseProxy", "WSA"
        };

        internal static string GetPathForUser(string userName)
        {
            int slashIdx = userName.IndexOf('\\');
            string plain = slashIdx >= 0 ? userName.Substring(slashIdx + 1) : userName;
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") + "\\";
            return Path.Combine(systemDrive, "Users", plain, "AppData", "Roaming", "1C", "1CEStart", "ibases.v8i");
        }

        internal static List<IbaseSection> Parse(string[] lines)
        {
            List<IbaseSection> sections = new List<IbaseSection>();
            IbaseSection current = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (line.Length > 2 && line[0] == '[' && line[line.Length - 1] == ']')
                {
                    if (current != null) current.EndLine = i;
                    current = new IbaseSection { Name = line.Substring(1, line.Length - 2), StartLine = i };
                    sections.Add(current);
                    continue;
                }
                if (current == null) continue;

                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    current.Props[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            if (current != null) current.EndLine = lines.Length;

            return sections;
        }

        internal static List<IbaseSection> ParseFile(string path)
        {
            if (!File.Exists(path)) return new List<IbaseSection>();
            return Parse(File.ReadAllLines(path, Encoding.UTF8));
        }

        internal static void DeleteSections(string path, List<IbaseSection> toDelete)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            bool[] exclude = new bool[lines.Length];
            foreach (IbaseSection s in toDelete)
            {
                for (int i = s.StartLine; i < s.EndLine && i < lines.Length; i++) exclude[i] = true;
            }

            List<string> kept = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!exclude[i]) kept.Add(lines[i]);
            }

            File.WriteAllLines(path, kept.ToArray(), new UTF8Encoding(false));
        }

        private static void AppendFolderBlock(IbaseSection s, List<string> outLines)
        {
            outLines.Add("[" + s.Name + "]");
            outLines.Add("ID=" + Guid.NewGuid());
            outLines.Add("OrderInList=-1");
            outLines.Add("Folder=" + (string.IsNullOrEmpty(s.Folder) ? "/" : s.Folder));
            outLines.Add("OrderInTree=0");
            outLines.Add("External=0");
        }

        private static void AppendDatabaseBlock(IbaseSection s, List<string> outLines)
        {
            outLines.Add("[" + s.Name + "]");
            string conn;
            s.Props.TryGetValue("Connect", out conn);
            outLines.Add("Connect=" + conn);
            outLines.Add("ID=" + Guid.NewGuid());
            outLines.Add("OrderInList=-1");
            outLines.Add("Folder=" + (string.IsNullOrEmpty(s.Folder) ? "/" : s.Folder));
            outLines.Add("OrderInTree=0");
            outLines.Add("External=0");

            foreach (string key in ExtraDbKeys)
            {
                string val;
                if (s.Props.TryGetValue(key, out val)) outLines.Add(key + "=" + val);
            }
        }

        private static void EnsureFolderChain(string folderRef, List<IbaseSection> sourceSections,
            Dictionary<string, bool> existingNames, Dictionary<string, bool> ensuredFolders, List<string> outLines)
        {
            if (string.IsNullOrEmpty(folderRef) || folderRef == "/") return;

            string folderName = folderRef.TrimStart('/');
            if (existingNames.ContainsKey(folderName)) return;
            if (ensuredFolders.ContainsKey(folderName)) return;

            IbaseSection folderSection = null;
            foreach (IbaseSection s in sourceSections)
            {
                if (!s.IsDatabase && string.Equals(s.Name, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    folderSection = s;
                    break;
                }
            }
            if (folderSection == null) return;

            EnsureFolderChain(folderSection.Folder, sourceSections, existingNames, ensuredFolders, outLines);

            AppendFolderBlock(folderSection, outLines);
            existingNames[folderName] = true;
            ensuredFolders[folderName] = true;
        }

        internal class AddResult
        {
            public int Added;
            public int Skipped;
        }

        internal static AddResult AddDatabases(string targetPath, List<IbaseSection> selectedDbs, List<IbaseSection> sourceSections)
        {
            AddResult result = new AddResult();

            string dir = Path.GetDirectoryName(targetPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            List<IbaseSection> existing = ParseFile(targetPath);

            Dictionary<string, bool> existingConnects = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> existingNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (IbaseSection s in existing)
            {
                existingNames[s.Name] = true;
                string conn;
                if (s.Props.TryGetValue("Connect", out conn)) existingConnects[conn.Trim()] = true;
            }

            Dictionary<string, bool> ensuredFolders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<string> newLines = new List<string>();

            foreach (IbaseSection dbSection in selectedDbs)
            {
                string conn;
                if (!dbSection.Props.TryGetValue("Connect", out conn)) continue;

                if (existingConnects.ContainsKey(conn.Trim()))
                {
                    result.Skipped++;
                    continue;
                }

                EnsureFolderChain(dbSection.Folder, sourceSections, existingNames, ensuredFolders, newLines);

                AppendDatabaseBlock(dbSection, newLines);
                existingConnects[conn.Trim()] = true;
                existingNames[dbSection.Name] = true;
                result.Added++;
            }

            if (newLines.Count > 0)
            {
                bool fileExisted = File.Exists(targetPath);
                using (StreamWriter sw = new StreamWriter(targetPath, true, new UTF8Encoding(false)))
                {
                    if (fileExisted) sw.WriteLine();
                    foreach (string line in newLines) sw.WriteLine(line);
                }
            }

            return result;
        }
    }

    /// <summary>Профіль віддаленого сервера (розділ 3 плану) - зберігається в реєстрі, по одному підключу на профіль.</summary>
    internal class ServerProfile
    {
        public Guid Id = Guid.NewGuid();
        public string Name = "";
        public string Host = "";
        public int Port = RemoteProtocol.DefaultPort;
        public string User = "";
        public string Password = "";
        public double LastPingMs = -1; // -1 = ще не перевірено, -2 = недоступний
    }

    /// <summary>Захист секретів (паролі профілів і "Дозволити керування") через DPAPI - System.Security.dll.</summary>
    internal static class CryptoHelper
    {
        internal static string ProtectString(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] protectedData = System.Security.Cryptography.ProtectedData.Protect(
                    data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedData);
            }
            catch
            {
                return "";
            }
        }

        internal static string UnprotectString(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return "";
            try
            {
                byte[] protectedData = Convert.FromBase64String(protectedBase64);
                byte[] data = System.Security.Cryptography.ProtectedData.Unprotect(
                    protectedData, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>Зберігання профілів серверів і налаштувань "Дозволити керування" під Software\ShadowSessionTool (розділ 3).</summary>
    internal static class ServerProfileStore
    {
        private const string ServersSubKey = @"Software\ShadowSessionTool\Servers";
        private const string RemoteControlSubKey = @"Software\ShadowSessionTool\RemoteControl";

        internal static List<ServerProfile> LoadProfiles()
        {
            List<ServerProfile> result = new List<ServerProfile>();
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(ServersSubKey))
                {
                    if (root == null) return result;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using (RegistryKey key = root.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            ServerProfile p = new ServerProfile();
                            Guid id;
                            p.Id = Guid.TryParse(sub, out id) ? id : Guid.NewGuid();
                            p.Name = Convert.ToString(key.GetValue("Name", ""));
                            p.Host = Convert.ToString(key.GetValue("Host", ""));
                            p.Port = Convert.ToInt32(key.GetValue("Port", RemoteProtocol.DefaultPort));
                            p.User = Convert.ToString(key.GetValue("User", ""));
                            p.Password = CryptoHelper.UnprotectString(Convert.ToString(key.GetValue("Password", "")));
                            result.Add(p);
                        }
                    }
                }
            }
            catch
            {
                // не критично - повертаємо те, що встигли завантажити
            }
            return result;
        }

        internal static void SaveProfile(ServerProfile p)
        {
            using (RegistryKey root = Registry.CurrentUser.CreateSubKey(ServersSubKey))
            using (RegistryKey key = root.CreateSubKey(p.Id.ToString()))
            {
                key.SetValue("Name", p.Name ?? "");
                key.SetValue("Host", p.Host ?? "");
                key.SetValue("Port", p.Port, RegistryValueKind.DWord);
                key.SetValue("User", p.User ?? "");
                key.SetValue("Password", CryptoHelper.ProtectString(p.Password ?? ""));
            }
        }

        internal static void DeleteProfile(Guid id)
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(ServersSubKey, true))
                {
                    if (root != null) root.DeleteSubKeyTree(id.ToString(), false);
                }
            }
            catch
            {
                // не критично
            }
        }

        internal class RemoteControlSettings
        {
            public bool Enabled;
            public int Port = RemoteProtocol.DefaultPort;
            public string User = "";
            public string Password = "";
        }

        internal static RemoteControlSettings LoadRemoteControlSettings()
        {
            RemoteControlSettings s = new RemoteControlSettings();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RemoteControlSubKey))
                {
                    if (key != null)
                    {
                        s.Enabled = Convert.ToInt32(key.GetValue("Enabled", 0)) != 0;
                        s.Port = Convert.ToInt32(key.GetValue("Port", RemoteProtocol.DefaultPort));
                        s.User = Convert.ToString(key.GetValue("User", ""));
                        s.Password = CryptoHelper.UnprotectString(Convert.ToString(key.GetValue("Password", "")));
                    }
                }
            }
            catch
            {
                // не критично - застосуємо вимкнений стан за замовчуванням
            }
            return s;
        }

        internal static void SaveRemoteControlSettings(RemoteControlSettings s)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RemoteControlSubKey))
            {
                key.SetValue("Enabled", s.Enabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Port", s.Port, RegistryValueKind.DWord);
                key.SetValue("User", s.User ?? "");
                key.SetValue("Password", CryptoHelper.ProtectString(s.Password ?? ""));
            }
        }
    }

    /// <summary>
    /// Кадрування/шифрування/(де)серіалізація протоколу віддаленого керування (розділ 5).
    /// Кадр: [4 байти довжини][IV(16)+AES-256-CBC-PKCS7 шифротекст]. Payload у відкритому вигляді:
    /// "КОМАНДА\nключ1=значення1\nключ2=значення2\n..." (значення екранують \ і переноси рядків).
    /// </summary>
    internal static class RemoteProtocol
    {
        internal const int DefaultPort = 51823;

        // Сіль KDF - не є секретом (секрет це пароль профілю/"Дозволити керування"), однакова на клієнті й сервері.
        private static readonly byte[] KeySalt = Encoding.UTF8.GetBytes("ShadowSessionTool.RemoteControl.Salt.v1");

        internal static byte[] DeriveKey(string password)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(password ?? "", KeySalt, 10000))
            {
                return kdf.GetBytes(32);
            }
        }

        private static RijndaelManaged CreateAes()
        {
            RijndaelManaged aes = new RijndaelManaged();
            aes.BlockSize = 128;
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes;
        }

        internal static byte[] Encrypt(byte[] key, string plainText)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            using (RijndaelManaged aes = CreateAes())
            {
                aes.Key = key;
                aes.GenerateIV();
                byte[] iv = aes.IV;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    byte[] cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    byte[] combined = new byte[iv.Length + cipher.Length];
                    Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
                    Buffer.BlockCopy(cipher, 0, combined, iv.Length, cipher.Length);
                    return combined;
                }
            }
        }

        internal static string Decrypt(byte[] key, byte[] combined)
        {
            if (combined.Length < 16) throw new CryptographicException("Кадр закороткий.");
            using (RijndaelManaged aes = CreateAes())
            {
                aes.Key = key;
                byte[] iv = new byte[16];
                Buffer.BlockCopy(combined, 0, iv, 0, 16);
                aes.IV = iv;
                using (ICryptoTransform dec = aes.CreateDecryptor())
                {
                    byte[] cipher = new byte[combined.Length - 16];
                    Buffer.BlockCopy(combined, 16, cipher, 0, cipher.Length);
                    byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }

        internal static void WriteFrame(NetworkStream stream, byte[] data)
        {
            byte[] lenBytes = BitConverter.GetBytes(data.Length);
            stream.Write(lenBytes, 0, 4);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        internal static byte[] ReadFrame(NetworkStream stream, int maxSize)
        {
            byte[] lenBytes = ReadExact(stream, 4);
            int len = BitConverter.ToInt32(lenBytes, 0);
            if (len < 0 || len > maxSize) throw new IOException("Некоректний розмір кадру протоколу: " + len);
            return ReadExact(stream, len);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read <= 0) throw new IOException("З'єднання закрито передчасно.");
                offset += read;
            }
            return buf;
        }

        internal static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "");
        }

        internal static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == 'n') { sb.Append('\n'); i++; continue; }
                    if (next == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string BuildMessage(string command, Dictionary<string, string> fields)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(command).Append('\n');
            if (fields != null)
            {
                foreach (KeyValuePair<string, string> kv in fields)
                {
                    sb.Append(kv.Key).Append('=').Append(Escape(kv.Value)).Append('\n');
                }
            }
            return sb.ToString();
        }

        internal class ParsedMessage
        {
            public string Command = "";
            public readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Get(string key)
            {
                string v;
                return Fields.TryGetValue(key, out v) ? v : "";
            }
        }

        internal static ParsedMessage ParseMessage(string text)
        {
            ParsedMessage msg = new ParsedMessage();
            string[] lines = text.Split('\n');
            if (lines.Length == 0) return msg;
            msg.Command = lines[0];
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                msg.Fields[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
            }
            return msg;
        }

        // Внутрішньополеві розділювачі (не \n, щоб уникнути подвійного екранування) - для списків сеансів/служб/секцій ibase.
        internal const char FieldSep = '\x01';
        internal const char RecordSep = '\x02';
        internal const char ExtraKvSep = '\x03';

        internal static string JoinFields(params string[] fields)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(FieldSep);
                string f = fields[i] ?? "";
                sb.Append(f.Replace(FieldSep, ' ').Replace(RecordSep, ' '));
            }
            return sb.ToString();
        }

        internal static string[] SplitFields(string joined)
        {
            return (joined ?? "").Split(FieldSep);
        }
    }

    /// <summary>
    /// Клієнтська сторона протоколу (розділ 5/6) - кожен виклик відкриває окреме TCP-з'єднання,
    /// шифрує запит паролем профілю й чекає зашифровану відповідь. Викликати лише з фонового потоку.
    /// </summary>
    internal static class RemoteClient
    {
        internal class RemoteException : Exception
        {
            internal RemoteException(string message) : base(message) { }
        }

        private static RemoteProtocol.ParsedMessage SendCommand(ServerProfile profile, string command, Dictionary<string, string> fields)
        {
            if (fields == null) fields = new Dictionary<string, string>();
            fields["user"] = profile.User ?? "";

            string plain = RemoteProtocol.BuildMessage(command, fields);
            byte[] key = RemoteProtocol.DeriveKey(profile.Password);
            byte[] encrypted = RemoteProtocol.Encrypt(key, plain);

            using (TcpClient client = new TcpClient())
            {
                IAsyncResult ar = client.BeginConnect(profile.Host, profile.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000))
                {
                    throw new RemoteException(string.Format("Час очікування з'єднання з {0}:{1} минув.", profile.Host, profile.Port));
                }
                client.EndConnect(ar);

                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 20000;
                    stream.WriteTimeout = 20000;

                    RemoteProtocol.WriteFrame(stream, encrypted);
                    byte[] responseEncrypted = RemoteProtocol.ReadFrame(stream, 32 * 1024 * 1024);

                    string responsePlain;
                    try
                    {
                        responsePlain = RemoteProtocol.Decrypt(key, responseEncrypted);
                    }
                    catch (Exception)
                    {
                        throw new RemoteException("Не вдалося розшифрувати відповідь сервера (перевірте пароль профілю).");
                    }

                    RemoteProtocol.ParsedMessage response = RemoteProtocol.ParseMessage(responsePlain);
                    if (!string.Equals(response.Command, "OK", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = response.Get("message");
                        throw new RemoteException(string.IsNullOrEmpty(msg) ? "Сервер повернув помилку." : msg);
                    }
                    return response;
                }
            }
        }

        private static string JoinIds(List<int> ids)
        {
            List<string> s = new List<string>();
            foreach (int id in ids) s.Add(id.ToString());
            return string.Join(",", s.ToArray());
        }

        internal static List<int> ParseIds(string csv)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrEmpty(csv)) return result;
            foreach (string p in csv.Split(','))
            {
                int id;
                if (int.TryParse(p, out id)) result.Add(id);
            }
            return result;
        }

        internal static List<RdpSession> ListSessions(ServerProfile profile)
        {
            RemoteProtocol.ParsedMessage resp = SendCommand(profile, "LIST_SESSIONS", null);
            List<RdpSession> result = new List<RdpSession>();
            int count;
            int.TryParse(resp.Get("count"), out count);
            for (int i = 0; i < count; i++)
            {
                string line = resp.Get("session" + i);
                string[] f = RemoteProtocol.SplitFields(line);
                if (f.Length < 9) continue;
                WtsConnectState rawState;
                Enum.TryParse(f[4], out rawState);
                int oneCCount;
                int.TryParse(f[5], out oneCCount);
                result.Add(new RdpSession
                {
                    UserName = f[0],
                    SessionName = f[1],
                    Id = f[2],
                    State = f[3],
                    RawState = rawState,
                    OneCCount = oneCCount,
                    Description = f[6],
                    FullName = f[7],
                    DomainName = f[8]
                });
            }
            return result;
        }

        internal static void SendMessage(ServerProfile profile, List<int> ids, string message)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["ids"] = JoinIds(ids);
            fields["message"] = message;
            SendCommand(profile, "SEND_MESSAGE", fields);
        }

        internal static void LogoffSessions(ServerProfile profile, List<int> ids)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["ids"] = JoinIds(ids);
            SendCommand(profile, "LOGOFF_SESSIONS", fields);
        }

        internal static string ShadowInfo(ServerProfile profile, int sessionId)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["id"] = sessionId.ToString();
            RemoteProtocol.ParsedMessage resp = SendCommand(profile, "SHADOW_INFO", fields);
            return resp.Get("userName");
        }

        internal static void TakeOver(ServerProfile profile, int sessionId, string destStation, string targetPassword)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["id"] = sessionId.ToString();
            fields["destStation"] = destStation;
            fields["targetPassword"] = targetPassword;
            SendCommand(profile, "TAKE_OVER", fields);
        }

        internal static void End1C(ServerProfile profile, List<int> ids)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["ids"] = JoinIds(ids);
            SendCommand(profile, "END_1C", fields);
        }

        internal static void ClearCache1C(ServerProfile profile, List<int> ids, List<string> userNames)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["ids"] = JoinIds(ids);
            fields["userNames"] = string.Join(RemoteProtocol.FieldSep.ToString(), userNames.ToArray());
            SendCommand(profile, "CLEAR_CACHE_1C", fields);
        }

        private static ServerCacheCleanup.CleanupResult ParseCleanupResult(RemoteProtocol.ParsedMessage resp)
        {
            ServerCacheCleanup.CleanupResult result = new ServerCacheCleanup.CleanupResult();
            int n;
            int.TryParse(resp.Get("processedCount"), out n);
            for (int i = 0; i < n; i++) result.ServicesProcessed.Add(resp.Get("processed" + i));
            int m;
            int.TryParse(resp.Get("errorCount"), out m);
            for (int i = 0; i < m; i++) result.Errors.Add(resp.Get("error" + i));
            return result;
        }

        internal static ServerCacheCleanup.CleanupResult ServerCleanup(ServerProfile profile)
        {
            return ParseCleanupResult(SendCommand(profile, "SERVER_CLEANUP", null));
        }

        internal static ServerCacheCleanup.CleanupResult ServiceStart(ServerProfile profile)
        {
            return ParseCleanupResult(SendCommand(profile, "SERVICE_START", null));
        }

        internal static ServerCacheCleanup.CleanupResult ServiceStop(ServerProfile profile)
        {
            return ParseCleanupResult(SendCommand(profile, "SERVICE_STOP", null));
        }

        internal static ServerCacheCleanup.CleanupResult ServiceRestart(ServerProfile profile)
        {
            return ParseCleanupResult(SendCommand(profile, "SERVICE_RESTART", null));
        }

        internal static Dictionary<string, string> ServiceStatus(ServerProfile profile)
        {
            RemoteProtocol.ParsedMessage resp = SendCommand(profile, "SERVICE_STATUS", null);
            Dictionary<string, string> statuses = new Dictionary<string, string>();
            int n;
            int.TryParse(resp.Get("count"), out n);
            for (int i = 0; i < n; i++)
            {
                string[] f = RemoteProtocol.SplitFields(resp.Get("svc" + i));
                if (f.Length < 2) continue;
                statuses[f[0]] = (f[1].Length == 0) ? null : f[1];
            }
            return statuses;
        }

        internal static string SerializeSections(List<IbaseSection> sections)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sections.Count; i++)
            {
                if (i > 0) sb.Append(RemoteProtocol.RecordSep);
                IbaseSection s = sections[i];
                string connect;
                s.Props.TryGetValue("Connect", out connect);

                StringBuilder extra = new StringBuilder();
                bool first = true;
                foreach (KeyValuePair<string, string> kv in s.Props)
                {
                    if (string.Equals(kv.Key, "Connect", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key, "Folder", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first) extra.Append(RemoteProtocol.ExtraKvSep);
                    extra.Append(kv.Key).Append('=').Append(kv.Value);
                    first = false;
                }

                sb.Append(RemoteProtocol.JoinFields(s.Name, s.Folder, s.IsDatabase ? "1" : "0",
                    s.StartLine.ToString(), s.EndLine.ToString(), connect ?? "", extra.ToString()));
            }
            return sb.ToString();
        }

        internal static List<IbaseSection> DeserializeSectionsRaw(string data)
        {
            List<IbaseSection> result = new List<IbaseSection>();
            if (string.IsNullOrEmpty(data)) return result;
            foreach (string rec in data.Split(RemoteProtocol.RecordSep))
            {
                string[] f = RemoteProtocol.SplitFields(rec);
                if (f.Length < 7) continue;
                IbaseSection s = new IbaseSection();
                s.Name = f[0];
                int startLine, endLine;
                int.TryParse(f[3], out startLine);
                int.TryParse(f[4], out endLine);
                s.StartLine = startLine;
                s.EndLine = endLine;
                s.Props["Folder"] = f[1];
                bool isDb = f[2] == "1";
                if (isDb) s.Props["Connect"] = f[5];
                if (f[6].Length > 0)
                {
                    foreach (string pair in f[6].Split(RemoteProtocol.ExtraKvSep))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0) s.Props[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                    }
                }
                result.Add(s);
            }
            return result;
        }

        internal static List<IbaseSection> IbaseList(ServerProfile profile, string userName)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["userName"] = userName;
            RemoteProtocol.ParsedMessage resp = SendCommand(profile, "IBASE_LIST", fields);
            return DeserializeSectionsRaw(resp.Get("sections"));
        }

        internal static IbaseFile.AddResult IbaseAdd(ServerProfile profile, string targetUserName, List<IbaseSection> selectedDbs, List<IbaseSection> sourceSections)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["userName"] = targetUserName;
            fields["source"] = SerializeSections(sourceSections);
            List<string> names = new List<string>();
            foreach (IbaseSection s in selectedDbs) names.Add(s.Name);
            fields["selected"] = string.Join(RemoteProtocol.FieldSep.ToString(), names.ToArray());

            RemoteProtocol.ParsedMessage resp = SendCommand(profile, "IBASE_ADD", fields);
            IbaseFile.AddResult result = new IbaseFile.AddResult();
            int.TryParse(resp.Get("added"), out result.Added);
            int.TryParse(resp.Get("skipped"), out result.Skipped);
            return result;
        }

        internal static void IbaseDelete(ServerProfile profile, string userName, List<IbaseSection> toDelete)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["userName"] = userName;
            fields["sections"] = SerializeSections(toDelete);
            SendCommand(profile, "IBASE_DELETE", fields);
        }
    }

    /// <summary>
    /// Серверна сторона протоколу (розділ 5) - слухає TCP-порт у виділеному фоновому потоці й виконує ті самі
    /// локальні дії, що й UI, повертаючи результат клієнту. Кожен запит обробляється синхронно в окремому потоці.
    /// </summary>
    internal class RemoteControlServer
    {
        private TcpListener _listener;
        private System.Threading.Thread _acceptThread;
        private volatile bool _running;
        private string _user = "";
        private string _password = "";
        private int _port;

        internal event EventHandler<RemoteActivityEventArgs> Activity;
        private readonly System.Windows.Forms.Control _uiContext;

        private static readonly Dictionary<string, List<DateTime>> _authFailures = new Dictionary<string, List<DateTime>>();
        private static readonly object _authLock = new object();
        private const int MaxFailuresPerWindow = 5;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);

        internal RemoteControlServer(System.Windows.Forms.Control uiContext)
        {
            _uiContext = uiContext;
        }

        internal bool IsRunning { get { return _running; } }
        internal int Port { get { return _port; } }

        internal string Start(int port, string user, string password)
        {
            if (_running) return null;
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (Exception ex)
            {
                return string.Format("Не вдалося почати прослуховування порту {0}: {1}", port, ex.Message);
            }

            _port = port;
            _user = user ?? "";
            _password = password ?? "";
            _running = true;

            _acceptThread = new System.Threading.Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
            return null;
        }

        internal void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); }
            catch { /* не критично */ }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    // Stop() вже виставив _running=false перед зупинкою листенера - це навмисне завершення.
                    // Будь-яка інша помилка приймання (наприклад, транзиентний обрив з'єднання під час
                    // хендшейку на реальній мережі) не повинна навсамкінець вбивати потік прийому - інакше
                    // застосунок мовчки перестає приймати нові підключення, хоча тумблер і далі показує "Слухає".
                    if (!_running) break;
                    continue;
                }

                System.Threading.Thread t = new System.Threading.Thread(delegate() { HandleClient(client); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void RaiseActivity(string text)
        {
            EventHandler<RemoteActivityEventArgs> handler = Activity;
            if (handler == null || _uiContext == null) return;
            try
            {
                if (_uiContext.IsHandleCreated && !_uiContext.IsDisposed)
                {
                    _uiContext.BeginInvoke(new Action(delegate { handler(this, new RemoteActivityEventArgs(text)); }));
                }
            }
            catch
            {
                // форма могла закритись між перевіркою і викликом - не критично
            }
        }

        private void HandleClient(TcpClient client)
        {
            string ip = "?";
            try
            {
                IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
                if (ep != null) ip = ep.Address.ToString();

                if (IsBlocked(ip))
                {
                    client.Close();
                    return;
                }

                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 20000;
                    stream.WriteTimeout = 20000;

                    byte[] requestEncrypted = RemoteProtocol.ReadFrame(stream, 32 * 1024 * 1024);
                    byte[] key = RemoteProtocol.DeriveKey(_password);

                    string requestPlain;
                    try
                    {
                        requestPlain = RemoteProtocol.Decrypt(key, requestEncrypted);
                    }
                    catch
                    {
                        RecordFailure(ip);
                        return; // невірний пароль профілю - тихо розриваємо з'єднання
                    }

                    RemoteProtocol.ParsedMessage request = RemoteProtocol.ParseMessage(requestPlain);
                    if (!string.Equals(request.Get("user"), _user, StringComparison.Ordinal))
                    {
                        RecordFailure(ip);
                        return;
                    }

                    RaiseActivity(string.Format("{0}: {1} від {2}", DateTime.Now.ToString("HH:mm:ss"), request.Command, ip));

                    RemoteProtocol.ParsedMessage response = Dispatch(request);
                    string responsePlain = RemoteProtocol.BuildMessage(response.Command, response.Fields);
                    byte[] responseEncrypted = RemoteProtocol.Encrypt(key, responsePlain);
                    RemoteProtocol.WriteFrame(stream, responseEncrypted);
                }
            }
            catch
            {
                // з'єднання перервано / помилка вводу-виводу - не критично для сервера
            }
        }

        private static bool IsBlocked(string ip)
        {
            lock (_authLock)
            {
                List<DateTime> list;
                if (!_authFailures.TryGetValue(ip, out list)) return false;
                DateTime cutoff = DateTime.UtcNow - FailureWindow;
                list.RemoveAll(delegate(DateTime t) { return t < cutoff; });
                return list.Count >= MaxFailuresPerWindow;
            }
        }

        private static void RecordFailure(string ip)
        {
            lock (_authLock)
            {
                List<DateTime> list;
                if (!_authFailures.TryGetValue(ip, out list))
                {
                    list = new List<DateTime>();
                    _authFailures[ip] = list;
                }
                list.Add(DateTime.UtcNow);
            }
        }

        private static string GetField(RemoteProtocol.ParsedMessage msg, string key)
        {
            return msg.Get(key);
        }

        private static List<int> ParseIds(string csv)
        {
            return RemoteClient.ParseIds(csv);
        }

        private RemoteProtocol.ParsedMessage Dispatch(RemoteProtocol.ParsedMessage request)
        {
            try
            {
                switch ((request.Command ?? "").ToUpperInvariant())
                {
                    case "LIST_SESSIONS": return HandleListSessions();
                    case "SEND_MESSAGE": return HandleSendMessage(request);
                    case "LOGOFF_SESSIONS": return HandleLogoffSessions(request);
                    case "SHADOW_INFO": return HandleShadowInfo(request);
                    case "TAKE_OVER": return HandleTakeOver(request);
                    case "END_1C": return HandleEnd1C(request);
                    case "CLEAR_CACHE_1C": return HandleClearCache1C(request);
                    case "SERVER_CLEANUP": return CleanupResultResponse(ServerCacheCleanup.Run(true));
                    case "SERVICE_START": return CleanupResultResponse(ServerCacheCleanup.StartServices());
                    case "SERVICE_STOP": return CleanupResultResponse(ServerCacheCleanup.StopServices());
                    case "SERVICE_RESTART": return CleanupResultResponse(ServerCacheCleanup.RestartServices());
                    case "SERVICE_STATUS": return HandleServiceStatus();
                    case "IBASE_LIST": return HandleIbaseList(request);
                    case "IBASE_ADD": return HandleIbaseAdd(request);
                    case "IBASE_DELETE": return HandleIbaseDelete(request);
                    default: return ErrorResponse("Невідома команда: " + request.Command);
                }
            }
            catch (Exception ex)
            {
                return ErrorResponse("Помилка на сервері: " + ex.Message);
            }
        }

        private static RemoteProtocol.ParsedMessage OkResponse(Dictionary<string, string> fields)
        {
            RemoteProtocol.ParsedMessage msg = new RemoteProtocol.ParsedMessage();
            msg.Command = "OK";
            if (fields != null) foreach (KeyValuePair<string, string> kv in fields) msg.Fields[kv.Key] = kv.Value;
            return msg;
        }

        private static RemoteProtocol.ParsedMessage ErrorResponse(string message)
        {
            RemoteProtocol.ParsedMessage msg = new RemoteProtocol.ParsedMessage();
            msg.Command = "ERR";
            msg.Fields["message"] = message;
            return msg;
        }

        private static RemoteProtocol.ParsedMessage HandleListSessions()
        {
            List<RdpSession> sessions = MainForm.GetRdpSessions();
            Dictionary<string, UserAccountInfo> cache = new Dictionary<string, UserAccountInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (RdpSession s in sessions)
            {
                string key = (string.IsNullOrEmpty(s.DomainName) ? "" : s.DomainName + "\\") + s.UserName;
                UserAccountInfo info;
                if (!cache.TryGetValue(key, out info))
                {
                    info = MainForm.GetUserAccountInfo(s.UserName, s.DomainName);
                    cache[key] = info;
                }
                s.Description = info.Description;
                s.FullName = info.FullName;
            }

            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["count"] = sessions.Count.ToString();
            for (int i = 0; i < sessions.Count; i++)
            {
                RdpSession s = sessions[i];
                fields["session" + i] = RemoteProtocol.JoinFields(s.UserName, s.SessionName, s.Id, s.State,
                    s.RawState.ToString(), s.OneCCount.ToString(), s.Description, s.FullName, s.DomainName);
            }
            return OkResponse(fields);
        }

        private static RemoteProtocol.ParsedMessage HandleSendMessage(RemoteProtocol.ParsedMessage request)
        {
            List<int> ids = ParseIds(GetField(request, "ids"));
            string message = GetField(request, "message");
            List<string> failed = MainForm.SendMessageToSessionsCore(ids, message);
            if (failed.Count > 0) return ErrorResponse("Не вдалося надіслати повідомлення сеансу(ам): " + string.Join(", ", failed.ToArray()));
            return OkResponse(null);
        }

        private static RemoteProtocol.ParsedMessage HandleLogoffSessions(RemoteProtocol.ParsedMessage request)
        {
            List<int> ids = ParseIds(GetField(request, "ids"));
            List<string> failed = MainForm.LogoffSessionsCore(ids);
            if (failed.Count > 0) return ErrorResponse("Не вдалося завершити сеанс(и) з ID: " + string.Join(", ", failed.ToArray()));
            return OkResponse(null);
        }

        private static RemoteProtocol.ParsedMessage HandleShadowInfo(RemoteProtocol.ParsedMessage request)
        {
            int id;
            int.TryParse(GetField(request, "id"), out id);
            foreach (RdpSession s in MainForm.GetRdpSessions())
            {
                int sid;
                if (int.TryParse(s.Id, out sid) && sid == id)
                {
                    Dictionary<string, string> f = new Dictionary<string, string>();
                    f["userName"] = s.UserName;
                    return OkResponse(f);
                }
            }
            return ErrorResponse("Сеанс з ID " + id + " не знайдено на віддаленому сервері.");
        }

        private static RemoteProtocol.ParsedMessage HandleTakeOver(RemoteProtocol.ParsedMessage request)
        {
            int id;
            int.TryParse(GetField(request, "id"), out id);
            string destStation = GetField(request, "destStation");
            string password = GetField(request, "targetPassword");
            string error = MainForm.TakeOverCore(id, destStation, password);
            if (error != null) return ErrorResponse(error);
            return OkResponse(null);
        }

        private static RemoteProtocol.ParsedMessage HandleEnd1C(RemoteProtocol.ParsedMessage request)
        {
            List<int> ids = ParseIds(GetField(request, "ids"));
            List<Process> processes = MainForm.GetOneCProcessesForSessions(ids);
            if (processes.Count == 0) return OkResponse(null);
            List<string> failed = MainForm.KillProcesses(processes);
            if (failed.Count > 0) return ErrorResponse("Не вдалося завершити процес(и): " + string.Join(", ", failed.ToArray()));
            return OkResponse(null);
        }

        private static RemoteProtocol.ParsedMessage HandleClearCache1C(RemoteProtocol.ParsedMessage request)
        {
            List<int> ids = ParseIds(GetField(request, "ids"));
            string userNamesRaw = GetField(request, "userNames");
            List<string> userNames = new List<string>(userNamesRaw.Split(RemoteProtocol.FieldSep));

            List<Process> processes = MainForm.GetOneCProcessesForSessions(ids);
            List<string> failedKill = MainForm.KillProcesses(processes);
            if (processes.Count > 0) System.Threading.Thread.Sleep(1500);

            List<string> cacheErrors = new List<string>();
            foreach (string userName in userNames)
            {
                if (userName.Length == 0) continue;
                try { MainForm.ClearOneCCache(userName); }
                catch (Exception ex) { cacheErrors.Add(userName + ": " + ex.Message); }
            }

            MainForm.SendMessageToSessionsCore(ids, "Можна працювати.");

            if (failedKill.Count > 0 || cacheErrors.Count > 0)
            {
                StringBuilder msg = new StringBuilder();
                if (failedKill.Count > 0) msg.Append("Не вдалося завершити процес(и): ").Append(string.Join(", ", failedKill.ToArray())).Append(". ");
                if (cacheErrors.Count > 0) msg.Append("Помилки очищення кешу: ").Append(string.Join("; ", cacheErrors.ToArray()));
                return ErrorResponse(msg.ToString());
            }
            return OkResponse(null);
        }

        private static RemoteProtocol.ParsedMessage CleanupResultResponse(ServerCacheCleanup.CleanupResult result)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["processedCount"] = result.ServicesProcessed.Count.ToString();
            for (int i = 0; i < result.ServicesProcessed.Count; i++) fields["processed" + i] = result.ServicesProcessed[i];
            fields["errorCount"] = result.Errors.Count.ToString();
            for (int i = 0; i < result.Errors.Count; i++) fields["error" + i] = result.Errors[i];
            return OkResponse(fields);
        }

        private static RemoteProtocol.ParsedMessage HandleServiceStatus()
        {
            Dictionary<string, string> statuses = ServerCacheCleanup.GetServiceStatuses();
            Dictionary<string, string> fields = new Dictionary<string, string>();
            int i = 0;
            foreach (KeyValuePair<string, string> kv in statuses)
            {
                fields["svc" + i] = RemoteProtocol.JoinFields(kv.Key, kv.Value ?? "");
                i++;
            }
            fields["count"] = i.ToString();
            return OkResponse(fields);
        }

        private static RemoteProtocol.ParsedMessage HandleIbaseList(RemoteProtocol.ParsedMessage request)
        {
            string userName = GetField(request, "userName");
            string path = IbaseFile.GetPathForUser(userName);
            List<IbaseSection> sections = IbaseFile.ParseFile(path);
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["sections"] = RemoteClient.SerializeSections(sections);
            return OkResponse(fields);
        }

        private static RemoteProtocol.ParsedMessage HandleIbaseAdd(RemoteProtocol.ParsedMessage request)
        {
            string userName = GetField(request, "userName");
            List<IbaseSection> sourceSections = RemoteClient.DeserializeSectionsRaw(GetField(request, "source"));
            List<string> selectedNames = new List<string>(GetField(request, "selected").Split(RemoteProtocol.FieldSep));

            List<IbaseSection> selectedDbs = new List<IbaseSection>();
            foreach (IbaseSection s in sourceSections)
            {
                if (s.IsDatabase && selectedNames.Contains(s.Name)) selectedDbs.Add(s);
            }

            string targetPath = IbaseFile.GetPathForUser(userName);
            IbaseFile.AddResult result = IbaseFile.AddDatabases(targetPath, selectedDbs, sourceSections);

            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["added"] = result.Added.ToString();
            fields["skipped"] = result.Skipped.ToString();
            return OkResponse(fields);
        }

        private static RemoteProtocol.ParsedMessage HandleIbaseDelete(RemoteProtocol.ParsedMessage request)
        {
            string userName = GetField(request, "userName");
            List<IbaseSection> toDelete = RemoteClient.DeserializeSectionsRaw(GetField(request, "sections"));
            string path = IbaseFile.GetPathForUser(userName);
            IbaseFile.DeleteSections(path, toDelete);
            return OkResponse(null);
        }
    }

    internal class RemoteActivityEventArgs : EventArgs
    {
        public readonly string Text;
        public RemoteActivityEventArgs(string text) { Text = text; }
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

        private const string AppVersion = "1.7.0";

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
        private ToolStripMenuItem miCtxAddDatabases;
        private ToolStripMenuItem miCtxViewDatabases;
        private ToolStripMenuItem miCtxMessage;
        private ToolStripMenuItem miCtxMessageAll;
        private bool _suppressIbaseCheckEvents;

        private List<RdpSession> _allSessions = new List<RdpSession>();
        private readonly Dictionary<string, UserAccountInfo> _accountInfoCache = new Dictionary<string, UserAccountInfo>(StringComparer.OrdinalIgnoreCase);
        private int _sortColumn = -1;
        private SortOrder _sortOrder = SortOrder.None;
        private readonly int _ownSessionId = Process.GetCurrentProcess().SessionId;
        private string _pendingUpdateVersion;
        private string _externalIp;

        // --- Профілі серверів / віддалене керування (розділи 2-6) ---
        private Button btnServers;
        private Label lblRemoteBanner;
        private Button btnGoLocal;
        private readonly List<Label> _serviceStatusLabels = new List<Label>();
        private ServerProfile _activeRemote;
        private readonly RemoteControlServer _remoteServer;
        private ServerManagerForm _serverManagerForm;

        public MainForm()
        {
            _remoteServer = new RemoteControlServer(this);
            InitializeComponent();
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5)
                {
                    RefreshSessions();
                    RefreshPolicyStatus();
                    RefreshServiceStatusLabels();
                }
            };
            FormClosing += (s, e) => { try { _remoteServer.Stop(); } catch { } };
            Shown += (s, e) =>
            {
                SendMessage(txtSearch.Handle, EM_SETCUEBANNER, IntPtr.Zero, "Пошук за користувачем/описом/іменем...");
                ApplyTheme(LoadSavedTheme());
                RefreshSessions();
                RefreshPolicyStatus();
                RefreshServiceStatusLabels();
                CheckForUpdatesAsync();
                ShowLocalIp();
                FetchExternalIpAsync();
                StartRemoteControlIfEnabled();
            };
        }

        private void StartRemoteControlIfEnabled()
        {
            ServerProfileStore.RemoteControlSettings settings = ServerProfileStore.LoadRemoteControlSettings();
            if (!settings.Enabled) return;

            string error = _remoteServer.Start(settings.Port, settings.User, settings.Password);
            if (error != null)
            {
                MessageBox.Show(this,
                    "Не вдалося увімкнути \"Дозволити керування\" при запуску: " + error,
                    "Дозволити керування", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
            btnRefresh.Click += (s, e) => { RefreshSessions(); RefreshPolicyStatus(); RefreshServiceStatusLabels(); };
            themeToolTip.SetToolTip(btnRefresh, "Оновити (F5)");

            btnServerCache = new Button
            {
                Text = "Обслуговування 1С ▾",
                Size = new Size(btnW, btnH),
                Location = new Point(colB, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            ToolStripMenuItem miStartServices = new ToolStripMenuItem("Запустити служби 1С/BAF");
            miStartServices.Click += MiStartServices_Click;
            ToolStripMenuItem miStopServices = new ToolStripMenuItem("Зупинити служби 1С/BAF");
            miStopServices.Click += MiStopServices_Click;
            ToolStripMenuItem miRestartServices = new ToolStripMenuItem("Перезапустити служби 1С/BAF");
            miRestartServices.Click += MiRestartServices_Click;

            ToolStripMenuItem miDoServerCleanup = new ToolStripMenuItem("Очистити серверний кеш 1С");
            miDoServerCleanup.Click += MiServerCacheCleanup_Click;
            ToolStripMenuItem miScheduleServerCleanup = new ToolStripMenuItem("Запланувати очищення...");
            miScheduleServerCleanup.Click += MiScheduleCacheCleanup_Click;
            ToolStripMenuItem miCancelScheduleServerCleanup = new ToolStripMenuItem("Скасувати заплановане");
            miCancelScheduleServerCleanup.Click += MiCancelScheduledCleanup_Click;

            serverCacheMenu = new ContextMenuStrip();
            serverCacheMenu.Items.Add(miStartServices);
            serverCacheMenu.Items.Add(miStopServices);
            serverCacheMenu.Items.Add(miRestartServices);
            serverCacheMenu.Items.Add(new ToolStripSeparator());
            serverCacheMenu.Items.Add(miDoServerCleanup);
            serverCacheMenu.Items.Add(new ToolStripSeparator());
            serverCacheMenu.Items.Add(miScheduleServerCleanup);
            serverCacheMenu.Items.Add(miCancelScheduleServerCleanup);

            btnServerCache.Click += (s, e) => serverCacheMenu.Show(btnServerCache, new Point(0, btnServerCache.Height));

            btnServers = new Button
            {
                Text = "⚙",
                Size = new Size(26, 20),
                Location = new Point(520, 9),
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnServers.FlatAppearance.BorderColor = Color.Gray;
            btnServers.Click += BtnServers_Click;
            themeToolTip.SetToolTip(btnServers, "Сервери... (профілі та віддалене керування)");

            lblRemoteBanner = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Location = new Point(12, 68),
                ForeColor = Color.DarkOrange,
                Visible = false,
                Text = ""
            };

            btnGoLocal = new Button
            {
                Text = "Локально",
                Size = new Size(76, 22),
                Location = new Point(600, 65),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Visible = false
            };
            btnGoLocal.Click += BtnGoLocal_Click;

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
            miCtxAddDatabases = new ToolStripMenuItem("Додати бази 1С...");
            miCtxAddDatabases.Click += MiAddDatabases_Click;
            miCtxViewDatabases = new ToolStripMenuItem("Переглянути список баз...");
            miCtxViewDatabases.Click += MiViewUserDatabases_Click;
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
            ctxMenu.Items.Add(miCtxAddDatabases);
            ctxMenu.Items.Add(miCtxViewDatabases);
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
            lvSessions.Columns.Add("Повне ім'я", 160);
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
            Controls.Add(btnServers);
            Controls.Add(lblRemoteBanner);
            Controls.Add(btnGoLocal);
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
                if (int.TryParse(item.SubItems[5].Text, out c)) oneCTotal += c;
            }
            miCtxEnd1C.Enabled = oneCTotal > 0;
            miCtxEnd1C.Text = oneCTotal > 0
                ? string.Format("Завершити 1С/BAS ({0})", oneCTotal)
                : "Завершити 1С/BAS";

            miCtxClearCache1C.Enabled = (selCount >= 1);

            miCtxAddDatabases.Enabled = (selCount >= 1);
            miCtxAddDatabases.Text = selCount > 1
                ? string.Format("Додати бази 1С ({0})", selCount)
                : "Додати бази 1С...";

            miCtxViewDatabases.Enabled = (selCount == 1);
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
            if (_activeRemote != null) return false; // ID сеансів на віддаленому сервері не пов'язані з _ownSessionId цієї машини
            if (lvSessions.SelectedItems.Count != 1) return false;
            int id;
            if (!int.TryParse(lvSessions.SelectedItems[0].SubItems[2].Text, out id)) return false;
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

            string id = lvSessions.SelectedItems[0].SubItems[2].Text;
            if (!Regex.IsMatch(id, @"^\d+$"))
            {
                MessageBox.Show(this, "Не вдалося визначити ID сеансу.", "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int idNum;
            if (int.TryParse(id, out idNum) && _activeRemote == null && idNum == _ownSessionId)
            {
                MessageBox.Show(this, "Не можна підключитися до власного сеансу.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int? policyValue = GetShadowPolicyValue();
            bool noConsent = policyValue.HasValue && policyValue.Value == DesiredValue;

            if (_activeRemote == null)
            {
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
                return;
            }

            // Розділ 6, "Особливості": тіньове підключення виконує mstsc.exe локально на сервері 1, з /v:<host> -
            // SHADOW_INFO лише перевіряє на сервері 2, що сеанс існує.
            ServerProfile profile = _activeRemote;
            RunRemoteAction(
                delegate { RemoteClient.ShadowInfo(profile, idNum); },
                delegate
                {
                    string args = noConsent
                        ? string.Format("/shadow:{0} /v:{1} /control /noConsentPrompt", id, profile.Host)
                        : string.Format("/shadow:{0} /v:{1} /control", id, profile.Host);
                    try
                    {
                        Process.Start("mstsc.exe", args);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Не вдалося запустити підключення: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
        }

        private void MiCtxTakeOver_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count != 1)
            {
                MessageBox.Show(this, "Виберіть рівно один сеанс.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int id;
            if (!int.TryParse(lvSessions.SelectedItems[0].SubItems[2].Text, out id)) return;

            if (_activeRemote == null && id == _ownSessionId)
            {
                MessageBox.Show(this, "Не можна перейняти власний сеанс.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string userName = lvSessions.SelectedItems[0].Text;

            DialogResult confirm = MessageBox.Show(this,
                string.Format("Поточний сеанс на {0} буде замінено сеансом користувача \"{1}\" (як команда \"Підключити\" в Диспетчері завдань). " +
                    "Це не тіньовий перегляд — робочий стіл, до якого підключається команда, стане недоступний, поки ви не повернетесь назад. Продовжити?",
                    _activeRemote == null ? "цьому комп'ютері" : _activeRemote.Name, userName),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            string password = ShowInputDialog(string.Format("Пароль користувача \"{0}\":", userName), "Перейняти сеанс", true);
            if (password == null) return;

            if (_activeRemote == null)
            {
                string ownStation = GetSessionStationName(_ownSessionId);
                if (string.IsNullOrEmpty(ownStation))
                {
                    MessageBox.Show(this, "Не вдалося визначити назву поточного сеансу.", "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string error = TakeOverCore(id, ownStation, password);
                if (error != null)
                {
                    MessageBox.Show(this, error, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            // Віддалено: сервер 2 не може автоматично визначити назву консольної станції адміністратора,
            // яка живе на сервері 1 - її потрібно вказати вручну (мається на увазі станція, з якої
            // адміністратор УЖЕ під'єднаний до сервера 2, наприклад через попередній тіньовий сеанс/RDP).
            string destStation = ShowInputDialog(
                "Назва станції (WinStation) на сервері \"" + _activeRemote.Name + "\", куди перейняти сеанс.\n" +
                "Дізнатися можна в Диспетчері завдань → вкладка Користувачі на самому сервері 2, стовпець \"Сеанс\".",
                "Перейняти сеанс (віддалено)", false);
            if (string.IsNullOrEmpty(destStation)) return;

            ServerProfile profile = _activeRemote;
            RunRemoteAction(delegate { RemoteClient.TakeOver(profile, id, destStation, password); }, null);
        }

        /// <summary>Без UI-побічних дій - переюзається сервером віддаленого керування (розділ 5/6). Повертає повідомлення про помилку або null.</summary>
        internal static string TakeOverCore(int sessionId, string destStation, string password)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("tscon.exe",
                    string.Format("{0} /dest:{1} /password:{2}", sessionId, destStation, password))
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
                        return "Не вдалося перейняти сеанс (код " + p.ExitCode + ")." + (string.IsNullOrEmpty(err) ? "" : "\n" + err);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return "Не вдалося перейняти сеанс: " + ex.Message;
            }
        }

        internal static string GetSessionStationName(int sessionId)
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
                if (int.TryParse(item.SubItems[2].Text, out id))
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

            LogoffSessionsDispatch(ids);
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

            LogoffSessionsDispatch(ids);
        }

        private void LogoffSessions(List<int> ids)
        {
            List<string> failed = LogoffSessionsCore(ids);

            if (failed.Count > 0)
            {
                MessageBox.Show(this, "Не вдалося завершити сеанс(и) з ID: " + string.Join(", ", failed.ToArray()), "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LogoffSessionsDispatch(List<int> ids)
        {
            if (_activeRemote == null)
            {
                LogoffSessions(ids);
                RefreshSessions();
                return;
            }

            ServerProfile profile = _activeRemote;
            RunRemoteAction(
                delegate { RemoteClient.LogoffSessions(profile, ids); },
                delegate { RefreshSessions(); });
        }

        /// <summary>Без UI-побічних дій - переюзається сервером віддаленого керування (розділ 5/6).</summary>
        internal static List<string> LogoffSessionsCore(List<int> ids)
        {
            List<string> failed = new List<string>();
            foreach (int id in ids)
            {
                if (!Wts.WTSLogoffSession(IntPtr.Zero, id, false))
                {
                    failed.Add(id.ToString());
                }
            }
            return failed;
        }

        internal static List<Process> GetOneCProcessesForSessions(List<int> sessionIds)
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

        internal static List<string> KillProcesses(List<Process> processes)
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
                if (int.TryParse(item.SubItems[2].Text, out id)) ids.Add(id);
            }
            if (ids.Count == 0) return;

            if (_activeRemote == null)
            {
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
                return;
            }

            // Віддалено ми не можемо синхронно дізнатись кількість процесів наперед без окремого запиту - підтверджуємо узагальнено.
            DialogResult confirmRemote = MessageBox.Show(this,
                string.Format("Завершити процеси 1С/BAS у вибраних сеансах на сервері \"{0}\"? Незбережені дані користувачів буде втрачено.", _activeRemote.Name),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmRemote != DialogResult.Yes) return;

            ServerProfile profileEnd1C = _activeRemote;
            RunRemoteAction(
                delegate { RemoteClient.End1C(profileEnd1C, ids); },
                delegate { RefreshSessions(); });
        }

        private void MiCtxClearCache1C_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count == 0) return;

            List<int> ids = new List<int>();
            List<string> userNames = new List<string>();
            foreach (ListViewItem item in lvSessions.SelectedItems)
            {
                int id;
                if (int.TryParse(item.SubItems[2].Text, out id))
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

            if (_activeRemote == null)
            {
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
                return;
            }

            ServerProfile profileClearCache = _activeRemote;
            RunRemoteAction(
                delegate { RemoteClient.ClearCache1C(profileClearCache, ids, userNames); },
                delegate
                {
                    RefreshSessions();
                    MessageBox.Show(this, "Готово: сеанси 1С завершено, кеш очищено, користувачів повідомлено.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
        }

        private static readonly string[] OneCVersionFolders = { "1Cv8", "1Cv82" };
        private static readonly string[] OneCCacheFoldersToDelete = { "Config", "ConfigSave", "DBNameCache", "SICache", "vrs-cache" };

        internal static void ClearOneCCache(string userName)
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

        private void MiAddDatabases_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count == 0) return;

            List<string> targetUsers = new List<string>();
            foreach (ListViewItem item in lvSessions.SelectedItems) targetUsers.Add(item.Text);

            string sourcePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1CEStart", "ibases.v8i");
            if (!File.Exists(sourcePath))
            {
                MessageBox.Show(this,
                    "Не знайдено ваш власний список баз 1С:\n" + sourcePath + "\n\nСпочатку додайте потрібні бази у своєму 1CEStart.",
                    "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<IbaseSection> sourceSections;
            try
            {
                sourceSections = IbaseFile.Parse(File.ReadAllLines(sourcePath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося прочитати " + sourcePath + ": " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            List<IbaseSection> selected = ShowIbaseTreeDialog(
                "Додати бази 1С обраним користувачам",
                sourceSections,
                "Додати",
                "Позначте бази (або цілі теки) зі свого списку 1С, які потрібно додати обраним користувачам:");
            if (selected == null) return;

            List<IbaseSection> dbsOnly = new List<IbaseSection>();
            foreach (IbaseSection s in selected)
            {
                if (s.IsDatabase) dbsOnly.Add(s);
            }

            if (dbsOnly.Count == 0)
            {
                MessageBox.Show(this, "Не вибрано жодної бази.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult confirmAdd = MessageBox.Show(this,
                string.Format("Додати {0} баз(и) для {1} користувач(ів): {2}?\n\nБази, які вже прописані (за рядком підключення), будуть пропущені.",
                    dbsOnly.Count, targetUsers.Count, string.Join(", ", targetUsers.ToArray())),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmAdd != DialogResult.Yes) return;

            if (_activeRemote == null)
            {
                StringBuilder summary = new StringBuilder();
                foreach (string userName in targetUsers)
                {
                    string targetPath = IbaseFile.GetPathForUser(userName);
                    try
                    {
                        IbaseFile.AddResult r = IbaseFile.AddDatabases(targetPath, dbsOnly, sourceSections);
                        summary.AppendLine(string.Format("{0}: додано {1}, пропущено (вже є) {2}", userName, r.Added, r.Skipped));
                    }
                    catch (Exception ex)
                    {
                        summary.AppendLine(string.Format("{0}: помилка — {1}", userName, ex.Message));
                    }
                }

                MessageBox.Show(this, summary.ToString(), "Додавання баз 1С", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Джерело (власний список 1С адміністратора) завжди читається локально - воно на машині, де запущено застосунок;
            // лише запис у список цільового користувача виконується на сервері 2 (IBASE_ADD).
            ServerProfile profile = _activeRemote;
            StringBuilder remoteSummary = new StringBuilder();
            RunRemoteAction(
                delegate
                {
                    foreach (string userName in targetUsers)
                    {
                        try
                        {
                            IbaseFile.AddResult r = RemoteClient.IbaseAdd(profile, userName, dbsOnly, sourceSections);
                            remoteSummary.AppendLine(string.Format("{0}: додано {1}, пропущено (вже є) {2}", userName, r.Added, r.Skipped));
                        }
                        catch (Exception ex)
                        {
                            remoteSummary.AppendLine(string.Format("{0}: помилка — {1}", userName, ex.Message));
                        }
                    }
                },
                delegate { MessageBox.Show(this, remoteSummary.ToString(), "Додавання баз 1С — " + profile.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); });
        }

        private void MiViewUserDatabases_Click(object sender, EventArgs e)
        {
            if (lvSessions.SelectedItems.Count != 1)
            {
                MessageBox.Show(this, "Виберіть рівно одного користувача.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string userName = lvSessions.SelectedItems[0].Text;

            if (_activeRemote == null)
            {
                string path = IbaseFile.GetPathForUser(userName);

                if (!File.Exists(path))
                {
                    MessageBox.Show(this, string.Format("У користувача \"{0}\" ще немає списку баз 1С.", userName),
                        "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                List<IbaseSection> sections;
                try
                {
                    sections = IbaseFile.Parse(File.ReadAllLines(path, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не вдалося прочитати файл: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                ShowIbaseTreeAndDeleteLocal(userName, sections, path);
                return;
            }

            ServerProfile profile = _activeRemote;
            List<IbaseSection> remoteSections = null;
            RunRemoteAction(
                delegate { remoteSections = RemoteClient.IbaseList(profile, userName); },
                delegate { ShowIbaseTreeAndDeleteRemote(profile, userName, remoteSections); });
        }

        private void ShowIbaseTreeAndDeleteLocal(string userName, List<IbaseSection> sections, string path)
        {
            if (sections.Count == 0)
            {
                MessageBox.Show(this, "Список баз порожній.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<IbaseSection> toDelete = ShowIbaseTreeDialog(
                string.Format("Список баз 1С — {0}", userName),
                sections,
                "Видалити",
                "Позначте застарілі записи (або теки) для видалення зі списку баз користувача:");
            if (toDelete == null || toDelete.Count == 0) return;

            DialogResult confirmDelete = MessageBox.Show(this,
                string.Format("Видалити {0} запис(ів) зі списку баз користувача \"{1}\"?\n\nЦе прибирає їх лише зі стартового списку 1С, самі бази даних не видаляються.",
                    toDelete.Count, userName),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmDelete != DialogResult.Yes) return;

            try
            {
                IbaseFile.DeleteSections(path, toDelete);
                MessageBox.Show(this, "Видалено.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося зберегти зміни: " + ex.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowIbaseTreeAndDeleteRemote(ServerProfile profile, string userName, List<IbaseSection> sections)
        {
            if (sections == null || sections.Count == 0)
            {
                MessageBox.Show(this,
                    string.Format("Список баз порожній (або у користувача \"{0}\" ще немає списку баз 1С) на сервері \"{1}\".", userName, profile.Name),
                    "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<IbaseSection> toDelete = ShowIbaseTreeDialog(
                string.Format("Список баз 1С — {0} ({1})", userName, profile.Name),
                sections,
                "Видалити",
                "Позначте застарілі записи (або теки) для видалення зі списку баз користувача:");
            if (toDelete == null || toDelete.Count == 0) return;

            DialogResult confirmDelete = MessageBox.Show(this,
                string.Format("Видалити {0} запис(ів) зі списку баз користувача \"{1}\" на сервері \"{2}\"?\n\nЦе прибирає їх лише зі стартового списку 1С, самі бази даних не видаляються.",
                    toDelete.Count, userName, profile.Name),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmDelete != DialogResult.Yes) return;

            RunRemoteAction(
                delegate { RemoteClient.IbaseDelete(profile, userName, toDelete); },
                delegate { MessageBox.Show(this, "Видалено.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information); });
        }

        private List<IbaseSection> ShowIbaseTreeDialog(string title, List<IbaseSection> sections, string actionButtonText, string hintText)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(420, 480);
                dlg.MinimumSize = new Size(340, 300);
                dlg.Font = Font;
                dlg.BackColor = BackColor;

                Label lblHintDlg = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(396, 0),
                    Location = new Point(12, 10),
                    ForeColor = lblHint.ForeColor,
                    Text = hintText
                };

                TreeView tree = new TreeView
                {
                    CheckBoxes = true,
                    Location = new Point(12, 40),
                    Size = new Size(396, 376),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = txtSearch.BackColor,
                    ForeColor = txtSearch.ForeColor
                };

                Dictionary<TreeNode, IbaseSection> nodeMap = new Dictionary<TreeNode, IbaseSection>();
                TreeNode root = BuildIbaseTree(sections, nodeMap);
                List<TreeNode> topNodes = new List<TreeNode>();
                foreach (TreeNode child in root.Nodes) topNodes.Add(child);
                foreach (TreeNode child in topNodes) tree.Nodes.Add(child);
                tree.ExpandAll();
                tree.AfterCheck += IbaseTree_AfterCheck;

                Button ok = new Button
                {
                    Text = actionButtonText,
                    DialogResult = DialogResult.OK,
                    Location = new Point(228, 428),
                    Size = new Size(90, 28),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                ok.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                Button cancel = new Button
                {
                    Text = "Скасувати",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(324, 428),
                    Size = new Size(84, 28),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    FlatStyle = btnDisconnect.FlatStyle,
                    BackColor = btnDisconnect.BackColor,
                    ForeColor = btnDisconnect.ForeColor
                };
                cancel.FlatAppearance.BorderColor = btnDisconnect.FlatAppearance.BorderColor;

                dlg.Controls.Add(lblHintDlg);
                dlg.Controls.Add(tree);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;

                List<IbaseSection> selected = new List<IbaseSection>();
                foreach (KeyValuePair<TreeNode, IbaseSection> kv in nodeMap)
                {
                    if (kv.Key.Checked) selected.Add(kv.Value);
                }
                return selected;
            }
        }

        private void IbaseTree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressIbaseCheckEvents) return;
            _suppressIbaseCheckEvents = true;
            SetChildrenChecked(e.Node, e.Node.Checked);
            _suppressIbaseCheckEvents = false;
        }

        private static void SetChildrenChecked(TreeNode node, bool value)
        {
            foreach (TreeNode child in node.Nodes)
            {
                child.Checked = value;
                SetChildrenChecked(child, value);
            }
        }

        private static TreeNode BuildIbaseTree(List<IbaseSection> sections, Dictionary<TreeNode, IbaseSection> nodeMap)
        {
            TreeNode root = new TreeNode("/");
            Dictionary<string, TreeNode> folderNodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
            folderNodes["/"] = root;

            List<IbaseSection> folderSections = new List<IbaseSection>();
            foreach (IbaseSection s in sections)
            {
                if (!s.IsDatabase) folderSections.Add(s);
            }

            Dictionary<string, bool> created = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            created["/"] = true;

            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (IbaseSection s in folderSections)
                {
                    string key = "/" + s.Name;
                    if (created.ContainsKey(key)) continue;

                    string parentKey = string.IsNullOrEmpty(s.Folder) ? "/" : s.Folder;
                    TreeNode parentNode;
                    if (!folderNodes.TryGetValue(parentKey, out parentNode)) continue;

                    TreeNode node = new TreeNode(s.Name);
                    nodeMap[node] = s;
                    parentNode.Nodes.Add(node);
                    folderNodes[key] = node;
                    created[key] = true;
                    progress = true;
                }
            }

            foreach (IbaseSection s in sections)
            {
                if (!s.IsDatabase) continue;

                string parentKey = string.IsNullOrEmpty(s.Folder) ? "/" : s.Folder;
                TreeNode parentNode;
                if (!folderNodes.TryGetValue(parentKey, out parentNode)) parentNode = root;

                TreeNode dbNode = new TreeNode(s.Name);
                nodeMap[dbNode] = s;
                parentNode.Nodes.Add(dbNode);
            }

            return root;
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

            List<int> ids = CollectOtherSessionIds();
            WarnSessionsFireAndForget(ids, "Через 30 секунд розпочнеться технічне обслуговування сервера 1С/BAF. Будь ласка, збережіть роботу.");

            if (ShowCountdownDialog(30)) return;

            btnServerCache.Enabled = false;

            if (_activeRemote == null)
            {
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
                            ShowServerCleanupResult(result, "Очищення серверного кешу 1С");
                            RefreshSessions();
                        }));
                    }
                });
            }
            else
            {
                ServerProfile profile = _activeRemote;
                ServerCacheCleanup.CleanupResult result = null;
                RunRemoteAction(
                    delegate
                    {
                        result = RemoteClient.ServerCleanup(profile);
                        if (ids.Count > 0)
                        {
                            try { RemoteClient.SendMessage(profile, ids, "Можна працювати."); }
                            catch { /* не критично - основна дія вже виконана */ }
                        }
                    },
                    delegate
                    {
                        btnServerCache.Enabled = true;
                        ShowServerCleanupResult(result, "Очищення серверного кешу 1С — " + profile.Name);
                        RefreshSessions();
                    });
            }
        }

        /// <summary>ID сеансів, крім власного (для локального обслуговування) - при активному remote-профілі власного сеансу серед них немає, тож фільтр не шкодить.</summary>
        private List<int> CollectOtherSessionIds()
        {
            List<int> ids = new List<int>();
            foreach (RdpSession s in _allSessions)
            {
                int id;
                if (int.TryParse(s.Id, out id) && (_activeRemote != null || id != _ownSessionId)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>Попередження перед відліком - для локального сервера синхронно (швидкий локальний виклик), для віддаленого - у фоні без очікування (не критично, якщо не дійде).</summary>
        private void WarnSessionsFireAndForget(List<int> ids, string message)
        {
            if (ids.Count == 0) return;

            if (_activeRemote == null)
            {
                SendMessageToSessions(ids, message);
                return;
            }

            ServerProfile profile = _activeRemote;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { RemoteClient.SendMessage(profile, ids, message); }
                catch { /* не критично - це лише попередження */ }
            });
        }

        private void ShowServerCleanupResult(ServerCacheCleanup.CleanupResult result, string title)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string s in result.ServicesProcessed) sb.AppendLine(s);
            if (result.ServicesProcessed.Count > 0 && result.FoldersDeleted.Count > 0)
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

            MessageBox.Show(this, sb.ToString(), title,
                MessageBoxButtons.OK, result.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void MiStartServices_Click(object sender, EventArgs e)
        {
            // Незворотньо-безпечна дія (лише запуск) - без попередження й відліку, за планом.
            if (_activeRemote == null)
            {
                btnServerCache.Enabled = false;
                Cursor = Cursors.WaitCursor;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    ServerCacheCleanup.CleanupResult result = ServerCacheCleanup.StartServices();
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            Cursor = Cursors.Default;
                            btnServerCache.Enabled = true;
                            ShowServerCleanupResult(result, "Запуск служб 1С/BAF");
                            RefreshServiceStatusLabels();
                        }));
                    }
                });
            }
            else
            {
                ServerProfile profile = _activeRemote;
                ServerCacheCleanup.CleanupResult result = null;
                btnServerCache.Enabled = false;
                RunRemoteAction(
                    delegate { result = RemoteClient.ServiceStart(profile); },
                    delegate
                    {
                        btnServerCache.Enabled = true;
                        ShowServerCleanupResult(result, "Запуск служб 1С/BAF — " + profile.Name);
                        RefreshServiceStatusLabels();
                    });
            }
        }

        private void MiStopServices_Click(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(this,
                "Це зупинить служби 1С/BAF. 1С стане недоступним для ВСІХ користувачів сервера, доки службу не буде запущено знову.\n\n" +
                "Усім активним сеансам буде надіслано попередження і 30-секундний відлік перед початком. Продовжити?",
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            List<int> ids = CollectOtherSessionIds();
            WarnSessionsFireAndForget(ids, "Через 30 секунд буде зупинено служби 1С/BAF. Будь ласка, збережіть роботу.");

            if (ShowCountdownDialog(30)) return;

            btnServerCache.Enabled = false;

            if (_activeRemote == null)
            {
                Cursor = Cursors.WaitCursor;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    ServerCacheCleanup.CleanupResult result = ServerCacheCleanup.StopServices();
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            Cursor = Cursors.Default;
                            btnServerCache.Enabled = true;
                            ShowServerCleanupResult(result, "Зупинка служб 1С/BAF");
                            RefreshServiceStatusLabels();
                        }));
                    }
                });
            }
            else
            {
                ServerProfile profile = _activeRemote;
                ServerCacheCleanup.CleanupResult result = null;
                RunRemoteAction(
                    delegate { result = RemoteClient.ServiceStop(profile); },
                    delegate
                    {
                        btnServerCache.Enabled = true;
                        ShowServerCleanupResult(result, "Зупинка служб 1С/BAF — " + profile.Name);
                        RefreshServiceStatusLabels();
                    });
            }
        }

        private void MiRestartServices_Click(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(this,
                "Це перезапустить служби 1С/BAF (без очищення кешу). 1С стане недоступним для ВСІХ користувачів сервера на короткий час.\n\n" +
                "Усім активним сеансам буде надіслано попередження і 30-секундний відлік перед початком. Продовжити?",
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            List<int> ids = CollectOtherSessionIds();
            WarnSessionsFireAndForget(ids, "Через 30 секунд буде перезапущено служби 1С/BAF. Будь ласка, збережіть роботу.");

            if (ShowCountdownDialog(30)) return;

            btnServerCache.Enabled = false;

            if (_activeRemote == null)
            {
                Cursor = Cursors.WaitCursor;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    ServerCacheCleanup.CleanupResult result = ServerCacheCleanup.RestartServices();
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            Cursor = Cursors.Default;
                            btnServerCache.Enabled = true;
                            if (ids.Count > 0) SendMessageToSessions(ids, "Можна працювати.");
                            ShowServerCleanupResult(result, "Перезапуск служб 1С/BAF");
                            RefreshServiceStatusLabels();
                        }));
                    }
                });
            }
            else
            {
                ServerProfile profile = _activeRemote;
                ServerCacheCleanup.CleanupResult result = null;
                RunRemoteAction(
                    delegate
                    {
                        result = RemoteClient.ServiceRestart(profile);
                        if (ids.Count > 0)
                        {
                            try { RemoteClient.SendMessage(profile, ids, "Можна працювати."); }
                            catch { /* не критично */ }
                        }
                    },
                    delegate
                    {
                        btnServerCache.Enabled = true;
                        ShowServerCleanupResult(result, "Перезапуск служб 1С/BAF — " + profile.Name);
                        RefreshServiceStatusLabels();
                    });
            }
        }

        private void BtnServers_Click(object sender, EventArgs e)
        {
            if (_serverManagerForm == null || _serverManagerForm.IsDisposed)
            {
                _serverManagerForm = new ServerManagerForm(this);
            }

            if (!_serverManagerForm.Visible) _serverManagerForm.Show(this);
            _serverManagerForm.BringToFront();
            _serverManagerForm.Activate();
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

        internal static int RunHidden(string exe, string args)
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
                if (int.TryParse(item.SubItems[2].Text, out id)) ids.Add(id);
            }
            if (ids.Count == 0) return;

            string prompt = ids.Count == 1
                ? "Текст повідомлення:"
                : string.Format("Текст повідомлення для вибраних сеансів ({0}):", ids.Count);

            string message = ShowInputDialog(prompt, "Надіслати повідомлення", false, true);
            if (string.IsNullOrEmpty(message)) return;

            SendMessageDispatch(ids, message);
        }

        private void MiCtxMessageAll_Click(object sender, EventArgs e)
        {
            List<int> ids = CollectOtherSessionIds();

            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Немає інших активних сеансів.", "Інформація", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string message = ShowInputDialog(
                string.Format("Текст повідомлення для всіх сеансів, крім вашого ({0}):", ids.Count),
                "Надіслати повідомлення всім", false, true);
            if (string.IsNullOrEmpty(message)) return;

            SendMessageDispatch(ids, message);
        }

        private void SendMessageDispatch(List<int> ids, string message)
        {
            if (_activeRemote == null)
            {
                SendMessageToSessions(ids, message);
                return;
            }

            ServerProfile profile = _activeRemote;
            RunRemoteAction(delegate { RemoteClient.SendMessage(profile, ids, message); }, null);
        }

        private void SendMessageToSessions(List<int> ids, string message)
        {
            List<string> failed = SendMessageToSessionsCore(ids, message);

            if (failed.Count > 0)
            {
                MessageBox.Show(this, "Не вдалося надіслати повідомлення сеансу(ам) з ID: " + string.Join(", ", failed.ToArray()), "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Без UI-побічних дій - переюзається сервером віддаленого керування (розділ 5/6).</summary>
        internal static List<string> SendMessageToSessionsCore(List<int> ids, string message)
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
            return failed;
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

            Button[] buttons = { btnRefresh, btnServerCache, btnServers, btnGoLocal, btnEnablePolicy, btnDisconnect, btnDisconnectAll, btnReboot };
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

        /// <summary>
        /// Виконує <paramref name="work"/> у фоновому потоці (ThreadPool) - обов'язково для будь-якого мережевого
        /// виклику RemoteClient, щоб не блокувати UI-потік (той самий патерн, що й FetchExternalIpAsync).
        /// <paramref name="onSuccess"/> викликається через BeginInvoke після успішного завершення; помилка показується MessageBox.
        /// </summary>
        private void RunRemoteAction(Action work, Action onSuccess)
        {
            Cursor = Cursors.WaitCursor;
            Enabled = false;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        Cursor = Cursors.Default;
                        Enabled = true;
                        if (error != null)
                        {
                            MessageBox.Show(this, "Помилка віддаленої дії: " + error.Message, "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else if (onSuccess != null)
                        {
                            onSuccess();
                        }
                    }));
                }
            });
        }

        private void UpdateRemoteBanner()
        {
            bool remote = _activeRemote != null;
            lblRemoteBanner.Visible = remote;
            btnGoLocal.Visible = remote;
            Text = remote
                ? string.Format("Засіб тіньових сеансів — {0} ({1})", _activeRemote.Name, _activeRemote.Host)
                : "Засіб тіньових сеансів";
            lblRemoteBanner.Text = remote
                ? string.Format("Підключено до: {0} ({1})", _activeRemote.Name, _activeRemote.Host)
                : "";
        }

        /// <summary>Викликається з ServerManagerForm при успішному підключенні до профілю.</summary>
        internal void ConnectToProfile(ServerProfile profile)
        {
            _activeRemote = profile;
            UpdateRemoteBanner();
            RefreshSessions();
            RefreshServiceStatusLabels();
        }

        /// <summary>Повертає керування до локального сервера. Викликається і кнопкою "Локально", і зі ServerManagerForm.</summary>
        internal void DisconnectRemote()
        {
            _activeRemote = null;
            UpdateRemoteBanner();
            RefreshSessions();
            RefreshServiceStatusLabels();
        }

        private void BtnGoLocal_Click(object sender, EventArgs e)
        {
            DisconnectRemote();
        }

        internal ServerProfile ActiveRemote { get { return _activeRemote; } }
        internal RemoteControlServer RemoteServer { get { return _remoteServer; } }

        // Кольори/стилі для дочірніх форм (ServerManagerForm тощо) - узгоджено з поточною темою, за зразком існуючих діалогів.
        internal Color DialogTextColor { get { return lblSelect.ForeColor; } }
        internal Color DialogHintColor { get { return lblHint.ForeColor; } }
        internal Color DialogInputBack { get { return txtSearch.BackColor; } }
        internal Color DialogInputFore { get { return txtSearch.ForeColor; } }
        internal FlatStyle DialogButtonFlatStyle { get { return btnDisconnect.FlatStyle; } }
        internal Color DialogButtonBack { get { return btnDisconnect.BackColor; } }
        internal Color DialogButtonFore { get { return btnDisconnect.ForeColor; } }
        internal Color DialogButtonBorder { get { return btnDisconnect.FlatAppearance.BorderColor; } }

        private void RefreshSessions()
        {
            if (_activeRemote != null)
            {
                RefreshSessionsRemote();
                return;
            }

            _allSessions = GetRdpSessions();
            ApplyCachedAccountInfo(_allSessions);
            ApplyFilter();
            FetchAccountInfoAsync(_allSessions);
        }

        private void RefreshSessionsRemote()
        {
            ServerProfile profile = _activeRemote;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<RdpSession> sessions = null;
                string error = null;
                try
                {
                    sessions = RemoteClient.ListSessions(profile);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (_activeRemote != profile) return; // користувач встиг відключитись/перемкнутись
                        if (error != null)
                        {
                            _allSessions = new List<RdpSession>();
                            ApplyFilter();
                            lblRemoteBanner.Text = string.Format("Підключено до: {0} ({1}) — помилка: {2}", profile.Name, profile.Host, error);
                        }
                        else
                        {
                            _allSessions = sessions;
                            ApplyFilter();
                        }
                    }));
                }
            });
        }

        private static string GetAccountCacheKey(RdpSession s)
        {
            bool isLocal = string.IsNullOrEmpty(s.DomainName) ||
                string.Equals(s.DomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            return isLocal ? s.UserName : s.DomainName + "\\" + s.UserName;
        }

        private void ApplyCachedAccountInfo(List<RdpSession> sessions)
        {
            foreach (RdpSession s in sessions)
            {
                UserAccountInfo cached;
                if (_accountInfoCache.TryGetValue(GetAccountCacheKey(s), out cached))
                {
                    s.Description = cached.Description;
                    s.FullName = cached.FullName;
                }
            }
        }

        private void FetchAccountInfoAsync(List<RdpSession> sessions)
        {
            List<RdpSession> toFetch = new List<RdpSession>();
            foreach (RdpSession s in sessions)
            {
                if (!_accountInfoCache.ContainsKey(GetAccountCacheKey(s))) toFetch.Add(s);
            }
            if (toFetch.Count == 0) return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, UserAccountInfo> fetched = new Dictionary<string, UserAccountInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (RdpSession s in toFetch)
                {
                    string key = GetAccountCacheKey(s);
                    UserAccountInfo info;
                    if (!fetched.TryGetValue(key, out info))
                    {
                        info = GetUserAccountInfo(s.UserName, s.DomainName);
                        fetched[key] = info;
                    }
                    s.Description = info.Description;
                    s.FullName = info.FullName;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        foreach (KeyValuePair<string, UserAccountInfo> kv in fetched)
                        {
                            _accountInfoCache[kv.Key] = kv.Value;
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
                    bool matchesFullName = !string.IsNullOrEmpty(s.FullName) &&
                        s.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchesUser && !matchesDesc && !matchesFullName) continue;
                }

                bool isOwn = false;
                int id;
                if (_activeRemote == null && int.TryParse(s.Id, out id) && id == _ownSessionId) isOwn = true;

                ListViewItem item = new ListViewItem(s.UserName);
                item.SubItems.Add(s.FullName);
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

        internal static List<RdpSession> GetRdpSessions()
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

                    string domainName = GetSessionDomainName(si.SessionID);

                    result.Add(new RdpSession
                    {
                        UserName = userName,
                        SessionName = stationName,
                        Id = si.SessionID.ToString(),
                        State = TranslateState(si.State),
                        RawState = si.State,
                        OneCCount = oneCCount,
                        Description = "",
                        FullName = "",
                        DomainName = domainName
                    });
                }
            }
            finally
            {
                Wts.WTSFreeMemory(pSessionInfo);
            }

            return result;
        }

        internal static string GetSessionUserName(int sessionId)
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

        internal static string GetSessionDomainName(int sessionId)
        {
            IntPtr buffer;
            int bytesReturned;
            string result = "";

            if (Wts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, Wts.WtsInfoClass.WTSDomainName, out buffer, out bytesReturned))
            {
                if (buffer != IntPtr.Zero)
                {
                    result = Marshal.PtrToStringUni(buffer);
                    Wts.WTSFreeMemory(buffer);
                }
            }

            return result;
        }

        internal static UserAccountInfo GetUserAccountInfo(string userName, string domainName)
        {
            UserAccountInfo info = new UserAccountInfo();

            int slashIdx = userName.IndexOf('\\');
            string plainUserName = slashIdx >= 0 ? userName.Substring(slashIdx + 1) : userName;

            bool isLocal = string.IsNullOrEmpty(domainName) ||
                string.Equals(domainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            try
            {
                using (PrincipalContext ctx = isLocal
                    ? new PrincipalContext(ContextType.Machine)
                    : new PrincipalContext(ContextType.Domain, domainName))
                {
                    using (UserPrincipal user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, plainUserName))
                    {
                        if (user != null)
                        {
                            info.Description = user.Description ?? "";
                            info.FullName = user.DisplayName ?? "";
                        }
                    }
                }
            }
            catch
            {
                // немає довіри до домену, обліковий запис недоступний тощо - лишаємо порожнім
            }

            return info;
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

        /// <summary>Розділ 2: статус служб 1С/BAF праворуч від зовнішнього IP - локально або (якщо є активний профіль) віддалено.</summary>
        private void RefreshServiceStatusLabels()
        {
            if (_activeRemote != null)
            {
                RefreshServiceStatusLabelsRemote();
                return;
            }

            RenderServiceStatusLabels("Служби 1С:", ServerCacheCleanup.GetServiceStatuses());
        }

        private void RefreshServiceStatusLabelsRemote()
        {
            ServerProfile profile = _activeRemote;
            RenderServiceStatusLabels("Служби 1С (" + profile.Name + "):", null, "перевірка...");

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, string> statuses = null;
                string error = null;
                try
                {
                    statuses = RemoteClient.ServiceStatus(profile);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (_activeRemote != profile) return;
                        RenderServiceStatusLabels("Служби 1С (" + profile.Name + "):", statuses, error == null ? null : "недоступно");
                    }));
                }
            });
        }

        private void RenderServiceStatusLabels(string caption, Dictionary<string, string> statuses)
        {
            RenderServiceStatusLabels(caption, statuses, statuses == null ? "недоступно" : null);
        }

        private void RenderServiceStatusLabels(string caption, Dictionary<string, string> statuses, string placeholder)
        {
            foreach (Label old in _serviceStatusLabels) Controls.Remove(old);
            _serviceStatusLabels.Clear();

            Font svcFont = new Font("Segoe UI", 8F);
            const int x = 290;
            int y = 152;

            Label captionLbl = new Label
            {
                AutoSize = true,
                Font = svcFont,
                Location = new Point(x, y),
                Text = caption,
                ForeColor = lblHint.ForeColor
            };
            Controls.Add(captionLbl);
            _serviceStatusLabels.Add(captionLbl);
            y += captionLbl.PreferredHeight;

            if (statuses == null)
            {
                Label ph = new Label
                {
                    AutoSize = true,
                    Font = svcFont,
                    Location = new Point(x, y),
                    Text = placeholder ?? "недоступно",
                    ForeColor = Color.Firebrick
                };
                Controls.Add(ph);
                _serviceStatusLabels.Add(ph);
                return;
            }

            foreach (string svcName in ServerCacheCleanup.ServiceNames)
            {
                string status;
                statuses.TryGetValue(svcName, out status);

                string text;
                Color color;
                if (status == null)
                {
                    text = svcName + " — не встановлено";
                    color = Color.Gray;
                }
                else if (string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    text = svcName + " — запущено";
                    color = Color.ForestGreen;
                }
                else
                {
                    text = svcName + " — " + TranslateServiceStatus(status);
                    color = Color.Firebrick;
                }

                Label lbl = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(390, 0),
                    Font = svcFont,
                    Location = new Point(x, y),
                    Text = text,
                    ForeColor = color
                };
                Controls.Add(lbl);
                _serviceStatusLabels.Add(lbl);
                y += lbl.PreferredHeight;
            }
        }

        private static string TranslateServiceStatus(string status)
        {
            switch (status)
            {
                case "Stopped": return "зупинено";
                case "StartPending": return "запускається";
                case "StopPending": return "зупиняється";
                case "Paused": return "призупинено";
                default: return status;
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

    /// <summary>
    /// Розділ 3: немодальне вікно "Сервери..." - список профілів (з пінгом) і секція "Дозволити керування цим сервером".
    /// </summary>
    internal class ServerManagerForm : Form
    {
        private const string FirewallRuleName = "ShadowSessionTool Remote";

        private readonly MainForm _owner;
        private List<ServerProfile> _profiles = new List<ServerProfile>();
        private readonly Dictionary<Guid, ListViewItem> _itemsByProfile = new Dictionary<Guid, ListViewItem>();
        private Timer _pingTimer;

        private ListView lvProfiles;
        private Button btnAdd, btnEdit, btnDelete, btnConnect, btnPingRefresh;

        private CheckBox chkRcEnabled;
        private TextBox txtRcUser;
        private TextBox txtRcPassword;
        private NumericUpDown numRcPort;
        private Label lblRcStatus;
        private Label lblRcActivity;
        private Button btnApplyRc;

        internal ServerManagerForm(MainForm owner)
        {
            _owner = owner;
            InitializeComponent();

            LoadProfilesIntoList();
            LoadRemoteControlSection();

            _owner.RemoteServer.Activity += RemoteServer_Activity;

            _pingTimer = new Timer { Interval = 5000 };
            _pingTimer.Tick += (s, e) => PingAllAsync();
            _pingTimer.Start();

            Shown += (s, e) => PingAllAsync();
            FormClosed += (s, e) =>
            {
                _owner.RemoteServer.Activity -= RemoteServer_Activity;
                if (_pingTimer != null) { _pingTimer.Stop(); _pingTimer.Dispose(); _pingTimer = null; }
            };
        }

        private void RemoteServer_Activity(object sender, RemoteActivityEventArgs e)
        {
            if (lblRcActivity != null) lblRcActivity.Text = "Остання активність: " + e.Text;
        }

        private void InitializeComponent()
        {
            Text = "Сервери";
            Font = _owner.Font;
            BackColor = _owner.BackColor;
            ClientSize = new Size(560, 470);
            MinimumSize = new Size(500, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            lvProfiles = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                Location = new Point(12, 12),
                Size = new Size(536, 170),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = _owner.DialogInputBack,
                ForeColor = _owner.DialogInputFore
            };
            lvProfiles.Columns.Add("Назва", 130);
            lvProfiles.Columns.Add("Хост", 130);
            lvProfiles.Columns.Add("Порт", 60);
            lvProfiles.Columns.Add("Користувач", 110);
            lvProfiles.Columns.Add("Пінг", 80);
            lvProfiles.SelectedIndexChanged += (s, e) => UpdateButtons();
            lvProfiles.DoubleClick += BtnConnect_Click;

            btnAdd = MakeButton("Додати", new Point(12, 190));
            btnAdd.Click += BtnAdd_Click;
            btnEdit = MakeButton("Редагувати", new Point(112, 190));
            btnEdit.Click += BtnEdit_Click;
            btnDelete = MakeButton("Видалити", new Point(212, 190));
            btnDelete.Click += BtnDelete_Click;
            btnPingRefresh = MakeButton("Оновити пінг", new Point(312, 190));
            btnPingRefresh.Click += (s, e) => PingAllAsync();
            btnConnect = MakeButton("Підключитися", new Point(412, 190));
            btnConnect.Size = new Size(136, 26);
            btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnConnect.Click += BtnConnect_Click;

            GroupBox grp = new GroupBox
            {
                Text = "Дозволити керування цим сервером",
                Location = new Point(12, 228),
                Size = new Size(536, 210),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                ForeColor = _owner.DialogTextColor
            };

            chkRcEnabled = new CheckBox
            {
                Text = "Увімкнено",
                Location = new Point(12, 24),
                AutoSize = true,
                ForeColor = _owner.DialogTextColor
            };

            Label lblUser = MakeLabel("Користувач:", 12, 55);
            txtRcUser = MakeTextBox(120, 52, 180, "");

            Label lblPassword = MakeLabel("Пароль:", 12, 85);
            txtRcPassword = MakeTextBox(120, 82, 180, "");
            txtRcPassword.UseSystemPasswordChar = true;

            Label lblPort = MakeLabel("Порт:", 12, 115);
            numRcPort = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 65535,
                Value = RemoteProtocol.DefaultPort,
                Location = new Point(120, 112),
                Size = new Size(90, 23)
            };

            btnApplyRc = MakeButton("Застосувати", new Point(320, 80));
            btnApplyRc.Size = new Size(120, 28);
            btnApplyRc.Click += BtnApplyRc_Click;

            lblRcStatus = new Label
            {
                AutoSize = true,
                Location = new Point(12, 148),
                Text = "Вимкнено",
                ForeColor = Color.Gray
            };

            lblRcActivity = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Location = new Point(12, 168),
                Text = "Остання активність: —",
                ForeColor = _owner.DialogHintColor
            };

            Label lblSecurityHint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Location = new Point(12, 190),
                ForeColor = _owner.DialogHintColor,
                Text = "Пароль і корисне навантаження команд передаються між серверами зашифрованими (AES-256), " +
                    "але саме TCP-з'єднання без TLS — використовуйте лише в довіреній внутрішній мережі."
            };

            grp.Controls.Add(chkRcEnabled);
            grp.Controls.Add(lblUser);
            grp.Controls.Add(txtRcUser);
            grp.Controls.Add(lblPassword);
            grp.Controls.Add(txtRcPassword);
            grp.Controls.Add(lblPort);
            grp.Controls.Add(numRcPort);
            grp.Controls.Add(btnApplyRc);
            grp.Controls.Add(lblRcStatus);
            grp.Controls.Add(lblRcActivity);
            grp.Controls.Add(lblSecurityHint);

            Controls.Add(lvProfiles);
            Controls.Add(btnAdd);
            Controls.Add(btnEdit);
            Controls.Add(btnDelete);
            Controls.Add(btnPingRefresh);
            Controls.Add(btnConnect);
            Controls.Add(grp);
        }

        private Label MakeLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = _owner.DialogTextColor };
        }

        private TextBox MakeTextBox(int x, int y, int width, string value)
        {
            return new TextBox
            {
                Text = value ?? "",
                Location = new Point(x, y),
                Size = new Size(width, 23),
                BackColor = _owner.DialogInputBack,
                ForeColor = _owner.DialogInputFore
            };
        }

        private Button MakeButton(string text, Point location)
        {
            Button b = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(94, 26),
                FlatStyle = _owner.DialogButtonFlatStyle,
                BackColor = _owner.DialogButtonBack,
                ForeColor = _owner.DialogButtonFore
            };
            b.FlatAppearance.BorderColor = _owner.DialogButtonBorder;
            return b;
        }

        // --- Профілі ---

        private void LoadProfilesIntoList()
        {
            _profiles = ServerProfileStore.LoadProfiles();
            RebuildListView();
        }

        private void RebuildListView()
        {
            lvProfiles.Items.Clear();
            _itemsByProfile.Clear();

            foreach (ServerProfile p in _profiles)
            {
                ListViewItem item = new ListViewItem(p.Name);
                item.SubItems.Add(p.Host);
                item.SubItems.Add(p.Port.ToString());
                item.SubItems.Add(p.User);
                item.SubItems.Add(FormatPing(p.LastPingMs));
                item.Tag = p;

                if (_owner.ActiveRemote != null && _owner.ActiveRemote.Id == p.Id)
                {
                    item.BackColor = Color.LightGreen;
                    item.ForeColor = Color.Black;
                }

                lvProfiles.Items.Add(item);
                _itemsByProfile[p.Id] = item;
            }

            UpdateButtons();
        }

        private static string FormatPing(double ms)
        {
            if (ms <= -1.5) return "недоступний";
            if (ms < 0) return "...";
            return ms.ToString("0") + " мс";
        }

        private void UpdateButtons()
        {
            bool hasSel = lvProfiles.SelectedItems.Count == 1;
            btnEdit.Enabled = hasSel;
            btnDelete.Enabled = hasSel;
            btnConnect.Enabled = hasSel;

            if (hasSel)
            {
                ServerProfile p = (ServerProfile)lvProfiles.SelectedItems[0].Tag;
                bool connected = _owner.ActiveRemote != null && _owner.ActiveRemote.Id == p.Id;
                btnConnect.Text = connected ? "Відключитися" : "Підключитися";
            }
            else
            {
                btnConnect.Text = "Підключитися";
            }
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            ServerProfile p = new ServerProfile();
            p.Port = RemoteProtocol.DefaultPort;
            if (!ShowProfileDialog(p, true)) return;

            ServerProfileStore.SaveProfile(p);
            _profiles.Add(p);
            RebuildListView();
            PingAllAsync();
        }

        private void BtnEdit_Click(object sender, EventArgs e)
        {
            if (lvProfiles.SelectedItems.Count != 1) return;
            ServerProfile p = (ServerProfile)lvProfiles.SelectedItems[0].Tag;
            if (!ShowProfileDialog(p, false)) return;

            ServerProfileStore.SaveProfile(p);
            RebuildListView();

            if (_owner.ActiveRemote != null && _owner.ActiveRemote.Id == p.Id)
            {
                _owner.ConnectToProfile(p); // оновити банер, якщо ім'я/host відредаговано під час активного підключення
            }
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            if (lvProfiles.SelectedItems.Count != 1) return;
            ServerProfile p = (ServerProfile)lvProfiles.SelectedItems[0].Tag;

            DialogResult confirm = MessageBox.Show(this,
                string.Format("Видалити профіль \"{0}\"?", p.Name),
                "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            if (_owner.ActiveRemote != null && _owner.ActiveRemote.Id == p.Id)
            {
                _owner.DisconnectRemote();
            }

            ServerProfileStore.DeleteProfile(p.Id);
            _profiles.Remove(p);
            RebuildListView();
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (lvProfiles.SelectedItems.Count != 1) return;
            ServerProfile p = (ServerProfile)lvProfiles.SelectedItems[0].Tag;

            bool alreadyConnected = _owner.ActiveRemote != null && _owner.ActiveRemote.Id == p.Id;
            if (alreadyConnected)
            {
                _owner.DisconnectRemote();
                RebuildListView();
                return;
            }

            btnConnect.Enabled = false;
            Cursor = Cursors.WaitCursor;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try
                {
                    RemoteClient.ListSessions(p); // "AUTH" - перевірка пароля/доступності перед перемиканням UI
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        Cursor = Cursors.Default;
                        btnConnect.Enabled = true;

                        if (error != null)
                        {
                            MessageBox.Show(this, "Не вдалося підключитися до \"" + p.Name + "\": " + error.Message,
                                "Помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        _owner.ConnectToProfile(p);
                        RebuildListView();
                    }));
                }
            });
        }

        private bool ShowProfileDialog(ServerProfile p, bool isNew)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = isNew ? "Новий профіль сервера" : "Редагувати профіль сервера";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(320, 230);
                dlg.Font = Font;
                dlg.BackColor = _owner.BackColor;

                Label lblName = MakeLabel("Назва:", 12, 15);
                TextBox txtName = MakeTextBox(110, 12, 198, p.Name);

                Label lblHost = MakeLabel("IP/host:", 12, 45);
                TextBox txtHost = MakeTextBox(110, 42, 198, p.Host);

                Label lblPort = MakeLabel("Порт:", 12, 75);
                NumericUpDown numPort = new NumericUpDown
                {
                    Minimum = 1,
                    Maximum = 65535,
                    Value = p.Port > 0 ? p.Port : RemoteProtocol.DefaultPort,
                    Location = new Point(110, 72),
                    Size = new Size(100, 23)
                };

                Label lblUser = MakeLabel("Користувач:", 12, 105);
                TextBox txtUser = MakeTextBox(110, 102, 198, p.User);

                Label lblPassword = MakeLabel("Пароль:", 12, 135);
                TextBox txtPassword = MakeTextBox(110, 132, 198, p.Password);
                txtPassword.UseSystemPasswordChar = true;

                Button ok = new Button
                {
                    Text = "Зберегти",
                    DialogResult = DialogResult.OK,
                    Location = new Point(140, 188),
                    Size = new Size(80, 28),
                    FlatStyle = _owner.DialogButtonFlatStyle,
                    BackColor = _owner.DialogButtonBack,
                    ForeColor = _owner.DialogButtonFore
                };
                ok.FlatAppearance.BorderColor = _owner.DialogButtonBorder;

                Button cancel = new Button
                {
                    Text = "Скасувати",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(228, 188),
                    Size = new Size(80, 28),
                    FlatStyle = _owner.DialogButtonFlatStyle,
                    BackColor = _owner.DialogButtonBack,
                    ForeColor = _owner.DialogButtonFore
                };
                cancel.FlatAppearance.BorderColor = _owner.DialogButtonBorder;

                dlg.Controls.Add(lblName);
                dlg.Controls.Add(txtName);
                dlg.Controls.Add(lblHost);
                dlg.Controls.Add(txtHost);
                dlg.Controls.Add(lblPort);
                dlg.Controls.Add(numPort);
                dlg.Controls.Add(lblUser);
                dlg.Controls.Add(txtUser);
                dlg.Controls.Add(lblPassword);
                dlg.Controls.Add(txtPassword);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return false;

                if (string.IsNullOrEmpty(txtName.Text.Trim()) || string.IsNullOrEmpty(txtHost.Text.Trim()))
                {
                    MessageBox.Show(this, "Вкажіть назву й адресу сервера.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                p.Name = txtName.Text.Trim();
                p.Host = txtHost.Text.Trim();
                p.Port = (int)numPort.Value;
                p.User = txtUser.Text.Trim();
                p.Password = txtPassword.Text;
                return true;
            }
        }

        private void PingAllAsync()
        {
            List<ServerProfile> snapshot = new List<ServerProfile>(_profiles);
            foreach (ServerProfile p in snapshot)
            {
                ServerProfile captured = p;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    double ms = PingHost(captured.Host);

                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            captured.LastPingMs = ms;
                            ListViewItem item;
                            if (_itemsByProfile.TryGetValue(captured.Id, out item))
                            {
                                item.SubItems[4].Text = FormatPing(ms);
                            }
                        }));
                    }
                });
            }
        }

        private static double PingHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return -2;
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = ping.Send(host, 2000);
                    if (reply != null && reply.Status == IPStatus.Success) return reply.RoundtripTime;
                    return -2;
                }
            }
            catch
            {
                return -2;
            }
        }

        // --- "Дозволити керування цим сервером" ---

        private void LoadRemoteControlSection()
        {
            ServerProfileStore.RemoteControlSettings settings = ServerProfileStore.LoadRemoteControlSettings();
            chkRcEnabled.Checked = settings.Enabled;
            txtRcUser.Text = settings.User;
            txtRcPassword.Text = settings.Password;
            numRcPort.Value = settings.Port > 0 ? settings.Port : RemoteProtocol.DefaultPort;

            UpdateRcStatusLabel();
        }

        private void UpdateRcStatusLabel()
        {
            if (_owner.RemoteServer.IsRunning)
            {
                lblRcStatus.Text = "Слухає на порту " + _owner.RemoteServer.Port;
                lblRcStatus.ForeColor = Color.ForestGreen;
            }
            else
            {
                lblRcStatus.Text = "Вимкнено";
                lblRcStatus.ForeColor = Color.Gray;
            }
        }

        private void BtnApplyRc_Click(object sender, EventArgs e)
        {
            bool enable = chkRcEnabled.Checked;
            int port = (int)numRcPort.Value;
            string user = txtRcUser.Text.Trim();
            string password = txtRcPassword.Text;

            if (enable && (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)))
            {
                MessageBox.Show(this, "Вкажіть користувача й пароль для дозволу керування.", "Увага", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RemoteControlServer server = _owner.RemoteServer;

            if (server.IsRunning)
            {
                server.Stop();
                MainForm.RunHidden("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRuleName + "\"");
            }

            ServerProfileStore.RemoteControlSettings settings = new ServerProfileStore.RemoteControlSettings();
            settings.Enabled = enable;
            settings.Port = port;
            settings.User = user;
            settings.Password = password;
            ServerProfileStore.SaveRemoteControlSettings(settings);

            if (enable)
            {
                string error = server.Start(port, user, password);
                if (error != null)
                {
                    lblRcStatus.Text = "Помилка: " + error;
                    lblRcStatus.ForeColor = Color.Firebrick;
                    return;
                }

                MainForm.RunHidden("netsh.exe", string.Format(
                    "advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol=TCP localport={1}",
                    FirewallRuleName, port));
            }

            UpdateRcStatusLabel();
        }
    }
}
