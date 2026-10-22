// TempCraftBackup: downloads the current source code of every Craft app in the
// Launcher's catalog and packs it into one archive.
//
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.8's
// csc.exe), so it builds and runs with nothing installed. Windows' own tar.exe
// (bsdtar) does the unpacking and the 7z packing.
//
// Everything is portable: settings live in data\settings.ini beside the exe, and
// the only other thing it touches is a scratch folder in %TEMP% that it removes
// when it's done.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// Opts into .NET 4.6.2+ path handling, so deep source trees in the scratch folder
// can still be cleaned up.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace TempCraftBackup
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            try
            {
                // TLS 1.2 and 1.3; GitHub refuses anything older.
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            }
            catch (NotSupportedException)
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class CraftApp
    {
        public string Name;
        public string Repo;

        public CraftApp(string name, string repo)
        {
            Name = name;
            Repo = repo;
        }

        public string SourceUrl
        {
            get { return "https://github.com/" + Repo + "/archive/HEAD.zip"; }
        }
    }

    /// The Craft apps the Launcher offers. Read from the Launcher's own catalog on
    /// GitHub, so apps added there are backed up too; the built-in list is used
    /// when that can't be read.
    static class Catalog
    {
        const string CatalogUrl = "https://raw.githubusercontent.com/JavacrafterDevelopment/Launcher/main/src/apps.rs";

        static readonly CraftApp[] BuiltIn =
        {
            new CraftApp("PhotoCraft", "storytold/photocraft"),
            new CraftApp("VectorCraft", "storytold/vectorcraft"),
            new CraftApp("FilmCraft", "storytold/filmcraft"),
            new CraftApp("EffectCraft", "storytold/effectcraft"),
            new CraftApp("LightCraft", "storytold/lightcraft"),
            new CraftApp("DesignCraft", "storytold/designcraft"),
            new CraftApp("PdfCraft", "storytold/pdfcraft"),
            new CraftApp("FigCraft", "storytold/figcraft"),
            new CraftApp("SoundCraft", "storytold/soundcraft"),
        };

        public static List<CraftApp> Load(out string source)
        {
            try
            {
                string text = Http.GetString(CatalogUrl);
                var found = new List<CraftApp>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var re = new Regex("name:\\s*\"([^\"]+)\"\\s*,\\s*repo:\\s*\"(storytold/[A-Za-z0-9_.\\-]+)\"");
                foreach (Match m in re.Matches(text))
                {
                    string name = m.Groups[1].Value, repo = m.Groups[2].Value;
                    if (repo.EndsWith("craft", StringComparison.OrdinalIgnoreCase) && seen.Add(repo))
                        found.Add(new CraftApp(name, repo));
                }
                if (found.Count > 0)
                {
                    source = "the Launcher's catalog on GitHub";
                    return found;
                }
            }
            catch (Exception)
            {
            }
            source = "the built-in list (the Launcher's catalog couldn't be read)";
            return new List<CraftApp>(BuiltIn);
        }
    }

    static class Http
    {
        public static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "TempCraftBackup/" + BuildInfo.Version;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        public static string GetString(string url)
        {
            using (var resp = (HttpWebResponse)Request(url).GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }

    /// Windows' own tar.exe, which is bsdtar: it reads zips and writes 7z.
    static class Tar
    {
        public static string Path
        {
            get
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                // A 32-bit process sees SysWOW64 as System32; Sysnative reaches the real one.
                foreach (string dir in new[] { "Sysnative", "System32" })
                {
                    string p = System.IO.Path.Combine(windows, dir + "\\tar.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        /// True when this Windows' tar was built with liblzma, so it can write 7z.
        public static bool Can7z()
        {
            string tar = Path;
            if (tar == null) return false;
            try
            {
                var psi = new ProcessStartInfo(tar, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return output.IndexOf("liblzma", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// data\settings.ini beside the exe: one key=value per line.
    static class Settings
    {
        static string FilePath
        {
            get { return Path.Combine(Path.Combine(AppDir, "data"), "settings.ini"); }
        }

        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static Dictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return map;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception)
            {
            }
            return map;
        }

        public static void Save(Dictionary<string, string> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                foreach (var kv in map) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }
    }

    /// The Explorer-style folder picker (navigation pane, address bar, search),
    /// through Windows' IFileOpenDialog. WinForms on .NET Framework only offers the
    /// old tree-view FolderBrowserDialog.
    static class FolderPicker
    {
        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialog
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

        /// The chosen folder, or null if the picker was cancelled.
        public static string Pick(IntPtr owner, string title, string startFolder)
        {
            var dialog = (IFileDialog)new FileOpenDialog();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dialog.SetTitle(title);
                dialog.SetOkButtonLabel("Select Folder");
                if (startFolder != null)
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        IShellItem item;
                        SHCreateItemFromParsingName(startFolder, IntPtr.Zero, ref iid, out item);
                        dialog.SetFolder(item);
                    }
                    catch (Exception)
                    {
                    }
                }
                int hr = dialog.Show(owner);
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                IShellItem result;
                dialog.GetResult(out result);
                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    class MainForm : Form
    {
        const string ArchiveBase = "ArtCraftApps";
        const string ScratchPrefix = "TempCraftBackup-";

        readonly TextBox folderBox = new TextBox();
        readonly Button browseButton = new Button();
        readonly Label formatLabel = new Label();
        readonly ListView list = new ListView();
        readonly ProgressBar progress = new ProgressBar();
        readonly TextBox logBox = new TextBox();
        readonly Button backupButton = new Button();
        readonly Button cancelButton = new Button();
        readonly Button openButton = new Button();
        readonly Label statusLabel = new Label();

        readonly Dictionary<string, string> settings;
        List<CraftApp> apps;
        bool use7z;
        string lastArchive;

        // Set while a backup runs.
        Thread worker;
        volatile bool cancelled;
        volatile bool closeWhenDone;
        readonly object gate = new object();
        Process tarProcess;
        HttpWebRequest download;

        public MainForm()
        {
            settings = Settings.Load();
            Text = "TempCraftBackup v" + BuildInfo.Version;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 6,
                Padding = new Padding(10),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var folderLabel = new Label { Text = "Save backups to:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            folderBox.Dock = DockStyle.Fill;
            folderBox.Margin = new Padding(3, 5, 3, 3);
            folderBox.Text = settings.ContainsKey("OutputFolder")
                ? settings["OutputFolder"]
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            folderBox.Leave += delegate { SaveFolder(); };
            browseButton.Text = "Browse...";
            browseButton.AutoSize = true;
            browseButton.Click += delegate { Browse(); };

            formatLabel.AutoSize = true;
            formatLabel.Margin = new Padding(3, 6, 3, 6);
            layout.SetColumnSpan(formatLabel, 3);

            list.View = View.Details;
            list.FullRowSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("App");
            list.Columns.Add("GitHub");
            list.Columns.Add("Status");
            list.Resize += delegate { SizeColumns(); };
            layout.SetColumnSpan(list, 3);

            progress.Dock = DockStyle.Fill;
            progress.Maximum = 1000;
            layout.SetColumnSpan(progress, 3);

            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Dock = DockStyle.Fill;
            logBox.BackColor = SystemColors.Window;
            layout.SetColumnSpan(logBox, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 0),
            };
            backupButton.Text = "Back up now";
            backupButton.AutoSize = true;
            backupButton.Click += delegate { StartBackup(); };
            cancelButton.Text = "Cancel";
            cancelButton.AutoSize = true;
            cancelButton.Enabled = false;
            cancelButton.Click += delegate { Cancel(); };
            openButton.Text = "Open folder";
            openButton.AutoSize = true;
            openButton.Click += delegate { OpenFolder(); };
            statusLabel.AutoSize = true;
            statusLabel.Margin = new Padding(3, 9, 12, 3);
            buttons.Controls.Add(backupButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(openButton);
            buttons.Controls.Add(statusLabel);
            layout.SetColumnSpan(buttons, 3);

            layout.Controls.Add(folderLabel, 0, 0);
            layout.Controls.Add(folderBox, 1, 0);
            layout.Controls.Add(browseButton, 2, 0);
            layout.Controls.Add(formatLabel, 0, 1);
            layout.Controls.Add(list, 0, 2);
            layout.Controls.Add(progress, 0, 3);
            layout.Controls.Add(logBox, 0, 4);
            layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout);

            ClientSize = new Size(Px(680), Px(520));
            MinimumSize = new Size(Px(520), Px(420));
            AcceptButton = backupButton;

            Load += delegate { Startup(); };
            FormClosing += OnClosing;
        }

        int Px(int px)
        {
            using (var g = CreateGraphics())
                return (int)Math.Round(px * g.DpiX / 96f);
        }

        void SizeColumns()
        {
            int w = list.ClientSize.Width;
            if (w <= 0) return;
            list.Columns[0].Width = w * 22 / 100;
            list.Columns[1].Width = w * 33 / 100;
            list.Columns[2].Width = w - list.Columns[0].Width - list.Columns[1].Width - 4;
        }

        // ------------------------------------------------------------- startup

        void Startup()
        {
            SizeColumns();
            backupButton.Enabled = false;
            statusLabel.Text = "Loading the app list...";
            Log("TempCraftBackup v" + BuildInfo.Version);
            var t = new Thread(delegate ()
            {
                bool sevenZip = Tar.Can7z();
                string source;
                var loaded = Catalog.Load(out source);
                Ui(delegate
                {
                    use7z = sevenZip;
                    apps = loaded;
                    if (Tar.Path == null)
                        formatLabel.Text = "Windows' tar.exe wasn't found, so backups can't be made on this PC.";
                    else if (use7z)
                        formatLabel.Text = "Makes " + ArchiveBase + "_<date>.7z (7z, LZMA2) with a folder of source code for each app.";
                    else
                        formatLabel.Text = "Makes " + ArchiveBase + "_<date>.zip with a folder of source code for each app (this Windows' tar.exe can't write 7z).";
                    Log("Found " + apps.Count + " Craft apps in " + source + ".");
                    FillList();
                    statusLabel.Text = "Ready";
                    backupButton.Enabled = Tar.Path != null;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void FillList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var a in apps)
            {
                var item = new ListViewItem(a.Name);
                item.SubItems.Add(a.Repo);
                item.SubItems.Add("");
                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        // ------------------------------------------------------------ actions

        void Browse()
        {
            string start = Directory.Exists(folderBox.Text.Trim()) ? folderBox.Text.Trim() : null;
            string chosen;
            try
            {
                chosen = FolderPicker.Pick(Handle, "Choose where backups are saved", start);
            }
            catch (Exception)
            {
                // The Explorer-style picker is in every Windows since Vista; this is
                // only a safety net.
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Choose where backups are saved";
                    dlg.ShowNewFolderButton = true;
                    if (start != null) dlg.SelectedPath = start;
                    chosen = dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
                }
            }
            if (chosen != null)
            {
                folderBox.Text = chosen;
                SaveFolder();
            }
        }

        void SaveFolder()
        {
            settings["OutputFolder"] = folderBox.Text.Trim();
            Settings.Save(settings);
        }

        void OpenFolder()
        {
            try
            {
                if (lastArchive != null && File.Exists(lastArchive))
                    Process.Start("explorer.exe", "/select,\"" + lastArchive + "\"");
                else if (Directory.Exists(folderBox.Text.Trim()))
                    Process.Start("explorer.exe", "\"" + folderBox.Text.Trim() + "\"");
            }
            catch (Exception)
            {
            }
        }

        void StartBackup()
        {
            if (worker != null || apps == null) return;
            string folder = folderBox.Text.Trim();
            if (folder.Length == 0)
            {
                MessageBox.Show(this, "Choose a folder to save the backup in first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "That folder can't be used:\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            folderBox.Text = folder;
            SaveFolder();

            cancelled = false;
            backupButton.Enabled = false;
            browseButton.Enabled = false;
            folderBox.ReadOnly = true;
            cancelButton.Enabled = true;
            progress.Value = 0;
            foreach (ListViewItem item in list.Items) item.SubItems[2].Text = "Waiting";
            var snapshot = new List<CraftApp>(apps);
            bool sevenZip = use7z;
            worker = new Thread(delegate () { RunBackup(snapshot, folder, sevenZip); });
            worker.IsBackground = true;
            worker.Start();
        }

        void Cancel()
        {
            if (worker == null) return;
            cancelled = true;
            cancelButton.Enabled = false;
            statusLabel.Text = "Stopping...";
            lock (gate)
            {
                try { if (download != null) download.Abort(); } catch { }
                try { if (tarProcess != null && !tarProcess.HasExited) tarProcess.Kill(); } catch { }
            }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (worker == null) return;
            if (!closeWhenDone)
            {
                var answer = MessageBox.Show(this, "A backup is running. Stop it and close?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            // Close once the backup has stopped and cleaned up after itself.
            closeWhenDone = true;
            e.Cancel = true;
            Cancel();
        }

        // ------------------------------------------------------------- backup

        void RunBackup(List<CraftApp> apps, string folder, bool sevenZip)
        {
            DateTime now = DateTime.Now;
            string scratch = Path.Combine(Path.GetTempPath(), ScratchPrefix + now.ToString("yyyy-MM-dd_HH-mm-ss"));
            // 12-hour time like 5-41PM; Windows doesn't allow ':' in file names.
            string stamp = now.ToString("yyyy-MM-dd_h-mmtt", System.Globalization.CultureInfo.InvariantCulture);
            string stage = Path.Combine(scratch, "stage");
            string ext = sevenZip ? ".7z" : ".zip";
            string target = UniquePath(Path.Combine(folder, ArchiveBase + "_" + stamp + ext));
            string partial = target + ".partial";
            var included = new List<string>();
            var info = new StringBuilder();
            var failed = new List<string>();
            bool ok = false;

            try
            {
                SweepOldScratch();
                Directory.CreateDirectory(stage);
                Log("Backing up " + apps.Count + " apps to " + target);

                info.AppendLine(ArchiveBase + " source code backup");
                info.AppendLine("Created " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " by TempCraftBackup v" + BuildInfo.Version);
                info.AppendLine("Each folder is the app's default branch on GitHub at that moment.");
                info.AppendLine();

                for (int i = 0; i < apps.Count; i++)
                {
                    if (cancelled) throw new OperationCanceledException();
                    var app = apps[i];
                    int index = i;
                    string zip = Path.Combine(scratch, app.Name + ".zip");
                    try
                    {
                        SetStatus(index, "Downloading");
                        Download(app.SourceUrl, zip, delegate (long done, long total)
                        {
                            SetStatus(index, total > 0
                                ? "Downloading " + (done * 100 / total) + "%"
                                : "Downloading " + Mb(done));
                            double frac = total > 0 ? (double)done / total : 0.5;
                            SetProgress((index + frac * 0.8) / apps.Count * 0.6);
                        });
                        if (cancelled) throw new OperationCanceledException();

                        string commit = TopFolderCommit(zip);
                        SetStatus(index, "Unpacking");
                        string dest = Path.Combine(stage, app.Name);
                        Directory.CreateDirectory(dest);
                        // Anything tar couldn't recreate (symlinks need admin rights on
                        // Windows) is noted; the rest of the source is still backed up.
                        string warning = RunTar("-xf \"" + zip + "\" -C \"" + dest + "\" --strip-components 1", null, true);
                        TryDelete(zip);
                        if (warning != null)
                        {
                            if (CountFiles(dest) == 0) throw new Exception(warning);
                            Log(app.Name + ": some files couldn't be unpacked (" + warning + ")");
                            info.AppendLine(app.Name + ": some files couldn't be unpacked (" + warning + ")");
                        }

                        included.Add(app.Name);
                        info.AppendLine(app.Name);
                        info.AppendLine("  https://github.com/" + app.Repo);
                        if (commit != null) info.AppendLine("  commit " + commit);
                        info.AppendLine();
                        SetStatus(index, "Downloaded" + (commit != null ? " (" + commit.Substring(0, 7) + ")" : ""));
                        Log(app.Name + ": downloaded " + app.Repo + (commit != null ? " at " + commit.Substring(0, 7) : ""));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        if (cancelled) throw new OperationCanceledException();
                        TryDelete(zip);
                        failed.Add(app.Name + ": " + e.Message);
                        SetStatus(index, "Failed: " + e.Message);
                        Log(app.Name + ": failed - " + e.Message);
                    }
                    SetProgress((double)(i + 1) / apps.Count * 0.6);
                }

                if (included.Count == 0) throw new Exception("No app's source code could be downloaded.");
                if (failed.Count > 0)
                {
                    info.AppendLine("Not included (couldn't be downloaded):");
                    foreach (string f in failed) info.AppendLine("  " + f);
                }
                File.WriteAllText(Path.Combine(stage, "BACKUP-INFO.txt"), info.ToString(), new UTF8Encoding(false));

                // Pack: one solid LZMA2 7z (or a zip) holding a folder per app.
                Status("Packing " + Path.GetFileName(target) + "...");
                Log("Packing " + included.Count + " apps into " + Path.GetFileName(target));
                int totalEntries = CountFiles(stage);
                var args = new StringBuilder();
                args.Append(sevenZip
                    ? "--format 7zip --options \"7zip:compression=lzma2,7zip:compression-level=9\""
                    : "--format zip --options \"zip:compression-level=9\"");
                args.Append(" -cvf \"" + partial + "\" -C \"" + stage + "\" BACKUP-INFO.txt");
                foreach (string name in included) args.Append(" \"" + name + "\"");
                int packed = 0;
                RunTar(args.ToString(), delegate (string line)
                {
                    if (!line.StartsWith("a ")) return;
                    packed++;
                    if (packed % 25 == 0 && totalEntries > 0)
                        SetProgress(0.6 + 0.4 * Math.Min(1.0, (double)packed / totalEntries));
                }, false);
                if (cancelled) throw new OperationCanceledException();
                File.Move(partial, target);
                SetProgress(1);

                lastArchive = target;
                ok = true;
                long size = new FileInfo(target).Length;
                Log("Done: " + target + " (" + Mb(size) + ")");
                if (failed.Count > 0)
                {
                    Log(failed.Count + " app(s) couldn't be downloaded and aren't in this backup.");
                    Status("Done, but " + failed.Count + " app(s) were skipped");
                }
                else
                {
                    Status("Done: " + Path.GetFileName(target));
                }
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled.");
                Status("Cancelled");
            }
            catch (Exception e)
            {
                Log("Backup failed: " + e.Message);
                Status("Backup failed");
                if (!closeWhenDone)
                    Ui(delegate { MessageBox.Show(this, "The backup failed:\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); });
            }
            finally
            {
                if (!ok) TryDelete(partial);
                DeleteTree(scratch);
                Ui(delegate
                {
                    worker = null;
                    backupButton.Enabled = true;
                    browseButton.Enabled = true;
                    folderBox.ReadOnly = false;
                    cancelButton.Enabled = false;
                    if (!ok) progress.Value = 0;
                    if (cancelled)
                        foreach (ListViewItem item in list.Items)
                        {
                            string st = item.SubItems[2].Text;
                            if (st == "Waiting" || st == "Unpacking" || st.StartsWith("Downloading")) item.SubItems[2].Text = "Cancelled";
                        }
                    if (closeWhenDone) Close();
                });
            }
        }

        delegate void ProgressHandler(long done, long total);

        void Download(string url, string path, ProgressHandler onProgress)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (cancelled) throw new OperationCanceledException();
                try
                {
                    var req = Http.Request(url);
                    lock (gate) download = req;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var body = resp.GetResponseStream())
                    using (var file = File.Create(path))
                    {
                        long total = resp.ContentLength, done = 0;
                        var buffer = new byte[81920];
                        int n;
                        DateTime lastTick = DateTime.MinValue;
                        while ((n = body.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancelled) throw new OperationCanceledException();
                            file.Write(buffer, 0, n);
                            done += n;
                            if ((DateTime.UtcNow - lastTick).TotalMilliseconds > 200)
                            {
                                lastTick = DateTime.UtcNow;
                                onProgress(done, total);
                            }
                        }
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    if (cancelled) throw new OperationCanceledException();
                    last = e;
                    if (attempt < 3) Thread.Sleep(2000 * attempt);
                }
                finally
                {
                    lock (gate) download = null;
                }
            }
            throw last;
        }

        /// GitHub names the zip's top folder <repo>-<commit>; the commit is worth
        /// recording. Also proves the download is a readable zip.
        static string TopFolderCommit(string zip)
        {
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count == 0) throw new Exception("the download was an empty archive");
                string first = archive.Entries[0].FullName;
                int slash = first.IndexOf('/');
                string top = slash > 0 ? first.Substring(0, slash) : first;
                var m = Regex.Match(top, "-([0-9a-f]{40})$");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        delegate void LineHandler(string line);

        /// Runs Windows' tar.exe. A failure throws, or with `lenient` is returned as
        /// tar's last error line; null means it went cleanly.
        string RunTar(string args, LineHandler onLine, bool lenient)
        {
            var psi = new ProcessStartInfo(Tar.Path, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            var errors = new List<string>();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                // bsdtar's -v lines ("a path") and errors both arrive on stderr.
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (onLine != null) onLine(e.Data);
                    if (!e.Data.StartsWith("a ") && !e.Data.StartsWith("x ")) lock (errors) errors.Add(e.Data);
                };
                p.OutputDataReceived += delegate { };
                lock (gate)
                {
                    if (cancelled) throw new OperationCanceledException();
                    p.Start();
                    tarProcess = p;
                }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                p.WaitForExit();
                lock (gate) tarProcess = null;
                if (cancelled) throw new OperationCanceledException();
                if (p.ExitCode == 0) return null;
                string detail;
                lock (errors) detail = errors.Count > 0 ? errors[errors.Count - 1] : "exit code " + p.ExitCode;
                if (lenient) return detail;
                throw new Exception("tar.exe: " + detail);
            }
        }

        // ------------------------------------------------------------ helpers

        static int CountFiles(string dir)
        {
            try
            {
                return Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static void DeleteTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // rd copes with read-only files and very long paths.
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c rd /s /q \"" + dir + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi)) p.WaitForExit();
                }
                catch (Exception)
                {
                }
            }
        }

        /// Scratch folders a crash or a forced close left behind.
        static void SweepOldScratch()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), ScratchPrefix + "*"))
                    DeleteTree(dir);
            }
            catch (Exception)
            {
            }
        }

        static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString(bytes < 10485760 ? "0.0" : "0") + " MB";
        }

        // ----------------------------------------------------------------- UI

        void Ui(MethodInvoker action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch (InvalidOperationException) { }
            }
            else
            {
                action();
            }
        }

        void Log(string line)
        {
            string text = DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
            Ui(delegate { logBox.AppendText(text); });
        }

        void Status(string text)
        {
            Ui(delegate { statusLabel.Text = text; });
        }

        void SetStatus(int index, string text)
        {
            Ui(delegate { if (index < list.Items.Count) list.Items[index].SubItems[2].Text = text; });
        }

        void SetProgress(double frac)
        {
            int value = (int)Math.Round(Math.Max(0, Math.Min(1, frac)) * 1000);
            Ui(delegate { progress.Value = value; });
        }
    }
}
