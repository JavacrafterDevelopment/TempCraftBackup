// TempCraftBackup: downloads the source code of every Craft app in the Launcher's
// catalog (the latest commit, or the latest release) and packs it into one
// archive, optionally with each app's full git history.
//
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.8's
// csc.exe), so it builds and runs with nothing installed. Windows' own tar.exe
// (bsdtar) does the unpacking and the 7z packing, and the portable Git in the
// git\ folder beside the exe (MinGit, from the Git for Windows project) does the
// cloning.
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
using System.Web.Script.Serialization;
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

        /// The source zip GitHub makes for `tag`, or for the default branch's latest
        /// commit when `tag` is null.
        public string SourceZipUrl(string tag)
        {
            return tag == null
                ? "https://github.com/" + Repo + "/archive/HEAD.zip"
                : "https://github.com/" + Repo + "/archive/refs/tags/" + Uri.EscapeDataString(tag) + ".zip";
        }

        public string CloneUrl
        {
            get { return "https://github.com/" + Repo + ".git"; }
        }
    }

    /// What one backup run does, fixed when it starts.
    class BackupOptions
    {
        public bool SevenZip;
        public bool GitHistory;
        public bool LatestRelease;
        public bool Prereleases;
    }

    class Release
    {
        public string Tag;
        public bool Prerelease;
        /// Set when the answer is less complete than asked for.
        public string Note;
    }

    /// Finds an app's latest GitHub release the way the Launcher does: the API
    /// first, then the website when the API's hourly limit is used up or it answers
    /// with an empty list.
    static class Releases
    {
        /// The newest release (with `prereleases`, of any kind; otherwise the newest
        /// stable one), or null when there is none. Throws when GitHub can't be asked.
        public static Release Latest(string repo, bool prereleases)
        {
            string body;
            int status = Http.GetApi("https://api.github.com/repos/" + repo + "/releases?per_page=30", out body);
            // The anonymous API allows 60 requests an hour per IP. The website isn't
            // limited that way, but only tells the newest stable release.
            if (status == 403 || status == 429)
                return ViaWebsite(repo, prereleases ? "GitHub's hourly API limit was used up, so only stable releases could be checked" : null);
            if (status == 404) throw new Exception("the repository wasn't found, or it's private");
            if (status != 200) throw new Exception("GitHub answered with HTTP " + status);

            var list = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(body) as object[];
            if (list == null) throw new Exception("GitHub sent a release list that couldn't be read");
            // The API now and then answers with an empty list for a repo that has
            // releases, so the release page gets the last word before "none".
            if (list.Length == 0) return ViaWebsite(repo, null);
            return Pick(list, prereleases);
        }

        /// The newest release in the API's list, skipping drafts (and pre-releases
        /// unless `prereleases`).
        static Release Pick(object[] list, bool prereleases)
        {
            Release best = null;
            string bestPublished = null;
            foreach (object item in list)
            {
                var r = item as Dictionary<string, object>;
                if (r == null) continue;
                string tag = Text(r, "tag_name");
                if (string.IsNullOrEmpty(tag) || Flag(r, "draft")) continue;
                bool pre = Flag(r, "prerelease");
                if (pre && !prereleases) continue;
                string published = Text(r, "published_at") ?? "";
                if (best == null || Newer(tag, published, best.Tag, bestPublished))
                {
                    best = new Release { Tag = tag, Prerelease = pre };
                    bestPublished = published;
                }
            }
            return best;
        }

        /// `/releases/latest` redirects to the newest stable release's tag page, or
        /// to `/releases` when there is none.
        static Release ViaWebsite(string repo, string note)
        {
            string path = Http.Landing("https://github.com/" + repo + "/releases/latest").AbsolutePath;
            const string marker = "/releases/tag/";
            int at = path.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            string tag = Uri.UnescapeDataString(path.Substring(at + marker.Length)).Trim('/');
            if (tag.Length == 0) return null;
            return new Release { Tag = tag, Prerelease = false, Note = note };
        }

        static string Text(Dictionary<string, object> r, string key)
        {
            object v;
            return r.TryGetValue(key, out v) ? v as string : null;
        }

        static bool Flag(Dictionary<string, object> r, string key)
        {
            object v;
            return r.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static readonly Regex VersionPattern = new Regex(@"^[vV]?(\d{1,9})(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?([-+].*)?$");

        /// Compares tags as versions (v1.2.3, with 1.2.3-beta below 1.2.3) when both
        /// are versions, and otherwise by when they were published.
        static bool Newer(string tag, string published, string otherTag, string otherPublished)
        {
            Match a = VersionPattern.Match(tag), b = VersionPattern.Match(otherTag);
            if (a.Success && b.Success)
            {
                for (int i = 1; i <= 3; i++)
                {
                    int x = a.Groups[i].Success ? int.Parse(a.Groups[i].Value) : 0;
                    int y = b.Groups[i].Success ? int.Parse(b.Groups[i].Value) : 0;
                    if (x != y) return x > y;
                }
                bool aPre = a.Groups[4].Value.StartsWith("-"), bPre = b.Groups[4].Value.StartsWith("-");
                if (aPre != bPre) return !aPre;
            }
            return string.CompareOrdinal(published, otherPublished) > 0;
        }
    }

    /// The portable Git that ships in the git\ folder beside the exe: MinGit, the
    /// minimal Git for Windows build made for apps to bundle.
    static class Git
    {
        public static string Path
        {
            get
            {
                string dir = System.IO.Path.Combine(Settings.AppDir, "git");
                foreach (string rel in new[] { "cmd\\git.exe", "mingw64\\bin\\git.exe" })
                {
                    string p = System.IO.Path.Combine(dir, rel);
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        /// "git version 2.x.y.windows.n", or null when the bundled Git is missing or
        /// won't run.
        public static string Version()
        {
            string git = Path;
            if (git == null) return null;
            try
            {
                var psi = new ProcessStartInfo(git, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    return p.ExitCode == 0 && output.Length > 0 ? output : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
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

        /// GETs a GitHub API URL: the HTTP status, with the body when it's 200.
        /// Throws only when GitHub can't be reached at all.
        public static int GetApi(string url, out string body)
        {
            var req = Request(url);
            req.Accept = "application/vnd.github+json";
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw;
                using (resp)
                {
                    body = "";
                    return (int)resp.StatusCode;
                }
            }
        }

        /// Where a URL ends up after its redirects.
        public static Uri Landing(string url)
        {
            var req = Request(url);
            req.Method = "HEAD";
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
                return resp.ResponseUri;
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
        readonly CheckBox historyBox = new CheckBox();
        readonly CheckBox releaseBox = new CheckBox();
        readonly CheckBox betaBox = new CheckBox();
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
        bool tarFound;
        string gitVersion;
        bool loadingOptions;
        string lastArchive;

        // Set while a backup runs.
        Thread worker;
        volatile bool cancelled;
        volatile bool closeWhenDone;
        readonly object gate = new object();
        Process childProcess;
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
                RowCount = 7,
                Padding = new Padding(10),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

            var options = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 0),
            };
            var tips = new ToolTip();
            loadingOptions = true;
            historyBox.Text = "Include git history";
            historyBox.Checked = OptionOn("IncludeGitHistory");
            tips.SetToolTip(historyBox, "Each app's folder is a full git clone with its .git folder: every commit, branch and tag.\nMakes the backup several times bigger and slower.");
            releaseBox.Text = "Use latest release";
            releaseBox.Checked = OptionOn("UseLatestRelease");
            tips.SetToolTip(releaseBox, "Back up the source code of each app's latest GitHub release instead of its latest commit.\nApps with no release fall back to their latest commit.");
            betaBox.Text = "Include betas / pre-releases";
            betaBox.Checked = OptionOn("IncludePrereleases");
            tips.SetToolTip(betaBox, "Count pre-releases (betas) when picking each app's latest release.");
            loadingOptions = false;
            foreach (var box in new[] { historyBox, releaseBox, betaBox })
            {
                box.AutoSize = true;
                box.Margin = new Padding(3, 3, 16, 3);
                box.CheckedChanged += delegate { OptionChanged(); };
                options.Controls.Add(box);
            }
            layout.SetColumnSpan(options, 3);

            formatLabel.AutoSize = true;
            formatLabel.Margin = new Padding(3, 6, 3, 6);
            layout.SetColumnSpan(formatLabel, 3);
            // Wrap the description to the window's width.
            layout.Resize += delegate
            {
                formatLabel.MaximumSize = new Size(Math.Max(Px(200), layout.ClientSize.Width - layout.Padding.Horizontal - 8), 0);
            };

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
            layout.Controls.Add(options, 0, 1);
            layout.Controls.Add(formatLabel, 0, 2);
            layout.Controls.Add(list, 0, 3);
            layout.Controls.Add(progress, 0, 4);
            layout.Controls.Add(logBox, 0, 5);
            layout.Controls.Add(buttons, 0, 6);
            Controls.Add(layout);

            UpdateOptions();
            ClientSize = new Size(Px(700), Px(560));
            MinimumSize = new Size(Px(540), Px(460));
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
                bool tar = Tar.Path != null;
                string git = Git.Version();
                string source;
                var loaded = Catalog.Load(out source);
                Ui(delegate
                {
                    use7z = sevenZip;
                    tarFound = tar;
                    gitVersion = git;
                    apps = loaded;
                    if (gitVersion != null)
                    {
                        Log("Bundled Git: " + gitVersion + ".");
                    }
                    else
                    {
                        Log("The bundled Git (the git folder beside TempCraftBackup.exe) wasn't found, so git history can't be included.");
                        historyBox.Text = "Include git history (bundled Git not found)";
                        // Shown off, without forgetting the saved choice.
                        loadingOptions = true;
                        historyBox.Checked = false;
                        loadingOptions = false;
                    }
                    Log("Found " + apps.Count + " Craft apps in " + source + ".");
                    FillList();
                    UpdateOptions();
                    statusLabel.Text = "Ready";
                    backupButton.Enabled = tarFound;
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

        // ------------------------------------------------------------ options

        bool OptionOn(string key)
        {
            string v;
            return settings.TryGetValue(key, out v) && v == "1";
        }

        void OptionChanged()
        {
            if (loadingOptions) return;
            settings["IncludeGitHistory"] = historyBox.Checked ? "1" : "0";
            settings["UseLatestRelease"] = releaseBox.Checked ? "1" : "0";
            settings["IncludePrereleases"] = betaBox.Checked ? "1" : "0";
            Settings.Save(settings);
            UpdateOptions();
        }

        bool GitHistoryOn
        {
            get { return historyBox.Checked && gitVersion != null; }
        }

        /// Which options can be changed right now, and what a backup will make.
        void UpdateOptions()
        {
            bool idle = worker == null;
            historyBox.Enabled = idle && gitVersion != null;
            releaseBox.Enabled = idle;
            betaBox.Enabled = idle && releaseBox.Checked;

            if (apps == null)
            {
                formatLabel.Text = "Checking this PC...";
                return;
            }
            if (!tarFound)
            {
                formatLabel.Text = "Windows' tar.exe wasn't found, so backups can't be made on this PC.";
                return;
            }
            var sb = new StringBuilder();
            sb.Append(use7z
                ? "Makes " + ArchiveBase + "_<date>_<time>.7z (7z, LZMA2) with a folder for each app: "
                : "Makes " + ArchiveBase + "_<date>_<time>.zip (this Windows' tar.exe can't write 7z) with a folder for each app: ");
            if (releaseBox.Checked)
                sb.Append("its latest " + (betaBox.Checked ? "release or pre-release" : "release") +
                    " in <App>_<version>, or its latest commit in <App>_<date> if it has no release");
            else
                sb.Append("its latest commit in <App>_<date>");
            sb.Append(GitHistoryOn ? ", with the full git history." : ", source files only.");
            formatLabel.Text = sb.ToString();
        }

        BackupOptions CurrentOptions()
        {
            return new BackupOptions
            {
                SevenZip = use7z,
                GitHistory = GitHistoryOn,
                LatestRelease = releaseBox.Checked,
                Prereleases = releaseBox.Checked && betaBox.Checked,
            };
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
            var options = CurrentOptions();
            worker = new Thread(delegate () { RunBackup(snapshot, folder, options); });
            worker.IsBackground = true;
            UpdateOptions();
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
                KillTree(childProcess);
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

        void RunBackup(List<CraftApp> apps, string folder, BackupOptions options)
        {
            DateTime now = DateTime.Now;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            string scratch = Path.Combine(Path.GetTempPath(), ScratchPrefix + now.ToString("yyyy-MM-dd_HH-mm-ss", invariant));
            // 12-hour time like 5-41PM; Windows doesn't allow ':' in file names.
            string stamp = now.ToString("yyyy-MM-dd_h-mmtt", invariant);
            // An app's folder is named for the backup's date when it holds a latest commit.
            string day = now.ToString("yyyy-MM-dd", invariant);
            string stage = Path.Combine(scratch, "stage");
            string ext = options.SevenZip ? ".7z" : ".zip";
            string target = UniquePath(Path.Combine(folder, ArchiveBase + "_" + stamp + ext));
            string partial = target + ".partial";
            var included = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var info = new StringBuilder();
            var failed = new List<string>();
            bool ok = false;

            try
            {
                SweepOldScratch();
                Directory.CreateDirectory(stage);
                Log("Backing up " + apps.Count + " apps to " + target);
                Log("Source: " + (options.LatestRelease
                        ? "latest " + (options.Prereleases ? "release or pre-release" : "release") + " (latest commit for apps with no release)"
                        : "latest commit on each default branch") +
                    "; git history " + (options.GitHistory ? "included" : "not included") + ".");

                info.AppendLine(ArchiveBase + " source code backup");
                info.AppendLine("Created " + now.ToString("yyyy-MM-dd HH:mm:ss", invariant) + " by TempCraftBackup v" + BuildInfo.Version);
                info.AppendLine();
                if (options.LatestRelease)
                {
                    info.AppendLine("Source: each app's latest " + (options.Prereleases ? "release, pre-releases included" : "stable release") +
                        ", in a folder named <App>_<version>.");
                    info.AppendLine("        An app with no release has its default branch's latest commit instead,");
                    info.AppendLine("        in a folder named <App>_<date of this backup>.");
                }
                else
                {
                    info.AppendLine("Source: the latest commit on each app's default branch at the time of this backup,");
                    info.AppendLine("        in a folder named <App>_<date of this backup>.");
                }
                info.AppendLine(options.GitHistory
                    ? "Git history: included. Each folder is a full git clone (every commit, branch and tag) with its .git folder."
                    : "Git history: not included. Each folder holds the source files only.");
                info.AppendLine();

                for (int i = 0; i < apps.Count; i++)
                {
                    if (cancelled) throw new OperationCanceledException();
                    var app = apps[i];
                    int index = i;
                    string zip = Path.Combine(scratch, app.Name + ".zip");
                    string dest = null;
                    try
                    {
                        var notes = new List<string>();

                        // Which code: the latest release's tag, or null for the latest commit.
                        Release release = null;
                        if (options.LatestRelease)
                        {
                            SetStatus(index, "Finding the latest release");
                            try
                            {
                                release = Releases.Latest(app.Repo, options.Prereleases);
                                if (release == null)
                                    notes.Add("no " + (options.Prereleases ? "release" : "stable release") + " is published, so this is the latest commit");
                                else if (release.Note != null)
                                    notes.Add(release.Note);
                            }
                            catch (Exception e)
                            {
                                if (cancelled) throw new OperationCanceledException();
                                notes.Add("its releases couldn't be checked (" + e.Message + "), so this is the latest commit");
                            }
                        }
                        string tag = release != null ? release.Tag : null;

                        string name = UniqueName(app.Name + "_" + (tag != null ? SafeFileName(tag) : day), usedNames);
                        dest = Path.Combine(stage, name);

                        string commit = null;
                        bool history = false;
                        if (options.GitHistory)
                        {
                            try
                            {
                                commit = Clone(app, tag, dest, index, apps.Count);
                                history = true;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception e)
                            {
                                if (cancelled) throw new OperationCanceledException();
                                DeleteTree(dest);
                                notes.Add("git history couldn't be cloned (" + e.Message + "), so this folder has the source files only");
                                Log(app.Name + ": cloning failed (" + e.Message + "); downloading the source files instead");
                            }
                        }
                        if (!history)
                        {
                            string warning;
                            commit = DownloadSource(app, tag, dest, zip, index, apps.Count, out warning);
                            if (warning != null) notes.Add("some files couldn't be unpacked (" + warning + ")");
                        }

                        included.Add(name);
                        usedNames.Add(name);
                        info.AppendLine(name);
                        info.AppendLine("  https://github.com/" + app.Repo);
                        if (tag != null) info.AppendLine("  release " + tag + (release.Prerelease ? " (pre-release)" : ""));
                        else info.AppendLine("  latest commit on the default branch");
                        if (commit != null) info.AppendLine("  commit " + commit);
                        info.AppendLine(history ? "  git history included" : "  source files only");
                        foreach (string n in notes) info.AppendLine("  note: " + n);
                        info.AppendLine();

                        string shortCommit = commit != null ? commit.Substring(0, 7) : null;
                        var done = new StringBuilder("Done: ");
                        done.Append(tag ?? "latest commit");
                        if (shortCommit != null) done.Append(" (" + shortCommit + ")");
                        if (history) done.Append(", with history");
                        if (notes.Count > 0) done.Append(" - see log");
                        SetStatus(index, done.ToString());
                        Log(app.Name + ": backed up " + app.Repo + " " + (tag != null ? "release " + tag : "latest commit") +
                            (shortCommit != null ? " at " + shortCommit : "") + (history ? " with git history" : "") + " as " + name);
                        foreach (string n in notes) Log(app.Name + ": " + n);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        if (cancelled) throw new OperationCanceledException();
                        TryDelete(zip);
                        if (dest != null) DeleteTree(dest);
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
                args.Append(options.SevenZip
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
                    UpdateOptions();
                    if (!ok) progress.Value = 0;
                    if (cancelled)
                        foreach (ListViewItem item in list.Items)
                        {
                            string st = item.SubItems[2].Text;
                            if (st == "Waiting" || st == "Unpacking" || st.StartsWith("Downloading") ||
                                st.StartsWith("Cloning") || st.StartsWith("Finding"))
                                item.SubItems[2].Text = "Cancelled";
                        }
                    if (closeWhenDone) Close();
                });
            }
        }

        /// Downloads the source zip GitHub makes for `tag` (or the default branch's
        /// latest commit) and unpacks it into `dest`. Returns the commit it holds, when
        /// known; `warning` says what tar couldn't recreate, if anything.
        string DownloadSource(CraftApp app, string tag, string dest, string zip, int index, int count, out string warning)
        {
            SetStatus(index, "Downloading");
            Download(app.SourceZipUrl(tag), zip, delegate (long done, long total)
            {
                SetStatus(index, total > 0
                    ? "Downloading " + (done * 100 / total) + "%"
                    : "Downloading " + Mb(done));
                double frac = total > 0 ? (double)done / total : 0.5;
                SetProgress((index + frac * 0.8) / count * 0.6);
            });
            if (cancelled) throw new OperationCanceledException();

            string commit = ZipCommit(zip);
            SetStatus(index, "Unpacking");
            Directory.CreateDirectory(dest);
            // Anything tar couldn't recreate (symlinks need admin rights on Windows)
            // is noted; the rest of the source is still backed up.
            warning = RunTar("-xf \"" + zip + "\" -C \"" + dest + "\" --strip-components 1", null, true);
            TryDelete(zip);
            if (warning != null && CountFiles(dest) == 0) throw new Exception(warning);
            return commit;
        }

        static readonly Regex GitProgress = new Regex(@"^(?:remote: )?([A-Za-z ]+):\s+(\d+)%");

        /// Clones the app's whole repository (every commit, branch and tag) into
        /// `dest`, checked out at `tag`, or at the default branch when it's null.
        /// Returns the checked-out commit.
        string Clone(CraftApp app, string tag, string dest, int index, int count)
        {
            SetStatus(index, "Cloning");
            // core.autocrlf=false keeps files exactly as committed (no CRLF
            // conversion) and core.longpaths=true lets deep source trees check out;
            // both stay in the backed-up repo's config for wherever it's restored.
            var args = new StringBuilder("-c advice.detachedHead=false clone --progress -c core.autocrlf=false -c core.longpaths=true");
            if (tag != null) args.Append(" --branch \"" + tag + "\"");
            args.Append(" -- \"" + app.CloneUrl + "\" \"" + dest + "\"");
            DateTime lastTick = DateTime.MinValue;
            RunGit(args.ToString(), delegate (string line)
            {
                var m = GitProgress.Match(line);
                if (!m.Success) return;
                if ((DateTime.UtcNow - lastTick).TotalMilliseconds < 200) return;
                lastTick = DateTime.UtcNow;
                string phase = m.Groups[1].Value.Trim();
                int pct = int.Parse(m.Groups[2].Value);
                double frac;
                if (phase == "Receiving objects") frac = 0.05 + 0.75 * pct / 100.0;
                else if (phase == "Resolving deltas") frac = 0.8 + 0.1 * pct / 100.0;
                else if (phase == "Updating files") frac = 0.9 + 0.1 * pct / 100.0;
                else frac = 0.05 * pct / 100.0;
                SetStatus(index, "Cloning: " + phase.ToLowerInvariant() + " " + pct + "%");
                SetProgress((index + frac) / count * 0.6);
            }, null);
            if (cancelled) throw new OperationCanceledException();

            var output = new List<string>();
            RunGit("-C \"" + dest + "\" rev-parse HEAD", null, output);
            string commit = output.Count > 0 ? output[0].Trim() : "";
            return Regex.IsMatch(commit, "^[0-9a-f]{40}$") ? commit : null;
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

        /// The commit a GitHub source zip holds. GitHub writes it as the zip's
        /// comment (git archive does), and for a branch also names the top folder
        /// <repo>-<commit>. Also proves the download is a readable zip.
        static string ZipCommit(string zip)
        {
            string top;
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count == 0) throw new Exception("the download was an empty archive");
                string first = archive.Entries[0].FullName;
                int slash = first.IndexOf('/');
                top = slash > 0 ? first.Substring(0, slash) : first;
            }
            string comment = ZipComment(zip);
            if (comment != null && Regex.IsMatch(comment, "^[0-9a-f]{40}$")) return comment;
            var m = Regex.Match(top, "-([0-9a-f]{40})$");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// The comment in a zip's end-of-central-directory record, or null.
        static string ZipComment(string zip)
        {
            try
            {
                using (var f = File.OpenRead(zip))
                {
                    // The record is 22 bytes plus a comment of up to 65535.
                    int len = (int)Math.Min(f.Length, 22 + 65535);
                    var buf = new byte[len];
                    f.Seek(-len, SeekOrigin.End);
                    int read = 0;
                    while (read < len)
                    {
                        int n = f.Read(buf, read, len - read);
                        if (n <= 0) return null;
                        read += n;
                    }
                    for (int i = len - 22; i >= 0; i--)
                    {
                        if (buf[i] != 0x50 || buf[i + 1] != 0x4b || buf[i + 2] != 0x05 || buf[i + 3] != 0x06) continue;
                        int commentLength = buf[i + 20] | (buf[i + 21] << 8);
                        if (i + 22 + commentLength != len) continue;
                        return Encoding.ASCII.GetString(buf, i + 22, commentLength).Trim();
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        delegate void LineHandler(string line);

        /// Runs Windows' tar.exe. A failure throws, or with `lenient` is returned as
        /// tar's last error line; null means it went cleanly.
        string RunTar(string args, LineHandler onLine, bool lenient)
        {
            var psi = new ProcessStartInfo(Tar.Path, args);
            // bsdtar's -v lines ("a path", "x path") and errors both arrive on stderr.
            return RunProcess(psi, "tar.exe", onLine, null,
                delegate (string line) { return !line.StartsWith("a ") && !line.StartsWith("x "); }, lenient);
        }

        /// Runs the bundled git.exe; a failure throws. Progress and messages arrive
        /// on stderr (to `onLine`); `stdout`, when given, collects what it prints.
        void RunGit(string args, LineHandler onLine, List<string> stdout)
        {
            var psi = new ProcessStartInfo(Git.Path, args);
            // Never stop to ask for a username or password: these are public repos.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            RunProcess(psi, "git", onLine, stdout,
                delegate (string line) { return line.StartsWith("fatal:") || line.StartsWith("error:"); }, false);
        }

        /// Runs a tool and waits for it. Lines on stderr go to `onLine`, and those
        /// `isError` picks out are kept for the error message. A failure throws, or
        /// with `lenient` is returned as the last error line; null means it went
        /// cleanly. Cancelling kills it.
        string RunProcess(ProcessStartInfo psi, string tool, LineHandler onLine, List<string> stdout, Predicate<string> isError, bool lenient)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            var errors = new List<string>();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (onLine != null) onLine(e.Data);
                    if (isError(e.Data)) lock (errors) errors.Add(e.Data);
                };
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && stdout != null) lock (stdout) stdout.Add(e.Data);
                };
                lock (gate)
                {
                    if (cancelled) throw new OperationCanceledException();
                    p.Start();
                    childProcess = p;
                }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                p.WaitForExit();
                lock (gate) childProcess = null;
                if (cancelled) throw new OperationCanceledException();
                if (p.ExitCode == 0) return null;
                string detail;
                lock (errors) detail = errors.Count > 0 ? errors[errors.Count - 1] : "exit code " + p.ExitCode;
                if (lenient) return detail;
                throw new Exception(tool + ": " + detail);
            }
        }

        /// Ends a process and everything it started (git runs helpers such as
        /// git-remote-https.exe).
        static void KillTree(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;
            }
            catch (Exception)
            {
                return;
            }
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/T /F /PID " + p.Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var k = Process.Start(psi)) k.WaitForExit(5000);
            }
            catch (Exception)
            {
            }
            try { if (!p.HasExited) p.Kill(); } catch { }
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

        /// `name` made safe for a Windows file name (tags can hold characters
        /// such as '/').
        static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '-' : c);
            string safe = sb.ToString().TrimEnd('.', ' ');
            return safe.Length > 0 ? safe : "-";
        }

        /// `name`, or `name (2)`, `name (3)`... if another app already has it.
        static string UniqueName(string name, HashSet<string> used)
        {
            if (!used.Contains(name)) return name;
            for (int i = 2; ; i++)
            {
                string n = name + " (" + i + ")";
                if (!used.Contains(n)) return n;
            }
        }

        static void DeleteTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                // Git makes its object files read-only, which Directory.Delete refuses.
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attrs = File.GetAttributes(f);
                        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
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
