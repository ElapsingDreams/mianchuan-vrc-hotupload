using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace VrcaUploader
{
    public partial class MainForm : Form
    {
        readonly string _baseDir;
        readonly VrcaClient _authClient;
        readonly List<UploadJob> _jobs = new List<UploadJob>();
        readonly object _jobsLock = new object();
        readonly object _scanProbeCacheLock = new object();
        Dictionary<string, ScanProbeCacheEntry> _scanProbeCache;
        bool _scanProbeCacheLoaded;
        bool _scanProbeCacheDirty;

        Panel _loginView;
        Panel _loginFormCard;
        Panel _loginSuccessCard;
        Panel _appView;
        Panel _taskEditorCard;
        TextBox _user;
        TextBox _pass;
        TextBox _twoFa;
        Button _loginBtn;
        Button _enterAppBtn;
        Label _loginStatus;
        Label _loggedUserLabel;
        Label _appUserLabel;

        TextBox _vrca;
        TextBox _image;
        TextBox _name;
        TextBox _desc;
        ComboBox _release;
        Button _addTaskBtn;
        Button _preflightBtn;
        Button _downloadBtn;

        // VRCW mode fields
        TextBox _vrcw;
        TextBox _vrcwQuest;
        TextBox _vrcwImage;
        TextBox _vrcwName;
        TextBox _worldIdBox;
        WorldSignatureInputs _worldSignatureInputs;
        Label _worldPairStatus;
        Panel _vrcaModePanel;
        Panel _vrcwModePanel;
        Panel _scanModePanel;
        Button _modeVrcaBtn;
        Button _modeVrcwBtn;
        Button _modeScanBtn;
        TextBox _scanFolder;
        Button _scanStartBtn;
        Label _scanStatus;
        int _uploadMode;
        int _scanGeneration;
        bool _scanBusy;
        bool _scanWorkspaceExpanded;
        Label _customerStatus;
        Panel _detailCard;
        FolderScanWorkspace _scanWorkspace;
        FlowLayoutPanel _progressList;
        Label _emptyProgressLabel;
        bool _loginBusy;
        bool _awaiting2FA;
        bool _loggedIn;
        bool _preflightBusy;
        int _nextJobId = 1;
        int _runningJobs;
        const string MaxUploadJobsEnv = "HOTUPLOAD_MAX_JOBS";
        const string AuthDirOverrideEnv = "HOTUPLOAD_AUTH_DIR";
        const string PrimaryGuiMutexName = "Local\\Mianchuan.VrcHotUpload.Gui";
        string _pendingUser = "";
        string _pending2FAType = "totp";
        string _currentUser = "";

        static Mutex _primaryGuiMutex;
        static bool _ownsPrimaryGuiMutex;
        static bool _isolatedGuiAuthSession;
        static string _ephemeralAuthDir = "";
        static string _authSessionsRoot = "";

        static readonly Color Bg = HotUploadPreview.Theme.Back;
        static readonly Color Card = HotUploadPreview.Theme.Surface;
        static readonly Color Card2 = HotUploadPreview.Theme.Line;
        static readonly Color TextMain = Color.FromArgb(245, 247, 250);
        static readonly Color TextMuted = Color.FromArgb(154, 160, 176);
        static readonly Color Accent = HotUploadPreview.Theme.Mint;
        static readonly Color Accent2 = HotUploadPreview.Theme.Cyan;
        static readonly Color Danger = Color.FromArgb(248, 113, 113);
        static readonly Color Warn = Color.FromArgb(251, 191, 36);

        // The window deliberately keeps a fixed pixel footprint so it remains usable on
        // 1366/1440-wide support machines. Fonts must therefore use pixel units as well.
        // Point fonts grow with Windows DPI (150% on this workstation) while the controls do
        // not, which was the root cause of clipped titles, labels and button captions.
        static Font UiFont(float pointSize)
        {
            return UiFont("Microsoft YaHei UI", pointSize, FontStyle.Regular);
        }

        static Font UiFont(float pointSize, FontStyle style)
        {
            return UiFont("Microsoft YaHei UI", pointSize, style);
        }

        static Font UiFont(string family, float pointSize, FontStyle style)
        {
            // Use a 110-DPI visual baseline: 15% larger than the previous conservative
            // 96-DPI baseline, while remaining independent from Windows display scaling.
            return new Font(family, pointSize * 110f / 72f, style, GraphicsUnit.Pixel);
        }

        [STAThread]
        static void Main()
        {
            if (!WorkspaceSelfTest) ConfigureGuiAuthSession();
            try
            {
                TrySetDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            finally
            {
                CleanupGuiAuthSession();
            }
        }

        static void ConfigureGuiAuthSession()
        {
            var createdNew = false;
            try
            {
                _primaryGuiMutex = new Mutex(true, PrimaryGuiMutexName, out createdNew);
                _ownsPrimaryGuiMutex = createdNew;
            }
            catch
            {
                createdNew = false;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            _authSessionsRoot = Path.Combine(local, "Mianchuan.VrcHotUpload", "sessions");
            TryCleanupStaleGuiAuthSessions(_authSessionsRoot);

            if (createdNew)
            {
                Environment.SetEnvironmentVariable(AuthDirOverrideEnv, null, EnvironmentVariableTarget.Process);
                return;
            }

            _isolatedGuiAuthSession = true;
            _ephemeralAuthDir = Path.Combine(
                _authSessionsRoot,
                "gui-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_ephemeralAuthDir);
            Environment.SetEnvironmentVariable(AuthDirOverrideEnv, _ephemeralAuthDir, EnvironmentVariableTarget.Process);
        }

        static void CleanupGuiAuthSession()
        {
            if (_isolatedGuiAuthSession)
                TryDeleteGuiAuthSessionDirectory(_ephemeralAuthDir, _authSessionsRoot);

            try
            {
                if (_ownsPrimaryGuiMutex && _primaryGuiMutex != null)
                    _primaryGuiMutex.ReleaseMutex();
            }
            catch { }
            try { if (_primaryGuiMutex != null) _primaryGuiMutex.Dispose(); } catch { }
            _primaryGuiMutex = null;
        }

        static void TryCleanupStaleGuiAuthSessions(string sessionsRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionsRoot) || !Directory.Exists(sessionsRoot)) return;
                var cutoff = DateTime.UtcNow - TimeSpan.FromDays(2);
                foreach (var directory in Directory.GetDirectories(sessionsRoot, "gui-*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(directory) ?? "";
                    var parts = name.Split('-');
                    int processId;
                    if (parts.Length >= 3 && int.TryParse(parts[1], out processId))
                    {
                        if (IsProcessRunning(processId)) continue;
                    }
                    else if (Directory.GetLastWriteTimeUtc(directory) > cutoff)
                    {
                        continue;
                    }
                    TryDeleteGuiAuthSessionDirectory(directory, sessionsRoot);
                }
            }
            catch { }
        }

        static bool IsProcessRunning(int processId)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch { return false; }
        }

        static void TryDeleteGuiAuthSessionDirectory(string path, string sessionsRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sessionsRoot)) return;
                var fullRoot = Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                if (!(Path.GetFileName(fullPath) ?? "").StartsWith("gui-", StringComparison.OrdinalIgnoreCase)) return;
                if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static void TrySetDpiAware()
        {
            try { SetProcessDPIAware(); } catch { }
        }











        void OpenScanWorkspace()
        {
            SwitchUploadMode(2);
            if (_scanWorkspace != null && !_scanWorkspace.HasResults)
            {
                string folder;
                List<BatchUploadSelection> cachedRows;
                if (TryLoadScanCatalog(out folder, out cachedRows))
                {
                    _scanFolder.Text = folder;
                    _scanWorkspace.RestoreCachedResults(folder, ResolveImagePath(), cachedRows);
                    _scanStatus.Text = "已恢复上次扫描结果；文件发生变化时重新扫描即可。";
                }
            }
            if (_scanWorkspace != null && _scanWorkspace.HasResults)
                SetScanWorkspaceExpanded(true);
        }



        void SetModeButtonState(Button button, bool active)
        {
            button.BackColor = active ? Accent : Card2;
            button.ForeColor = active ? Color.FromArgb(12, 28, 20) : TextMuted;
        }

        sealed partial class WorldSignatureInputs : Panel
        {
            readonly TextBox pcFile;
            readonly TextBox questFile;
            readonly Label questLabel;
            readonly Button questBrowse;
            string targetId = "";



            static void Pick(TextBox box)
            {
                using (var dialog = new OpenFileDialog {
                    Title = "选择 worldSignature 纯值文本", Filter = "签名文本 (*.txt)|*.txt|所有文件 (*.*)|*.*"
                })
                    if (dialog.ShowDialog(box.FindForm()) == DialogResult.OK) box.Text = dialog.FileName;
            }

            public void SetTarget(string id, bool hasQuest)
            {
                id = (id ?? "").Trim();
                if (!string.Equals(targetId, id, StringComparison.Ordinal))
                {
                    ClearPc();
                    ClearQuest();
                }
                targetId = id;
                Visible = id.Length > 0;
                questLabel.Visible = questFile.Visible = questBrowse.Visible = hasQuest;
                if (!hasQuest) ClearQuest();
            }

            public void ClearPc() { pcFile.Clear(); }
            public void ClearQuest() { questFile.Clear(); }

            public string ReadSignature(bool quest)
            {
                if (string.IsNullOrWhiteSpace(targetId)) return null;
                var path = (quest ? questFile.Text : pcFile.Text).Trim();
                if (path.Length == 0) return null;
                if (!System.Text.RegularExpressions.Regex.IsMatch(targetId,
                    "^wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
                    throw new InvalidOperationException("使用签名文本更新时，请填写完整有效的 World ID。");
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > 1024 * 1024) throw new InvalidOperationException("签名文本超过 1 MB，请确认选的不是模型或世界文件。");
                    using (var reader = new StreamReader(input, new UTF8Encoding(false, true), true))
                    {
                        var value = reader.ReadToEnd().Trim();
                        if (value.Length == 0) throw new InvalidOperationException("签名文本为空，请重新选择或清空路径。");
                        return value;
                    }
                }
            }
        }

        void AddWorldTaskFromForm()
        {
            if (string.IsNullOrEmpty(_vrcw.Text) || !File.Exists(_vrcw.Text))
            {
                SetCustomerStatus("请先选择有效的 VRCW 文件。");
                return;
            }

            var image = ResolveWorldImagePath();
            if (string.IsNullOrEmpty(image) || !File.Exists(image))
            {
                SetCustomerStatus("封面图片不可用，且默认封面生成失败。");
                return;
            }

            ComboBox worldReleaseCb = null;
            foreach (Control c in _vrcwModePanel.Controls)
            {
                if (c.Name == "_worldRelease") { worldReleaseCb = c as ComboBox; break; }
            }
            var release = worldReleaseCb != null && worldReleaseCb.SelectedIndex == 1 ? "public" : "private";
            var name = string.IsNullOrWhiteSpace(_vrcwName.Text) ? Path.GetFileNameWithoutExtension(_vrcw.Text) : _vrcwName.Text.Trim();

            var questPath = _vrcwQuest != null && File.Exists(_vrcwQuest.Text) ? _vrcwQuest.Text : null;
            string pcSignature;
            string questSignature;
            try
            {
                pcSignature = _worldSignatureInputs.ReadSignature(false);
                questSignature = questPath == null ? null : _worldSignatureInputs.ReadSignature(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "签名文本读取失败");
                return;
            }
            AddWorldUploadTask(_vrcw.Text, questPath, image, name, "", release, _worldIdBox.Text.Trim(), pcSignature, questSignature);
        }

        void AddWorldUploadTask(string vrcwPath, string questVrcwPath, string imagePath,
                                string name, string description, string release, string worldId,
                                string pcSignature = null, string questSignature = null)
        {
            var job = new UploadJob();
            job.Id = _nextJobId++;
            job.Name = name;
            job.VrcaPath = vrcwPath;
            job.ImagePath = imagePath;
            job.State = "排队中";
            job.IsWorldUpload = true;
            job.WorldRequest = new VrcaClient.WorldUploadRequest
            {
                VrcwPath = vrcwPath,
                ImagePath = imagePath,
                Name = name,
                Description = description ?? "",
                ReleaseStatus = release,
                Platform = "standalonewindows",
                WorldSignature = pcSignature,
                WorldId = string.IsNullOrEmpty(worldId) ? null : worldId
            };
            if (!string.IsNullOrEmpty(questVrcwPath))
            {
                job.WorldQuestRequest = new VrcaClient.WorldUploadRequest
                {
                    VrcwPath = questVrcwPath,
                    ImagePath = imagePath,
                    Name = name,
                    Description = description ?? "",
                    ReleaseStatus = release,
                    Platform = "android",
                    WorldSignature = questSignature,
                    WorldId = string.IsNullOrEmpty(worldId) ? null : worldId
                };
            }

            lock (_jobsLock) { _jobs.Add(job); }
            OnUi(delegate
            {
                UpdateDetailPanel(job);
                UpdateSummary();
                SetCustomerStatus("已添加世界上传任务：" + job.Name +
                    (job.WorldQuestRequest == null ? "（仅 PC）" : "（PC + Quest）"));
            });
            StartNextJobs();
        }











        void OnLoginClick(object sender, EventArgs e)
        {
            if (_loginBusy) return;

            if (_awaiting2FA)
            {
                var code = _twoFa.Text.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    MessageBox.Show("请输入验证码。", "需要验证码");
                    return;
                }

                SetLoginBusy(true);
                SetLoginStatus("正在校验验证码...", Accent2);
                RunLoginThread(delegate
                {
                    var result = _authClient.LoginVerify2FA(_pendingUser, code, _pending2FAType);
                    OnUi(delegate
                    {
                        SetLoginBusy(false);
                        if (result.Success)
                            MarkLoggedIn(result.DisplayName + " (" + result.UserId + ")");
                        else
                            SetLoginStatus("登录失败: " + SafeError(result.Error), Danger);
                    });
                });
                return;
            }

            var user = _user.Text.Trim();
            var pass = _pass.Text;
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                MessageBox.Show("请输入用户名和密码。", "需要登录");
                return;
            }

            SetLoginBusy(true);
            SetLoginStatus("正在认证...", Accent2);
            RunLoginThread(delegate
            {
                var result = _authClient.LoginStart(user, pass);
                OnUi(delegate
                {
                    SetLoginBusy(false);
                    if (result.Needs2FA)
                    {
                        _awaiting2FA = true;
                        _pendingUser = user;
                        _pending2FAType = result.TwoFactorType ?? "totp";
                        _twoFa.Enabled = true;
                        _twoFa.Focus();
                        _loginBtn.Text = "验证并登录";
                        SetLoginStatus("需要验证码，请输入后点击验证。", Warn);
                    }
                    else if (result.Success)
                    {
                        MarkLoggedIn(result.DisplayName + " (" + result.UserId + ")");
                    }
                    else
                    {
                        SetLoginStatus("登录失败: " + SafeError(result.Error), Danger);
                    }
                });
            });
        }



        void CheckCachedLogin()
        {
            SetLoginBusy(true);
            SetLoginStatus("正在检查本地会话...", TextMuted);
            RunLoginThread(delegate
            {
                string who = null;
                try { who = _authClient.WhoAmI(); } catch { }
                OnUi(delegate
                {
                    SetLoginBusy(false);
                    if (!string.IsNullOrEmpty(who))
                        MarkLoggedIn(who);
                    else
                        MarkLoggedOut("未登录，请输入账号密码。");
                });
            });
        }

        bool TryRestoreCachedLogin() { return DevRequireLogin(); }





        void MarkLoggedIn(string who)
        {
            _loggedIn = true;
            _currentUser = who ?? "";
            _awaiting2FA = false;
            _pendingUser = "";
            _twoFa.Text = "";
            _twoFa.Enabled = false;
            _loginBtn.Text = "登录";
            _loginFormCard.Visible = false;
            _loginSuccessCard.Visible = true;
            _loggedUserLabel.Text = _currentUser;
            _appUserLabel.Text = _currentUser;
            if (_pass != null) _pass.Clear();
            _enterAppBtn.Enabled = true;
            SetLoginStatus("已登录", Accent);
        }

        void MarkLoggedOut(string status)
        {
            _loggedIn = false;
            _currentUser = "";
            _awaiting2FA = false;
            _twoFa.Enabled = false;
            _twoFa.Text = "";
            _loginBtn.Text = "登录";
            _loginFormCard.Visible = true;
            _loginSuccessCard.Visible = false;
            _appUserLabel.Text = "未登录";
            _enterAppBtn.Enabled = false;
            SetLoginStatus(status, TextMuted);
        }







        void AddTaskFromForm()
        {
            if (!TryRestoreCachedLogin())
            {
                ShowLoginView();
                return;
            }
            if (string.IsNullOrEmpty(_vrca.Text) || !File.Exists(_vrca.Text))
            {
                MessageBox.Show("请先选择 VRCA 文件。", "缺少文件");
                return;
            }

            var image = ResolveImagePath();
            var name = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileNameWithoutExtension(_vrca.Text) : _name.Text.Trim();
            AddUploadTask(_vrca.Text, image, name, _desc.Text, SelectedRelease());
        }

        void BatchAddTasks()
        {
            SwitchUploadMode(2);
            BrowseScanFolder();
            if (!string.IsNullOrEmpty(_scanFolder.Text)) StartFolderScan();
        }

        void BrowseScanFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择包含 VRCA / VRCW / 封面图片的文件夹，工具会自动扫描子目录并分类。";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _scanFolder.Text = dlg.SelectedPath;
                SaveLastScanFolder(dlg.SelectedPath);
                _scanStatus.Text = "已选择文件夹，点击“开始扫描”。";
            }
        }

        void StartFolderScan()
        {
            var folder = _scanFolder.Text.Trim();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                BrowseScanFolder();
                folder = _scanFolder.Text.Trim();
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            }
            if (_scanBusy) return;

            var defaultImage = ResolveImagePath();
            SaveLastScanFolder(folder);
            var generation = Interlocked.Increment(ref _scanGeneration);
            _scanBusy = true;
            _scanStartBtn.Enabled = false;
            _scanStatus.Text = "扫描进行中；扫描工作台已展开为全宽视图。";
            SetCustomerStatus("正在后台扫描模型、地图、平台、封面和简介...");
            _scanWorkspace.BeginScan(folder, defaultImage);
            SetScanWorkspaceExpanded(true);

            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception scanError = null;
                List<BatchUploadSelection> scannedRows = null;
                try
                {
                    scannedRows = ScanUploadAssets(folder, defaultImage,
                        delegate(BatchUploadSelection row) { _scanWorkspace.AddScannedFile(row); },
                        delegate { return _scanWorkspace.ScanCancelled || generation != _scanGeneration; });
                    if (!_scanWorkspace.ScanCancelled && generation == _scanGeneration)
                        SaveScanCatalog(folder, scannedRows);
                }
                catch (Exception ex) { scanError = ex; }
                _scanWorkspace.MarkScanComplete(scanError);
                OnUi(delegate
                {
                    if (generation != _scanGeneration) return;
                    _scanBusy = false;
                    _scanStartBtn.Enabled = true;
                    _scanStatus.Text = scanError == null ? "扫描完成，可单独上传或批量加入。" : "扫描完成，但部分文件读取失败。";
                });
            });
        }

        void StopFolderScan()
        {
            Interlocked.Increment(ref _scanGeneration);
            _scanBusy = false;
            if (_scanWorkspace != null) _scanWorkspace.CancelScan();
            if (_scanStartBtn != null) _scanStartBtn.Enabled = true;
            if (_scanStatus != null) _scanStatus.Text = "扫描已停止，已显示的结果仍然保留。";
            SetCustomerStatus("文件夹扫描已停止。");
        }

        void QueueBatchSelections(List<BatchUploadSelection> selectedFiles, string defaultImage)
        {
            if (selectedFiles == null || selectedFiles.Count == 0)
            {
                MessageBox.Show("没有选择需要上传的模型或地图。", "未选择文件");
                return;
            }
            foreach (var item in selectedFiles)
            {
                var image = string.IsNullOrEmpty(item.ImagePath) ? defaultImage : item.ImagePath;
                if (item.AssetType == "地图")
                {
                    AddWorldUploadTask(item.VrcaPath, item.QuestVrcwPath, image,
                        item.DisplayName, _desc.Text, SelectedRelease(), "");
                }
                else
                {
                    AddUploadTask(item.VrcaPath, image, item.DisplayName,
                        _desc.Text, SelectedRelease());
                }
            }
            _scanStatus.Text = "已加入 " + selectedFiles.Count + " 个上传任务。";
            SetCustomerStatus("已扫描并加入 " + selectedFiles.Count + " 个上传任务。");
        }

        List<BatchUploadSelection> ScanUploadAssets(string folder, string defaultImage)
        {
            return ScanUploadAssets(folder, defaultImage, null, null);
        }

        List<BatchUploadSelection> ScanUploadAssets(string folder, string defaultImage,
            Action<BatchUploadSelection> onFound, Func<bool> isCancelled)
        {
            var result = new List<BatchUploadSelection>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return result;

            var vrcaFiles = new List<string>();
            var vrcwFiles = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                if (isCancelled != null && isCancelled()) return result;
                var current = pending.Pop();
                try
                {
                    var files = Directory.GetFiles(current);
                    foreach (var file in files)
                    {
                        var ext = Path.GetExtension(file);
                        if (ext.Equals(".vrca", StringComparison.OrdinalIgnoreCase)) vrcaFiles.Add(file);
                        else if (ext.Equals(".vrcw", StringComparison.OrdinalIgnoreCase)) vrcwFiles.Add(file);
                    }
                }
                catch { }

                try
                {
                    var dirs = Directory.GetDirectories(current);
                    foreach (var dir in dirs)
                    {
                        var name = Path.GetFileName(dir);
                        if (name.Equals(".hotupload-cache", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("prepared-worlds", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("_runtime", StringComparison.OrdinalIgnoreCase))
                            continue;
                        pending.Push(dir);
                    }
                }
                catch { }
            }

            foreach (var file in vrcaFiles)
            {
                if (isCancelled != null && isCancelled()) return result;
                AddScanResult(result, new BatchUploadSelection
                {
                    AssetType = "模型",
                    VrcaPath = file,
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    PlatformLabel = "VRCA",
                    ImagePath = MatchImageForAsset(file, defaultImage),
                    Description = FindAssetDescription(file, "模型文件 · 已自动匹配封面")
                }, onFound);
            }

            vrcwFiles.Sort(StringComparer.OrdinalIgnoreCase);
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = ScanProbeConcurrency() };
            Parallel.ForEach(vrcwFiles, parallelOptions, file =>
            {
                if (isCancelled != null && isCancelled()) return;
                WorldProbeInfo probe = null;
                var cached = TryGetCachedWorldProbe(file, out probe);
                if (!cached)
                {
                    Exception probeError = null;
                    // Large LZMA worlds are probed by helper processes. Under a cold six-way
                    // scan Windows can occasionally reject or starve one helper. Retry only
                    // failed files here so a transient resource race does not become a false
                    // "platform recognition failed" card or split a PC/Quest pair.
                    for (var attempt = 0; attempt < 3 && probe == null; attempt++)
                    {
                        if (isCancelled != null && isCancelled()) return;
                        try
                        {
                            probe = ProbeWorldFile(file);
                            StoreCachedWorldProbe(file, probe);
                        }
                        catch (Exception ex)
                        {
                            probeError = ex;
                            if (attempt < 2) Thread.Sleep(150 * (attempt + 1));
                        }
                    }
                    if (probe == null)
                    {
                        probe = new WorldProbeInfo();
                        var namedPlatform = ClassifyWorldFileName(file);
                        probe.Platform = namedPlatform == "android" ? 13 :
                            (namedPlatform == "standalonewindows" ? 19 : 0);
                        probe.Error = probeError == null ? "未知平台探针错误" : probeError.Message;
                    }
                }
                var row = new BatchUploadSelection
                {
                    AssetType = "地图",
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    WorldId = probe.WorldId ?? "",
                    ImagePath = MatchImageForAsset(file, defaultImage),
                    PairKey = (Path.GetDirectoryName(file) ?? "").ToLowerInvariant() + "|" + WorldPairKey(file),
                    Description = FindAssetDescription(file,
                        probe.Platform == 0 ? "世界文件 · 平台识别失败" :
                        (cached ? "世界文件 · 已复用平台识别缓存" : "世界文件 · 平台识别完成"))
                };
                if (!string.IsNullOrEmpty(row.ImagePath) &&
                    !string.Equals(row.ImagePath, defaultImage, StringComparison.OrdinalIgnoreCase))
                {
                    try { row.CoverKey = Path.GetFullPath(row.ImagePath).ToLowerInvariant(); }
                    catch { row.CoverKey = row.ImagePath.ToLowerInvariant(); }
                }
                if (probe.Platform == 19)
                {
                    row.VrcaPath = file;
                    row.PlatformLabel = "仅 PC";
                }
                else
                {
                    row.QuestVrcwPath = file;
                    row.PlatformLabel = probe.Platform == 13 ? "仅 Quest" : "平台识别失败";
                }
                AddScanResult(result, row, onFound);
            });
            SaveScanProbeCache();

            result.Sort(delegate(BatchUploadSelection a, BatchUploadSelection b)
            {
                var type = StringComparer.OrdinalIgnoreCase.Compare(a.AssetType, b.AssetType);
                return type != 0 ? type : StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            });
            return result;
        }

        int ScanProbeConcurrency()
        {
            // Six concurrent probes was the best stable point on the real 17-world test set:
            // 4 workers ~= 64 s, 6 workers ~= 34 s, while 8 workers regressed to ~= 39 s
            // because the large LZMA bundles started competing for disk and CPU.
            var value = 6;
            int configured;
            if (int.TryParse(Environment.GetEnvironmentVariable("HOTUPLOAD_SCAN_JOBS"), out configured))
                value = configured;
            return Math.Max(1, Math.Min(8, value));
        }

        void AddScanResult(List<BatchUploadSelection> result, BatchUploadSelection row,
            Action<BatchUploadSelection> onFound)
        {
            BatchUploadSelection merge = null;
            lock (result)
            {
                if (row.AssetType == "地图")
                {
                    merge = result.Find(delegate(BatchUploadSelection current)
                    {
                        if (current.AssetType != "地图") return false;
                        var sameIdentity = (!string.IsNullOrEmpty(current.WorldId) && !string.IsNullOrEmpty(row.WorldId) &&
                                string.Equals(current.WorldId, row.WorldId, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(current.PairKey) && !string.IsNullOrEmpty(row.PairKey) &&
                                string.Equals(current.PairKey, row.PairKey, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(current.CoverKey) && !string.IsNullOrEmpty(row.CoverKey) &&
                                string.Equals(current.CoverKey, row.CoverKey, StringComparison.OrdinalIgnoreCase));
                        if (!sameIdentity) return false;
                        return (!string.IsNullOrEmpty(row.VrcaPath) && string.IsNullOrEmpty(current.VrcaPath)) ||
                            (!string.IsNullOrEmpty(row.QuestVrcwPath) && string.IsNullOrEmpty(current.QuestVrcwPath));
                    });
                }
                if (merge == null) result.Add(row);
                else
                {
                    if (!string.IsNullOrEmpty(row.VrcaPath))
                    {
                        merge.VrcaPath = row.VrcaPath;
                        merge.DisplayName = row.DisplayName;
                        merge.ImagePath = row.ImagePath;
                    }
                    if (!string.IsNullOrEmpty(row.QuestVrcwPath)) merge.QuestVrcwPath = row.QuestVrcwPath;
                    if (string.IsNullOrEmpty(merge.WorldId)) merge.WorldId = row.WorldId;
                    if (string.IsNullOrEmpty(merge.PairKey)) merge.PairKey = row.PairKey;
                    if (string.IsNullOrEmpty(merge.CoverKey)) merge.CoverKey = row.CoverKey;
                    if (string.IsNullOrEmpty(merge.Description)) merge.Description = row.Description;
                    merge.PlatformLabel = !string.IsNullOrEmpty(merge.VrcaPath) && !string.IsNullOrEmpty(merge.QuestVrcwPath)
                        ? "PC + Quest" : (!string.IsNullOrEmpty(merge.VrcaPath) ? "仅 PC" : row.PlatformLabel);
                    if (merge.PlatformLabel == "PC + Quest")
                        merge.DisplayName = ChoosePairedWorldName(merge.VrcaPath, merge.QuestVrcwPath, merge.DisplayName);
                }
            }
            if (onFound != null) onFound(row);
        }

        static string ChoosePairedWorldName(string pcPath, string questPath, string fallback)
        {
            try
            {
                var primary = !string.IsNullOrEmpty(pcPath) ? pcPath : questPath;
                var directory = Path.GetDirectoryName(primary);
                var folderName = Path.GetFileName(directory ?? "");
                var generic = new[] { "downloads", "documents", "desktop", "maps", "worlds", "地图", "世界", "新建文件夹" };
                if (!string.IsNullOrWhiteSpace(folderName) &&
                    Array.IndexOf(generic, folderName.Trim().ToLowerInvariant()) < 0)
                    return folderName.Trim();
            }
            catch { }
            return fallback;
        }

        string MatchImageForAsset(string assetPath, string defaultImage)
        {
            var dir = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return defaultImage;
            var images = new List<string>();
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                        images.Add(file);
                }
            }
            catch { }
            if (images.Count == 0) return defaultImage;
            if (images.Count == 1) return images[0];

            var assetName = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
            string best = null;
            var bestScore = -1;
            foreach (var image in images)
            {
                var imageName = Path.GetFileNameWithoutExtension(image).ToLowerInvariant();
                var score = 0;
                if (assetName == imageName) score = 10000;
                else
                {
                    if (assetName.Contains(imageName) || imageName.Contains(assetName))
                        score += 1000 + Math.Min(assetName.Length, imageName.Length);
                    var common = 0;
                    while (common < assetName.Length && common < imageName.Length &&
                           assetName[common] == imageName[common]) common++;
                    score += common * 10;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = image;
                }
            }
            return bestScore >= 20 ? best : defaultImage;
        }

        string FindAssetDescription(string assetPath, string fallback)
        {
            try
            {
                var dir = Path.GetDirectoryName(assetPath) ?? "";
                var stem = Path.GetFileNameWithoutExtension(assetPath);
                var candidates = new[]
                {
                    Path.Combine(dir, stem + ".txt"),
                    Path.Combine(dir, "简介.txt"),
                    Path.Combine(dir, "description.txt")
                };
                foreach (var candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;
                    var text = File.ReadAllText(candidate, Encoding.UTF8).Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
                    return text.Length > 180 ? text.Substring(0, 180) + "…" : text;
                }
            }
            catch { }
            return fallback;
        }

        string ScanStateDirectory()
        {
            var overridePath = Environment.GetEnvironmentVariable("HOTUPLOAD_SCAN_STATE_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath.Trim());
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            return Path.Combine(local, "Mianchuan.VrcHotUpload");
        }

        string ScanProbeCachePath()
        {
            return Path.Combine(ScanStateDirectory(), "scan-probe-cache.json");
        }

        void RestoreLastScanFolder()
        {
            try
            {
                var path = Path.Combine(ScanStateDirectory(), "last-scan-folder.txt");
                if (!File.Exists(path)) return;
                var folder = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (_scanFolder != null && Directory.Exists(folder)) _scanFolder.Text = folder;
            }
            catch { }
        }

        void SaveLastScanFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                var dir = ScanStateDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "last-scan-folder.txt"), folder, new UTF8Encoding(false));
            }
            catch { }
        }

        void EnsureScanProbeCacheLoaded()
        {
            lock (_scanProbeCacheLock)
            {
                if (_scanProbeCacheLoaded) return;
                _scanProbeCacheLoaded = true;
                _scanProbeCache = new Dictionary<string, ScanProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var path = ScanProbeCachePath();
                    if (!File.Exists(path)) return;
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                    var entries = serializer.Deserialize<List<ScanProbeCacheEntry>>(json);
                    if (entries == null) return;
                    foreach (var entry in entries)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Path)) continue;
                        _scanProbeCache[entry.Path] = entry;
                    }
                }
                catch
                {
                    _scanProbeCache.Clear();
                }
            }
        }

        bool TryGetCachedWorldProbe(string path, out WorldProbeInfo probe)
        {
            probe = null;
            try
            {
                EnsureScanProbeCacheLoaded();
                var full = Path.GetFullPath(path);
                var info = new FileInfo(full);
                ScanProbeCacheEntry entry;
                lock (_scanProbeCacheLock)
                {
                    if (!_scanProbeCache.TryGetValue(full, out entry)) return false;
                    if (entry.Length != info.Length || entry.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks)
                    {
                        _scanProbeCache.Remove(full);
                        _scanProbeCacheDirty = true;
                        return false;
                    }
                }
                probe = new WorldProbeInfo
                {
                    Platform = entry.Platform,
                    WorldId = entry.WorldId,
                    UnityVersion = entry.UnityVersion,
                    SceneName = entry.SceneName
                };
                return probe.Platform == 13 || probe.Platform == 19;
            }
            catch { return false; }
        }

        void StoreCachedWorldProbe(string path, WorldProbeInfo probe)
        {
            if (probe == null || (probe.Platform != 13 && probe.Platform != 19)) return;
            try
            {
                EnsureScanProbeCacheLoaded();
                var full = Path.GetFullPath(path);
                var info = new FileInfo(full);
                var entry = new ScanProbeCacheEntry
                {
                    Path = full,
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Platform = probe.Platform,
                    WorldId = probe.WorldId,
                    UnityVersion = probe.UnityVersion,
                    SceneName = probe.SceneName
                };
                lock (_scanProbeCacheLock)
                {
                    _scanProbeCache[full] = entry;
                    _scanProbeCacheDirty = true;
                }
            }
            catch { }
        }

        void SaveScanProbeCache()
        {
            List<ScanProbeCacheEntry> entries;
            lock (_scanProbeCacheLock)
            {
                if (!_scanProbeCacheLoaded || !_scanProbeCacheDirty || _scanProbeCache == null) return;
                entries = new List<ScanProbeCacheEntry>(_scanProbeCache.Values);
                _scanProbeCacheDirty = false;
            }
            try
            {
                var dir = ScanStateDirectory();
                Directory.CreateDirectory(dir);
                var path = ScanProbeCachePath();
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                File.WriteAllText(temp, serializer.Serialize(entries), new UTF8Encoding(false));
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch
            {
                lock (_scanProbeCacheLock) _scanProbeCacheDirty = true;
            }
        }

        string ScanCatalogPath()
        {
            return Path.Combine(ScanStateDirectory(), "scan-catalog.json");
        }

        void SaveScanCatalog(string rootFolder, List<BatchUploadSelection> rows)
        {
            if (string.IsNullOrEmpty(rootFolder) || rows == null) return;
            try
            {
                var catalog = new ScanCatalogFile
                {
                    RootFolder = rootFolder,
                    Items = new List<ScanCatalogEntry>()
                };
                lock (rows)
                {
                    foreach (var row in rows)
                    {
                        var entry = new ScanCatalogEntry
                        {
                            AssetType = row.AssetType,
                            VrcaPath = row.VrcaPath,
                            QuestVrcwPath = row.QuestVrcwPath,
                            ImagePath = row.ImagePath,
                            DisplayName = row.DisplayName,
                            PlatformLabel = row.PlatformLabel,
                            WorldId = row.WorldId,
                            PairKey = row.PairKey,
                            CoverKey = row.CoverKey,
                            Description = row.Description
                        };
                        ReadFileStamp(entry.VrcaPath, out entry.PcLength, out entry.PcWriteUtcTicks);
                        ReadFileStamp(entry.QuestVrcwPath, out entry.QuestLength, out entry.QuestWriteUtcTicks);
                        catalog.Items.Add(entry);
                    }
                }
                var dir = ScanStateDirectory();
                Directory.CreateDirectory(dir);
                var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                var path = ScanCatalogPath();
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, serializer.Serialize(catalog), new UTF8Encoding(false));
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch { }
        }

        bool TryLoadScanCatalog(out string rootFolder, out List<BatchUploadSelection> rows)
        {
            rootFolder = "";
            rows = new List<BatchUploadSelection>();
            try
            {
                var path = ScanCatalogPath();
                if (!File.Exists(path)) return false;
                var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                var catalog = serializer.Deserialize<ScanCatalogFile>(File.ReadAllText(path, Encoding.UTF8));
                if (catalog == null || string.IsNullOrEmpty(catalog.RootFolder) ||
                    !Directory.Exists(catalog.RootFolder) || catalog.Items == null) return false;
                rootFolder = catalog.RootFolder;
                foreach (var entry in catalog.Items)
                {
                    if (entry == null) continue;
                    if (!FileStampMatches(entry.VrcaPath, entry.PcLength, entry.PcWriteUtcTicks)) continue;
                    if (!FileStampMatches(entry.QuestVrcwPath, entry.QuestLength, entry.QuestWriteUtcTicks)) continue;
                    rows.Add(new BatchUploadSelection
                    {
                        AssetType = entry.AssetType,
                        VrcaPath = entry.VrcaPath,
                        QuestVrcwPath = entry.QuestVrcwPath,
                        ImagePath = File.Exists(entry.ImagePath) ? entry.ImagePath : ResolveImagePath(),
                        DisplayName = entry.DisplayName,
                        PlatformLabel = entry.PlatformLabel,
                        WorldId = entry.WorldId,
                        PairKey = entry.PairKey,
                        CoverKey = File.Exists(entry.ImagePath) ? entry.CoverKey : "",
                        Description = entry.Description
                    });
                }
                return rows.Count > 0;
            }
            catch { return false; }
        }

        static void ReadFileStamp(string path, out long length, out long ticks)
        {
            length = 0;
            ticks = 0;
            if (string.IsNullOrEmpty(path)) return;
            var info = new FileInfo(path);
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }

        static bool FileStampMatches(string path, long length, long ticks)
        {
            if (string.IsNullOrEmpty(path)) return length == 0 && ticks == 0;
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length == length && info.LastWriteTimeUtc.Ticks == ticks;
            }
            catch { return false; }
        }

        WorldProbeInfo ProbeWorldFile(string path)
        {
            var exe = Path.Combine(_baseDir, "tools", "HotswapTool.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("缺少世界平台探针。", exe);
            var prefix = Path.Combine(Path.GetTempPath(), "vrcw_probe_" + Guid.NewGuid().ToString("N"));
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = QuoteProcessArg(path) + " " + QuoteProcessArg(prefix) +
                                " wrld_11111111-2222-4333-8444-555555555555 --probe-world",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = psi;
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(10 * 60 * 1000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("读取世界平台超时。");
                    }
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("读取世界平台失败：" + stderr.Trim());
                    foreach (var line in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.StartsWith("WORLD_PROBE|", StringComparison.Ordinal)) continue;
                        var result = new WorldProbeInfo();
                        foreach (var part in line.Split('|'))
                        {
                            var eq = part.IndexOf('=');
                            if (eq <= 0) continue;
                            var key = part.Substring(0, eq);
                            var value = part.Substring(eq + 1);
                            int platform;
                            if (key == "platform" && int.TryParse(value, out platform)) result.Platform = platform;
                            else if (key == "worldId") result.WorldId = value;
                            else if (key == "unity") result.UnityVersion = value;
                            else if (key == "scene") result.SceneName = value;
                        }
                        return result;
                    }
                    throw new InvalidOperationException("世界平台探针没有返回结果。");
                }
            }
            finally
            {
                TryDeleteLocal(prefix);
                TryDeleteLocal(prefix + ".source-unpacked");
            }
        }

        static void TryDeleteLocal(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        static string QuoteProcessArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }



        void AddUploadTask(string vrcaPath, string imagePath, string name, string description, string release)
        {
            var job = new UploadJob();
            job.Id = _nextJobId++;
            job.Name = name;
            job.VrcaPath = vrcaPath;
            job.ImagePath = imagePath;
            job.State = "排队中";
            job.Progress = 0;
            job.LocalProgress = 0;
            job.UploadProgress = 0;
            job.SecurityProgress = 0;
            job.Request = new VrcaClient.UploadRequest
            {
                VrcaPath = vrcaPath,
                ImagePath = imagePath,
                Name = name,
                Description = description ?? "",
                ReleaseStatus = release
            };

            lock (_jobsLock) { _jobs.Add(job); }
            OnUi(delegate
            {
                UpdateDetailPanel(job);
                UpdateSummary();
                SetCustomerStatus("已添加任务并开始上传：" + job.Name);
            });
            StartNextJobs();
        }

        void StartNextJobs()
        {
            if (!TryRestoreCachedLogin()) return;

            var toStart = new List<UploadJob>();
            var maxJobs = GetMaxUploadJobs();
            var runningAfterStart = 0;
            lock (_jobsLock)
            {
                while (_runningJobs < maxJobs)
                {
                    UploadJob next = null;
                    foreach (var job in _jobs)
                    {
                        if (job.State == "排队中")
                        {
                            next = job;
                            break;
                        }
                    }
                    if (next == null) break;

                    next.State = "运行中";
                    next.Progress = Math.Max(next.Progress, 1);
                    next.StartedAt = DateTime.Now;
                    _runningJobs++;
                    toStart.Add(next);
                }
                runningAfterStart = _runningJobs;
            }

            foreach (var job in toStart)
            {
                UpdateJobRow(job);
                var thread = new Thread(new ThreadStart(delegate { RunUploadJob(job); }));
                thread.IsBackground = true;
                job.WorkerThread = thread;
                thread.Start();
            }
            if (toStart.Count > 0)
                SetCustomerStatus("正在同时处理 " + runningAfterStart + " 个上传任务。");
            UpdateSummary();
        }

        int GetMaxUploadJobs()
        {
            var raw = Environment.GetEnvironmentVariable(MaxUploadJobsEnv);
            int parsed;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out parsed))
            {
                if (parsed < 1) parsed = 1;
                if (parsed > 24) parsed = 24;
                return parsed;
            }

            // The queue remains unlimited, but starting too many full bundle
            // jobs at once causes disk thrashing and multiplies multipart PUTs.
            return Math.Max(3, Math.Min(6, Math.Max(1, Environment.ProcessorCount / 2)));
        }

        void RunUploadJob(UploadJob job)
        {
            var client = new VrcaClient(_baseDir, delegate(string message) { HandleTaskLog(job, message); });
            job.Client = client;
            try
            {
                client.StartNewLog("upload gui task " + job.Id + " - " + job.Name);
                job.LogPath = client.LogFilePath;
                SetJobProgress(job, 3, 0, 0, "准备中");

                if (job.IsWorldUpload)
                {
                    var worldResult = client.UploadWorldDetailed(job.WorldRequest);
                    if (job.CancelRequested)
                        throw new OperationCanceledException("上传任务已停止。");
                    job.AvatarId = worldResult.WorldId;
                    job.PcPlatformCompleted = true;

                    if (job.WorldQuestRequest != null)
                    {
                        job.WorldQuestRequest.WorldId = worldResult.WorldId;
                        job.WorldQuestRequest.ExistingImageUrl = worldResult.ImageUrl;
                        BeginWorldQuestPhase(job);
                        SetCustomerStatus("PC 世界已完成，正在追加 Quest 平台：" + job.Name);
                        try
                        {
                            var questResult = client.UploadWorldDetailed(job.WorldQuestRequest);
                            if (job.CancelRequested)
                                throw new OperationCanceledException("上传任务已停止。");
                            if (!string.Equals(questResult.WorldId, worldResult.WorldId, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Quest 平台返回了不同的 World ID。");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            job.State = "部分成功";
                            job.Stage = "PC 成功，Quest 失败";
                            job.Progress = Math.Max(job.Progress, 52);
                            job.Error = "PC 已上传成功（" + worldResult.WorldId + "），Quest 上传失败：" + ex.Message;
                            WriteWorkspaceFailure(job, ex);
                            job.FinishedAt = DateTime.Now;
                            SetCustomerStatus("PC 已成功，但 Quest 未完成：" + job.Name + "。可使用同一 World ID 重试 Quest。");
                            return;
                        }
                    }

                    job.State = "成功";
                    job.Progress = 100;
                    job.LocalProgress = 100;
                    job.UploadProgress = 100;
                    job.SecurityProgress = 100;
                    job.FinishedAt = DateTime.Now;
                    SetCustomerStatus("世界上传成功：" + job.Name +
                        (job.WorldQuestRequest == null ? "（PC）" : "（PC + Quest）") +
                        "。World ID: " + worldResult.WorldId);
                }
                else
                {
                    var result = client.UploadDetailed(job.Request);
                    if (job.CancelRequested)
                        throw new OperationCanceledException("上传任务已停止。");
                    job.AvatarId = result.AvatarId;
                    job.State = "成功";
                    job.Progress = 100;
                    job.LocalProgress = 100;
                    job.UploadProgress = 100;
                    job.SecurityProgress = 100;
                    job.FinishedAt = DateTime.Now;
                    SetCustomerStatus("上传成功：" + job.Name + "。Avatar ID: " + result.AvatarId);
                }
            }
            catch (OperationCanceledException)
            {
                job.State = "已停止";
                job.Stage = "已停止";
                job.Error = "";
                job.FinishedAt = DateTime.Now;
                WriteWorkspaceFailure(job, new OperationCanceledException("用户停止了任务。"));
                SetCustomerStatus("已停止上传任务：" + job.Name);
            }
            catch (VrcaClient.NotLoggedInException ex)
            {
                WriteWorkspaceFailure(job, ex);
                if (IsSessionStillValid(client))
                {
                    job.State = "失败";
                    job.Progress = 0;
                    job.Error = "上传接口返回登录异常，但当前会话仍有效，请重试该任务。";
                    job.FinishedAt = DateTime.Now;
                    SetCustomerStatus("上传未完成，请重试该任务。");
                    return;
                }

                job.State = "需要登录";
                job.Progress = 0;
                job.Error = "登录已过期，请重新登录。";
                SetCustomerStatus("登录已过期，请重新登录后继续上传。");
                OnUi(delegate
                {
                    MarkLoggedOut("登录已过期，请重新登录。");
                    ShowLoginView();
                });
            }
            catch (VrcaClient.ServerSecurityException ex)
            {
                job.State = "安全失败";
                job.Stage = "安全检测失败";
                job.Progress = Math.Min(job.Progress, 95);
                job.SecurityProgress = Math.Min(Math.Max(job.SecurityProgress, 95), 99);
                job.Error = ex.Message;
                WriteWorkspaceFailure(job, ex);
                job.FinishedAt = DateTime.Now;
                SetCustomerStatus("安全检测失败：" + job.Name + "。请打开任务详情查看完整日志。");
            }
            catch (Exception ex)
            {
                job.State = "失败";
                job.Stage = "上传失败";
                job.Progress = Math.Min(job.Progress, 90);
                job.Error = ex.Message;
                WriteWorkspaceFailure(job, ex);
                job.FinishedAt = DateTime.Now;
                SetCustomerStatus("上传失败：" + job.Name + "。请打开任务详情查看完整日志。");
            }
            finally
            {
                job.Client = null;
                job.WorkerThread = null;
                lock (_jobsLock)
                {
                    if (_runningJobs > 0) _runningJobs--;
                }
                UpdateJobRow(job);
                StartNextJobs();
            }
        }

        bool IsSessionStillValid(VrcaClient client)
        {
            try
            {
                return client != null && !string.IsNullOrEmpty(client.WhoAmI());
            }
            catch
            {
                return false;
            }
        }

        void HandleTaskLog(UploadJob job, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (message.IndexOf("[UPLOAD] 检查登录", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 4, 0, 0, "检查登录");
            else if (message.IndexOf("[PREFLIGHT]", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, Math.Max(job.LocalProgress, 18), 0, 0, "预检查");
            else if (message.IndexOf("创建 avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("创建上传任务", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("已创建上传任务", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 28, 0, 0, "创建记录");
            else if (message.IndexOf("改写 bundle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("本地处理资源", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("读取资源信息", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 40, 0, 0, "本地处理");
            else if (message.IndexOf("[hotswap] still working", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, EstimateHotswapProgress(job, message), 0, 0, "本地处理");
            else if (message.IndexOf("[hotswap]", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, Math.Max(job.LocalProgress, 46), 0, 0, "本地处理");
            else if (message.IndexOf("改写完成", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("本地处理完成", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, 0, 0, "本地完成");
            else if (message.IndexOf("上传 avatar bundle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("上传模型文件", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, 8, 0, "上传模型");
            else if (message.IndexOf("[FILE] 上传文件数据", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, job.UploadProgress >= 80 ? 84 : 14, 0, "上传文件");
            else if (message.IndexOf("[FILE] 上传 file part", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("[FILE] 上传 signature part", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("[FILE] 上传数据 part", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("[FILE] 上传校验数据 part", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, EstimateUploadPartProgress(job, message), 0, "上传文件");
            else if (message.IndexOf("[FILE] 上传签名数据", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("[FILE] 上传校验数据", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, job.UploadProgress >= 80 ? 92 : 72, 0, "上传签名");
            else if (message.IndexOf("[FILE] 等待处理", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("[FILE] 等待服务器处理", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, job.UploadProgress >= 82 ? 96 : 80, 0, "上传文件");
            else if (message.IndexOf("上传封面", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, 82, 0, "上传封面");
            else if (message.IndexOf("更新 avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("更新上传信息", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, 100, 0, "更新资料");
            else if (message.IndexOf("[SECURITY]", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, 100, EstimateSecurityProgress(job, message), "安全检测");
            else if (message.IndexOf("[DONE]", StringComparison.OrdinalIgnoreCase) >= 0)
                SetJobProgress(job, 100, 100, 100,
                    IsDualPlatformWorld(job)
                        ? (job.QuestPhaseActive ? "Quest 完成" : "PC 完成")
                        : "完成");
        }

        static bool IsDualPlatformWorld(UploadJob job)
        {
            return job != null && job.IsWorldUpload && job.WorldQuestRequest != null;
        }

        void BeginWorldQuestPhase(UploadJob job)
        {
            // PC and Quest each own an independent three-stage progress cycle.
            // Reset only the per-platform bars; the overall bar continues from
            // 50%, so the completed PC phase can never masquerade as full success.
            job.PcPlatformCompleted = true;
            job.QuestPhaseActive = true;
            job.LocalProgress = 0;
            job.UploadProgress = 0;
            job.SecurityProgress = 0;
            job.Progress = 50;
            job.Stage = "PC 已完成，Quest 准备中";
            SetJobProgress(job, 3, 0, 0, "Quest 准备中");
        }

        void SetJobProgress(UploadJob job, int localProgress, int uploadProgress, int securityProgress, string state)
        {
            if (localProgress > job.LocalProgress) job.LocalProgress = ClampPercent(localProgress);
            if (uploadProgress > job.UploadProgress) job.UploadProgress = ClampPercent(uploadProgress);
            if (securityProgress > job.SecurityProgress) job.SecurityProgress = ClampPercent(securityProgress);
            var platformProgress = ClampPercent((int)Math.Round(
                job.LocalProgress * 0.45 + job.UploadProgress * 0.40 +
                job.SecurityProgress * 0.15));
            if (IsDualPlatformWorld(job))
            {
                if (job.QuestPhaseActive)
                    job.Progress = ClampPercent(50 + (int)Math.Round(platformProgress * 0.50));
                else
                    // UploadWorldDetailed emits [DONE] just before returning.
                    // Hold at 49 until the PC result (World ID/image) is safely
                    // available, then BeginWorldQuestPhase advances to 50.
                    job.Progress = job.PcPlatformCompleted
                        ? 50
                        : Math.Min(49, (int)Math.Round(platformProgress * 0.50));
            }
            else
            {
                job.Progress = platformProgress;
            }
            if (!string.IsNullOrEmpty(state) && job.State == "运行中") job.Stage = state;
            SetCustomerStatus("正在处理：" + job.Name + "    " + GetCurrentStepName(job) + " " + GetCurrentStepPercent(job) + "%");
            UpdateJobRow(job);
        }

        int EstimateHotswapProgress(UploadJob job, string message)
        {
            var elapsed = ExtractDuration(message, "elapsed=");
            if (elapsed.TotalSeconds > 0)
            {
                var value = 45 + (int)Math.Min(50, Math.Floor(elapsed.TotalSeconds / 6.0));
                return ClampPercent(Math.Max(job.LocalProgress + 1, value));
            }
            return ClampPercent(Math.Max(job.LocalProgress + 2, 52));
        }

        int EstimateUploadPartProgress(UploadJob job, string message)
        {
            int part;
            int total;
            var hasPart = TryParseNumberPairAfter(message, " part ", out part, out total);
            var isSignature = message.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("校验数据", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasPart)
                return ClampPercent(Math.Max(job.UploadProgress + 3, 40));

            if (job.UploadProgress >= 80)
            {
                var baseValue = isSignature ? 92 : 84;
                var span = isSignature ? 5 : 8;
                return ClampPercent(baseValue + (int)Math.Round((double)part * span / Math.Max(1, total)));
            }

            if (isSignature)
                return ClampPercent(72 + (int)Math.Round((double)part * 8 / Math.Max(1, total)));

            return ClampPercent(14 + (int)Math.Round((double)part * 56 / Math.Max(1, total)));
        }

        int EstimateSecurityProgress(UploadJob job, string message)
        {
            if (message.IndexOf("status=200", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("analysis=available", StringComparison.OrdinalIgnoreCase) >= 0)
                return 100;

            int attempt;
            int total;
            if (TryParseNumberPairAfter(message, "check ", out attempt, out total))
                return ClampPercent(10 + (int)Math.Round((double)attempt * 85 / Math.Max(1, total)));

            if (message.IndexOf("等待", StringComparison.OrdinalIgnoreCase) >= 0)
                return Math.Max(job.SecurityProgress, 8);

            return Math.Max(job.SecurityProgress, 35);
        }

        TimeSpan ExtractDuration(string text, string key)
        {
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return TimeSpan.Zero;
            index += key.Length;
            var end = index;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == ':'))
                end++;

            TimeSpan value;
            if (TimeSpan.TryParse(text.Substring(index, end - index), out value))
                return value;
            return TimeSpan.Zero;
        }

        bool TryParseNumberPairAfter(string text, string marker, out int first, out int second)
        {
            first = 0;
            second = 0;
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            index += marker.Length;

            var slash = text.IndexOf('/', index);
            if (slash < 0) return false;

            var end = slash + 1;
            while (end < text.Length && char.IsDigit(text[end]))
                end++;

            return int.TryParse(text.Substring(index, slash - index).Trim(), out first) &&
                   int.TryParse(text.Substring(slash + 1, end - slash - 1).Trim(), out second) &&
                   first > 0 && second > 0;
        }

        void UpdateJobRow(UploadJob job)
        {
            OnUi(delegate
            {
                UpdateDetailPanel(job);
            });
        }

        void PreflightCurrentFile()
        {
            if (_preflightBusy) return;
            if (string.IsNullOrEmpty(_vrca.Text) || !File.Exists(_vrca.Text))
            {
                MessageBox.Show("请先选择 VRCA 文件。", "缺少文件");
                return;
            }

            _preflightBusy = true;
            _preflightBtn.Enabled = false;
            AppendActivity("[PREFLIGHT] 开始预检查: " + Path.GetFileName(_vrca.Text));
            var path = _vrca.Text;
            var client = new VrcaClient(_baseDir, delegate(string message)
            {
                if (ShouldShowActivity(message)) AppendActivity(message);
            });

            var thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    client.StartNewLog("preflight gui");
                    var result = client.Preflight(path);
                    OnUi(delegate
                    {
                        MessageBox.Show(FormatPreflightSummary(result), result.ErrorCount > 0 ? "预检查发现问题" : "预检查完成");
                    });
                }
                catch (Exception ex)
                {
                    OnUi(delegate { MessageBox.Show("预检查失败:\n" + ex.Message, "失败"); });
                }
                finally
                {
                    OnUi(delegate
                    {
                        _preflightBusy = false;
                        _preflightBtn.Enabled = true;
                    });
                }
            }));
            thread.IsBackground = true;
            thread.Start();
        }

        void OpenSelectedLog()
        {
            var job = PickDetailJob();
            if (job == null || string.IsNullOrEmpty(job.LogPath) || !File.Exists(job.LogPath))
            {
                MessageBox.Show("当前任务还没有可打开的日志。", "没有日志");
                return;
            }
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + job.LogPath + "\""); }
            catch (Exception ex) { MessageBox.Show("打开日志失败:\n" + ex.Message, "失败"); }
        }

        void ClearFinishedJobs()
        {
            lock (_jobsLock)
            {
                for (var i = _jobs.Count - 1; i >= 0; i--)
                {
                    var s = _jobs[i].State;
                    if (s == "成功" || s == "失败" || s == "安全失败" || s == "需要登录")
                        _jobs.RemoveAt(i);
                }
            }
            OnUi(delegate { UpdateSummary(); });
        }

        string ResolveImagePath()
        {
            if (!string.IsNullOrEmpty(_image.Text) && File.Exists(_image.Text))
                return _image.Text;
            var path = EnsureDefaultCoverFile();
            _image.Text = path;
            AppendActivity("[GUI] 未选择封面，已使用默认封面。");
            return path;
        }

        string ResolveWorldImagePath()
        {
            if (_vrcwImage != null && !string.IsNullOrEmpty(_vrcwImage.Text) && File.Exists(_vrcwImage.Text))
                return _vrcwImage.Text;
            var path = EnsureDefaultCoverFile();
            if (_vrcwImage != null) _vrcwImage.Text = path;
            AppendActivity("[GUI] 世界上传未选择封面，已使用默认封面。");
            return path;
        }

        string EnsureDefaultCoverFile()
        {
            var dir = Path.Combine(_baseDir, "_runtime", "assets");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mianchuan-cover-v2.png");
            if (File.Exists(path)) return path;

            SaveWorkspaceCover(path);
            return path;
        }

        static void SaveWorkspaceCover(string path)
        {
            using (var bmp = new Bitmap(512, 384))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 512, 384), Color.FromArgb(24, 48, 57), Color.FromArgb(88, 132, 128), 90f))
                    g.FillRectangle(brush, 0, 0, 512, 384);
                using (var moon = new SolidBrush(Color.FromArgb(240, 225, 184)))
                    g.FillEllipse(moon, 340, 56, 74, 74);
                using (var far = new SolidBrush(Color.FromArgb(66, 105, 110)))
                    g.FillPolygon(far, new[] { new Point(0, 234), new Point(106, 123), new Point(212, 232), new Point(307, 156), new Point(512, 261), new Point(512, 384), new Point(0, 384) });
                using (var near = new SolidBrush(Color.FromArgb(30, 74, 79)))
                    g.FillPolygon(near, new[] { new Point(0, 302), new Point(164, 202), new Point(312, 302), new Point(424, 224), new Point(512, 286), new Point(512, 384), new Point(0, 384) });
                using (var river = new GraphicsPath())
                using (var water = new SolidBrush(Color.FromArgb(145, 184, 167)))
                {
                    river.AddBezier(286, 258, 196, 291, 398, 296, 248, 346);
                    river.AddBezier(248, 346, 204, 361, 172, 373, 154, 384);
                    river.AddLine(154, 384, 342, 384);
                    river.AddBezier(342, 384, 311, 366, 299, 358, 308, 349);
                    river.AddBezier(308, 349, 411, 301, 241, 283, 294, 258);
                    river.CloseFigure();
                    g.FillPath(water, river);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        void PickAndPairWorldFiles(bool preferQuestSlot)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "VRCW 文件 (*.vrcw)|*.vrcw|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                dlg.Title = "选择一个或同时选择 PC、Quest 世界文件";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string pc = null;
                string quest = null;
                foreach (var file in dlg.FileNames)
                {
                    var platform = ClassifyWorldFileName(file);
                    if (platform == "android" && quest == null) quest = file;
                    else if (platform == "standalonewindows" && pc == null) pc = file;
                }

                if (dlg.FileNames.Length == 1)
                {
                    var selected = dlg.FileNames[0];
                    var classified = ClassifyWorldFileName(selected);
                    if (classified == "android" || preferQuestSlot) quest = selected;
                    else pc = selected;

                    var partner = FindWorldPlatformPartner(selected,
                        quest == selected ? "standalonewindows" : "android");
                    if (!string.IsNullOrEmpty(partner))
                    {
                        if (quest == selected) pc = partner;
                        else quest = partner;
                    }
                }
                else if (dlg.FileNames.Length == 2 && pc == null && quest == null)
                {
                    // Filenames without platform tokens: the explicitly opened
                    // slot decides the first file; the second becomes its pair.
                    if (preferQuestSlot)
                    {
                        quest = dlg.FileNames[0];
                        pc = dlg.FileNames[1];
                    }
                    else
                    {
                        pc = dlg.FileNames[0];
                        quest = dlg.FileNames[1];
                    }
                }

                if (!string.IsNullOrEmpty(pc)) _vrcw.Text = pc;
                if (!string.IsNullOrEmpty(quest)) _vrcwQuest.Text = quest;
                if (!string.IsNullOrEmpty(_vrcw.Text) && string.IsNullOrWhiteSpace(_vrcwName.Text))
                    _vrcwName.Text = Path.GetFileNameWithoutExtension(_vrcw.Text);
                UpdateWorldPairStatus();
            }
        }

        static string ClassifyWorldFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                @"(^|[^a-z0-9])(quest|android|mobile)([^a-z0-9]|$)"))
                return "android";
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                @"(^|[^a-z0-9])(pc|windows|standalonewindows|standalonewindows64)([^a-z0-9]|$)"))
                return "standalonewindows";
            return "";
        }

        static string WorldPairKey(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
            name = System.Text.RegularExpressions.Regex.Replace(name,
                @"quest|android|mobile|standalonewindows64|standalonewindows|windows|pc", "");
            return System.Text.RegularExpressions.Regex.Replace(name, @"[^a-z0-9\p{L}]", "");
        }

        static string FindWorldPlatformPartner(string selected, string wantedPlatform)
        {
            var directory = Path.GetDirectoryName(selected);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
            var key = WorldPairKey(selected);
            foreach (var candidate in Directory.GetFiles(directory, "*.vrcw", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(candidate, selected, StringComparison.OrdinalIgnoreCase)) continue;
                if (ClassifyWorldFileName(candidate) != wantedPlatform) continue;
                if (WorldPairKey(candidate) == key) return candidate;
            }
            return null;
        }

        void UpdateWorldPairStatus()
        {
            if (_worldPairStatus == null) return;
            var hasPc = _vrcw != null && File.Exists(_vrcw.Text);
            var hasQuest = _vrcwQuest != null && File.Exists(_vrcwQuest.Text);
            if (!hasPc)
            {
                _worldPairStatus.Text = "缺少 PC 世界文件，无法创建任务";
                _worldPairStatus.ForeColor = Danger;
            }
            else if (!hasQuest)
            {
                _worldPairStatus.Text = "已识别 PC；未找到 Quest，将仅上传 PC";
                _worldPairStatus.ForeColor = Warn;
            }
            else
            {
                _worldPairStatus.Text = "已配对 PC + Quest，将依次上传到同一 World ID";
                _worldPairStatus.ForeColor = Accent;
            }
        }

        void PickFile(TextBox target, string filter, TextBox autoName)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = filter;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                target.Text = dlg.FileName;
                if (autoName != null)
                    autoName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        }

        string SelectedRelease()
        {
            return _release.SelectedItem == null ? "private" : _release.SelectedItem.ToString();
        }

        string FormatPreflightSummary(VrcaClient.PreflightResult result)
        {
            var text = "错误: " + result.ErrorCount +
                "\n可修复: " + result.RepairableCount +
                "\n需要官方构建: " + result.BuildRequiredCount +
                "\n警告: " + result.WarningCount +
                "\n报告: " + result.ReportPath;
            var shown = 0;
            if (result.Items != null)
            {
                foreach (var item in result.Items)
                {
                    if (item.Level != "ERROR" && item.Level != "REPAIRABLE" && item.Level != "BUILD_REQUIRED" && item.Level != "WARN") continue;
                    if (shown == 0) text += "\n\n主要问题:";
                    text += "\n[" + item.Level + "] " + item.Code + ": " + item.Message;
                    shown++;
                    if (shown >= 8) break;
                }
            }
            return text;
        }

        UploadJob PickDetailJob()
        {
            lock (_jobsLock)
            {
                foreach (var job in _jobs)
                    if (job.State == "运行中")
                        return job;
                foreach (var job in _jobs)
                    if (job.State == "排队中")
                        return job;
                if (_jobs.Count > 0)
                    return _jobs[_jobs.Count - 1];
            }
            return null;
        }

        void UpdateDetailPanel(UploadJob job)
        {
            if (_progressList == null) return;
            if (job == null)
            {
                UpdateEmptyProgressLabel();
                return;
            }

            EnsureProgressCard(job);
            UpdateProgressCard(job);
            UpdateEmptyProgressLabel();
        }







        void UpdateProgressCard(UploadJob job)
        {
            if (job == null || job.ProgressCard == null) return;

            var completed = GetCompletedStepCount(job);
            var currentStep = GetCurrentStepName(job);
            var currentPercent = GetCurrentStepPercent(job);
            var overallProgress = GetOverallDisplayProgress(job);
            var id = string.IsNullOrEmpty(job.AvatarId) ? "-" : job.AvatarId;

            job.CardNameLabel.Text = "名称: " + job.Name;
            job.CardIdLabel.Text = "ID: " + id;
            job.CardOverallLabel.Text = BuildOverallProgressText(job, completed, overallProgress);
            job.CardStepLabel.Text = "当前步骤: " + currentStep + "  " + currentPercent + "%";
            job.CardHintLabel.Text = string.IsNullOrEmpty(job.Error) ? BuildDetailHint(job) : job.Error;
            job.CardHintLabel.ForeColor = RowColor(job.State);
            job.CardCopyButton.Enabled = !string.IsNullOrEmpty(job.AvatarId);
            job.CardStopButton.Enabled = job.State == "运行中" || job.State == "排队中";
            job.CardRemoveButton.Enabled = !IsJobActive(job);

            SetProgressColor(job.CardOverallProgress, ProgressColor(job));
            SetProgressColor(job.CardStepProgress, ProgressColor(job));
            SetProgressValue(job.CardOverallProgress, overallProgress);
            SetProgressValue(job.CardStepProgress, currentPercent);
            SetJobCardCover(job, job.ImagePath);
        }

        void StopUploadJob(UploadJob job)
        {
            if (job == null) return;

            VrcaClient client = null;
            lock (_jobsLock)
            {
                if (job.State == "排队中")
                {
                    job.CancelRequested = true;
                    job.State = "已停止";
                    job.Stage = "已停止";
                    job.FinishedAt = DateTime.Now;
                }
                else if (job.State == "运行中")
                {
                    job.CancelRequested = true;
                    job.State = "停止中";
                    job.Stage = "正在停止";
                    client = job.Client;
                }
                else
                {
                    return;
                }
            }

            if (client != null)
                client.Cancel();

            SetCustomerStatus("正在停止任务：" + job.Name);
            UpdateJobRow(job);
            StartNextJobs();
        }

        bool IsJobActive(UploadJob job)
        {
            if (job == null) return false;
            return job.State == "运行中" || job.State == "停止中";
        }

        void RemoveProgressJob(UploadJob job)
        {
            if (job == null) return;
            if (IsJobActive(job))
            {
                SetCustomerStatus("任务正在上传，完成后再移除：" + job.Name);
                return;
            }

            lock (_jobsLock)
                _jobs.Remove(job);

            if (_progressList != null && job.ProgressCard != null)
                _progressList.Controls.Remove(job.ProgressCard);

            DisposeJobCardImage(job);
            if (job.ProgressCard != null)
            {
                job.ProgressCard.Dispose();
                job.ProgressCard = null;
            }

            UpdateEmptyProgressLabel();
            SetCustomerStatus("已移除任务：" + job.Name);
        }

        void SwitchEditorToJob(UploadJob job)
        {
            if (job == null) return;
            _vrca.Text = job.VrcaPath ?? "";
            _image.Text = job.ImagePath ?? "";
            _name.Text = job.Name ?? "";
            _desc.Text = job.Request == null ? "" : job.Request.Description ?? "";
            if (job.Request != null && !string.IsNullOrEmpty(job.Request.ReleaseStatus))
                _release.SelectedItem = job.Request.ReleaseStatus;
            SetCustomerStatus("已切换到任务：" + job.Name);
        }

        void UpdateEmptyProgressLabel()
        {
            if (_emptyProgressLabel == null || _progressList == null) return;
            var isEmpty = _progressList.Controls.Count == 0;
            _emptyProgressLabel.Visible = isEmpty;
            _progressList.Visible = !isEmpty;
        }

        string BuildDetailHint(UploadJob job)
        {
            if (job.State == "排队中") return "任务已加入队列，点击“开始上传”后会自动处理。";
            if (job.State == "成功") return "Upload completed: " + (string.IsNullOrEmpty(job.AvatarId) ? job.Name : job.AvatarId);
            if (job.State == "停止中") return "正在停止任务，请稍候。";
            if (job.State == "已停止") return "任务已停止，可移除后重新添加。";
            if (job.State == "安全失败") return "安全检测失败，请打开详情查看完整日志。";
            if (job.State == "失败") return "上传失败，请打开详情查看完整日志。";
            if (job.State == "需要登录") return "登录已过期，请重新登录后继续上传。";
            return "阶段说明：本地处理 -> 上传文件 -> 安全检测。详情中可实时查看技术日志。";
        }

        string DetailStateText(UploadJob job)
        {
            if (job.State == "安全失败") return "安全检测失败";
            if (job.State == "失败") return "上传失败";
            if (job.State == "成功") return "上传成功";
            if (job.State == "停止中") return "正在停止";
            if (job.State == "已停止") return "已停止";
            if (job.State == "需要登录") return "需要重新登录";
            if (job.State == "运行中" && !string.IsNullOrEmpty(job.Stage)) return job.Stage;
            return job.State;
        }

        int GetCompletedStepCount(UploadJob job)
        {
            var count = 0;
            if (job.LocalProgress >= 100) count++;
            if (job.UploadProgress >= 100) count++;
            if (job.SecurityProgress >= 100) count++;
            return count;
        }

        string BuildOverallProgressText(UploadJob job, int completed, int overallProgress)
        {
            if (IsDualPlatformWorld(job))
            {
                if (job.State == "成功")
                    return "双端总进度: PC 已完成 · Quest 已完成    100%";
                if (job.State == "部分成功")
                    return "双端总进度: PC 已完成 · Quest 未完成    " + overallProgress + "%";
                if (job.State == "安全失败")
                    return job.PcPlatformCompleted
                        ? "双端总进度: PC 已完成 · Quest 检测失败    " + overallProgress + "%"
                        : "双端总进度: PC 检测失败 · Quest 未开始    " + overallProgress + "%";
                if (job.State == "失败")
                    return job.PcPlatformCompleted
                        ? "双端总进度: PC 已完成 · Quest 上传失败    " + overallProgress + "%"
                        : "双端总进度: PC 上传失败 · Quest 未开始    " + overallProgress + "%";
                if (job.State == "需要登录")
                    return "双端总进度: 等待重新登录    " + overallProgress + "%";
                if (job.PcPlatformCompleted && job.State == "停止中")
                    return "双端总进度: PC 已完成 · Quest 正在停止    " + overallProgress + "%";
                if (job.PcPlatformCompleted && job.State == "已停止")
                    return "双端总进度: PC 已完成 · Quest 已停止    " + overallProgress + "%";
                if (job.QuestPhaseActive)
                    return "双端总进度: PC 已完成 · Quest 处理中    " + overallProgress + "%";
                if (job.PcPlatformCompleted)
                    return "双端总进度: PC 已完成 · Quest 准备中    50%";
                return "双端总进度: PC 处理中 · Quest 等待    " + overallProgress + "%";
            }
            if (job.State == "安全失败") return "总进度: 2 / 3    检测失败";
            if (job.State == "停止中") return "总进度: 正在停止";
            if (job.State == "已停止") return "总进度: 已停止";
            if (job.State == "失败") return "总进度: 已中止";
            if (job.State == "需要登录") return "总进度: 等待登录";
            return "总进度: " + completed + " / 3    " + overallProgress + "%";
        }

        int GetOverallDisplayProgress(UploadJob job)
        {
            if (job.State == "安全失败") return 95;
            if (IsDualPlatformWorld(job) && job.PcPlatformCompleted &&
                (job.State == "停止中" || job.State == "已停止" || job.State == "部分成功"))
                return Math.Min(Math.Max(job.Progress, 50), 99);
            if (job.State == "已停止") return Math.Min(Math.Max(job.Progress, 0), 90);
            if (job.State == "失败") return Math.Min(Math.Max(job.Progress, 8), 90);
            if (job.State == "需要登录") return 0;
            return job.Progress;
        }

        string GridProgressText(UploadJob job)
        {
            if (job.State == "安全失败" || job.State == "失败") return "失败";
            if (job.State == "停止中") return "停止中";
            if (job.State == "已停止") return "已停止";
            if (job.State == "需要登录") return "登录";
            return GetOverallDisplayProgress(job) + "%";
        }

        string GetCurrentStepName(UploadJob job)
        {
            if (job.State == "安全失败") return "安全检测失败";
            if (job.State == "停止中") return "正在停止";
            if (job.State == "已停止") return "已停止";
            if (job.State == "失败") return "上传失败";
            if (job.State == "需要登录") return "需要登录";
            var platformPrefix = IsDualPlatformWorld(job)
                ? (job.QuestPhaseActive ? "Quest · " : "PC · ")
                : "";
            if (job.LocalProgress < 100) return platformPrefix + "本地处理";
            if (job.UploadProgress < 100) return platformPrefix + "上传文件";
            if (job.SecurityProgress < 100) return platformPrefix + "安全检测";
            return platformPrefix + "完成";
        }

        int GetCurrentStepPercent(UploadJob job)
        {
            if (job.State == "安全失败") return 100;
            if (job.State == "停止中" || job.State == "已停止") return GetOverallDisplayProgress(job);
            if (job.State == "失败") return GetOverallDisplayProgress(job);
            if (job.State == "需要登录") return 0;
            if (job.LocalProgress < 100) return job.LocalProgress;
            if (job.UploadProgress < 100) return job.UploadProgress;
            if (job.SecurityProgress < 100) return job.SecurityProgress;
            return 100;
        }

        void SetProgressValue(ProgressStrip bar, int value)
        {
            if (bar == null) return;
            bar.Value = ClampPercent(value);
        }

        void SetProgressColor(ProgressStrip bar, Color color)
        {
            if (bar == null) return;
            if (bar.FillColor.ToArgb() == color.ToArgb()) return;
            bar.FillColor = color;
            bar.Invalidate();
        }

        Color ProgressColor(UploadJob job)
        {
            if (job.State == "失败" || job.State == "安全失败" || job.State == "需要登录") return Danger;
            if (job.State == "停止中" || job.State == "已停止") return Warn;
            return Accent;
        }

        void SetJobCardCover(UploadJob job, string path)
        {
            if (job == null || job.CardThumb == null) return;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try { path = EnsureDefaultCoverFile(); } catch { path = ""; }
            }

            var current = job.CardThumb.Tag as string;
            if (string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                return;

            var old = job.CardThumb.Image;
            job.CardThumb.Image = LoadImageCopy(path);
            job.CardThumb.Tag = path;
            if (old != null) old.Dispose();
        }

        void DisposeJobCardImage(UploadJob job)
        {
            if (job == null || job.CardThumb == null || job.CardThumb.Image == null) return;
            var old = job.CardThumb.Image;
            job.CardThumb.Image = null;
            job.CardThumb.Tag = null;
            old.Dispose();
        }

        Image LoadImageCopy(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                using (var stream = File.OpenRead(path))
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
            catch
            {
                return null;
            }
        }

        void UpdateSummary()
        {
            List<UploadJob> snapshot;
            lock (_jobsLock)
                snapshot = new List<UploadJob>(_jobs);

            foreach (var job in snapshot)
                UpdateDetailPanel(job);

            UpdateEmptyProgressLabel();
        }

        void RunLoginThread(ThreadStart work)
        {
            var thread = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex)
                {
                    OnUi(delegate
                    {
                        SetLoginBusy(false);
                        SetLoginStatus("登录失败: " + ex.Message, Danger);
                    });
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }





        void SetLoginBusy(bool busy)
        {
            _loginBusy = busy;
            _loginBtn.Enabled = !busy;
            _enterAppBtn.Enabled = !busy && _loggedIn;
            _user.Enabled = !busy && !_awaiting2FA;
            _pass.Enabled = !busy && !_awaiting2FA;
            _twoFa.Enabled = !busy && _awaiting2FA;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        void SetLoginStatus(string text, Color color)
        {
            if (_loginStatus == null) return;
            _loginStatus.Text = text;
            _loginStatus.ForeColor = color;
        }

        void AppendActivity(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            SetCustomerStatus(ToCustomerMessage(message));
        }

        void SetCustomerStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (_customerStatus == null || !_customerStatus.IsHandleCreated)
                return;
            if (_customerStatus.InvokeRequired)
            {
                try { _customerStatus.BeginInvoke((Action<string>)SetCustomerStatus, message); } catch { }
                return;
            }

            _customerStatus.Text = message;
        }

        string ToCustomerMessage(string message) { return VrcaClient.ProtectDiagnosticText(message); }

        bool ShouldShowActivity(string message) { return !string.IsNullOrEmpty(message); }

        void OnUi(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }







        void AddCardTitle(Control parent, string title, string subtitle)
        {
            parent.Controls.Add(new Label
            {
                Text = title,
                Location = new Point(24, 16),
                Size = new Size(parent.Width - 48, 38),
                ForeColor = TextMain,
                BackColor = Color.Transparent,
                Font = UiFont(14f, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false
            });
            if (string.IsNullOrEmpty(subtitle)) return;
            parent.Controls.Add(new Label
            {
                Text = subtitle,
                Location = new Point(25, 54),
                Size = new Size(parent.Width - 50, 26),
                ForeColor = TextMuted,
                BackColor = Color.Transparent,
                Font = UiFont(9f),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false
            });
        }

        Label MakeLabel(string text, int x, int y, int w, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 22),
                ForeColor = color,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false
            };
        }

        TextBox MakeTextBox(int x, int y, int w, bool password)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 30),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = HotUploadPreview.Theme.Input,
                ForeColor = TextMain,
                UseSystemPasswordChar = password,
                Font = UiFont(10f)
            };
        }



        Label MakeActionChip(string text, int x, int y, int w, int h)
        {
            var chip = new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Color.FromArgb(32, 42, 60),
                ForeColor = TextMain,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = UiFont(9.5f, FontStyle.Bold)
            };
            chip.MouseEnter += delegate { if (chip.Enabled) chip.BackColor = Color.FromArgb(48, 67, 96); };
            chip.MouseLeave += delegate { chip.BackColor = chip.Enabled ? Color.FromArgb(32, 42, 60) : Color.FromArgb(22, 26, 34); };
            chip.EnabledChanged += delegate
            {
                chip.BackColor = chip.Enabled ? Color.FromArgb(32, 42, 60) : Color.FromArgb(22, 26, 34);
                chip.ForeColor = chip.Enabled ? TextMain : TextMuted;
                chip.Cursor = chip.Enabled ? Cursors.Hand : Cursors.Default;
            };
            return chip;
        }

        Label MakeStageLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(92, 22),
                ForeColor = TextMuted,
                BackColor = Bg,
                Font = UiFont(9f, FontStyle.Bold)
            };
        }

        ProgressBar MakeStageProgress(int x, int y)
        {
            return new ProgressBar
            {
                Location = new Point(x, y),
                Size = new Size(524, 14),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                Value = 0
            };
        }

        Label MakeDetailLabel(string text, int x, int y, int w, int h, Color color, float size, FontStyle style)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                ForeColor = color,
                BackColor = Color.Transparent,
                Font = UiFont(size, style),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false
            };
        }

        ProgressStrip MakeLargeProgress(int x, int y, int w, int h)
        {
            return new ProgressStrip
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                Value = 0,
                FillColor = Accent
            };
        }

        Color RowColor(string state)
        {
            if (state == "成功") return Accent;
            if (state == "失败" || state == "安全失败" || state == "需要登录" || state == "授权失败") return Danger;
            if (state == "停止中" || state == "已停止") return Warn;
            if (state == "排队中") return TextMuted;
            return TextMain;
        }

        string SafeError(string text)
        {
            return string.IsNullOrEmpty(text) ? "未知错误" : text;
        }

        string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "...";
        }

        static Color Blend(Color from, Color to, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            var r = (int)(from.R + (to.R - from.R) * amount);
            var g = (int)(from.G + (to.G - from.G) * amount);
            var b = (int)(from.B + (to.B - from.B) * amount);
            return Color.FromArgb(from.A, r, g, b);
        }

        void SetStageBar(ProgressBar bar, Label label, string title, int value)
        {
            value = ClampPercent(value);
            if (bar != null) bar.Value = value;
            if (label != null) label.Text = title + " " + value + "%";
        }

        int ClampPercent(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2f;
            if (diameter <= 0f)
            {
                path.AddRectangle(bounds);
                path.CloseFigure();
                return path;
            }

            var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        class CyberStagePanel : Panel
        {
            public CyberStagePanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var rect = ClientRectangle;
                using (var brush = new LinearGradientBrush(rect, Color.FromArgb(5, 8, 14), Color.FromArgb(12, 19, 30), 35f))
                    e.Graphics.FillRectangle(brush, rect);

                using (var pen = new Pen(Color.FromArgb(18, 68, 220, 255), 1f))
                {
                    for (var x = -40; x < Width; x += 44)
                        e.Graphics.DrawLine(pen, x, 0, x + 120, Height);
                    for (var y = 0; y < Height; y += 42)
                        e.Graphics.DrawLine(pen, 0, y, Width, y);
                }

                using (var glow = new SolidBrush(Color.FromArgb(24, 58, 255, 151)))
                    e.Graphics.FillEllipse(glow, Width - 380, 80, 520, 260);
            }
        }

        class CyberCard : Panel
        {
            public CyberCard()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? HotUploadPreview.Theme.Back : Parent.BackColor);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1, 1, Width - 3, Height - 3);
                using (var path = RoundedRect(rect, 16f))
                using (var fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(25, 31, 44), Color.FromArgb(16, 20, 31), 90f))
                using (var border = new Pen(Color.FromArgb(92, 68, 220, 255), 1f))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(border, path);
                }

                using (var accent = new Pen(Color.FromArgb(120, 58, 255, 151), 2f))
                    e.Graphics.DrawLine(accent, 22, 1, Math.Min(Width - 24, 132), 1);

                base.OnPaint(e);
            }
        }

        class ProgressShellCard : Panel
        {
            public ProgressShellCard()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using (var fill = new SolidBrush(BackColor))
                    e.Graphics.FillRectangle(fill, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.None;

                var borderRect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (var fill = new SolidBrush(BackColor))
                    e.Graphics.FillRectangle(fill, ClientRectangle);
                using (var border = new Pen(Color.FromArgb(42, 53, 70), 1f))
                    e.Graphics.DrawRectangle(border, borderRect);
            }
        }

        class ProgressItemCard : Panel
        {
            public ProgressItemCard()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using (var fill = new SolidBrush(BackColor))
                    e.Graphics.FillRectangle(fill, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.None;

                var borderRect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (var fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(17, 21, 32), Color.FromArgb(10, 13, 21), 90f))
                    e.Graphics.FillRectangle(fill, ClientRectangle);
                using (var border = new Pen(Color.FromArgb(48, 60, 78), 1f))
                    e.Graphics.DrawRectangle(border, borderRect);
            }
        }

        class ProgressStrip : Control
        {
            int _value;

            public Color FillColor { get; set; }

            public int Value
            {
                get { return _value; }
                set
                {
                    var next = value;
                    if (next < 0) next = 0;
                    if (next > 100) next = 100;
                    if (_value == next) return;
                    _value = next;
                    Invalidate();
                }
            }

            public ProgressStrip()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                BackColor = Color.FromArgb(13, 15, 20);
                ForeColor = Color.White;
                FillColor = Color.FromArgb(74, 222, 128);
                Font = UiFont(9f, FontStyle.Bold);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var track = new SolidBrush(Color.FromArgb(30, 34, 45)))
                    e.Graphics.FillRectangle(track, bounds);

                var fillWidth = (int)Math.Round((Width - 2) * (_value / 100.0));
                if (fillWidth > 0)
                {
                    var fill = new Rectangle(1, 1, fillWidth, Math.Max(1, Height - 2));
                    using (var brush = new LinearGradientBrush(fill, FillColor, Color.FromArgb(87, 211, 255), 0f))
                        e.Graphics.FillRectangle(brush, fill);
                }

                using (var pen = new Pen(Color.FromArgb(88, 96, 118)))
                    e.Graphics.DrawRectangle(pen, bounds);

                if (Height >= Font.Height + 4) TextRenderer.DrawText(
                    e.Graphics,
                    _value + "%",
                    Font,
                    new Rectangle(8, 0, Math.Max(1, Width - 14), Height),
                    ForeColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        class BatchUploadSelection
        {
            public string AssetType;
            public string VrcaPath;
            public string QuestVrcwPath;
            public string ImagePath;
            public string DisplayName;
            public string PlatformLabel;
            public string WorldId;
            public string PairKey;
            public string CoverKey;
            public string Description;
        }

        class WorldProbeInfo
        {
            public int Platform;
            public string WorldId;
            public string UnityVersion;
            public string SceneName;
            public string Error;
        }

        class ScanProbeCacheEntry
        {
            public string Path;
            public long Length;
            public long LastWriteUtcTicks;
            public int Platform;
            public string WorldId;
            public string UnityVersion;
            public string SceneName;
        }

        class ScanCatalogFile
        {
            public string RootFolder;
            public List<ScanCatalogEntry> Items;
        }

        class ScanCatalogEntry
        {
            public string AssetType;
            public string VrcaPath;
            public string QuestVrcwPath;
            public string ImagePath;
            public string DisplayName;
            public string PlatformLabel;
            public string WorldId;
            public string PairKey;
            public string CoverKey;
            public string Description;
            public long PcLength;
            public long PcWriteUtcTicks;
            public long QuestLength;
            public long QuestWriteUtcTicks;
        }

        class FolderScanWorkspace : Panel
        {
            static readonly SemaphoreSlim ThumbnailWorkers = new SemaphoreSlim(4, 4);
            readonly Label _title;
            readonly Label _summary;
            readonly Label _modelTitle;
            readonly Label _worldTitle;
            readonly FlowLayoutPanel _modelFlow;
            readonly FlowLayoutPanel _worldFlow;
            readonly Button _uploadSelected;
            readonly Button _stopButton;
            readonly Button _collapseButton;
            readonly List<ScanAssetCard> _cards = new List<ScanAssetCard>();
            readonly Queue<BatchUploadSelection> _pendingRows = new Queue<BatchUploadSelection>();
            readonly object _pendingLock = new object();
            readonly object _thumbnailLock = new object();
            readonly Dictionary<string, List<ScanAssetCard>> _thumbnailBuilds =
                new Dictionary<string, List<ScanAssetCard>>(StringComparer.OrdinalIgnoreCase);
            string _defaultImage = "";
            string _rootFolder = "";
            volatile bool _cancelled;
            bool _scanRunning;
            bool _drainScheduled;
            bool _completionRequested;
            Exception _completionError;

            public event Action<List<BatchUploadSelection>> UploadRequested;
            public event Action StopRequested;
            public event Action CollapseRequested;
            public bool ScanCancelled { get { return _cancelled; } }
            public bool HasResults { get { return _cards.Count > 0; } }

            public FolderScanWorkspace()
            {
                BackColor = Card;
                BorderStyle = BorderStyle.FixedSingle;

                _title = TextLabel("文件夹扫描", 13f, FontStyle.Bold, TextMain);
                Controls.Add(_title);
                _summary = TextLabel("选择文件夹后开始扫描", 9f, FontStyle.Regular, TextMuted);
                Controls.Add(_summary);

                var all = ActionButton("全选", Accent2, Color.FromArgb(10, 26, 34));
                all.Click += delegate { SetAllChecked(true); };
                Controls.Add(all);
                all.Name = "selectAll";
                var none = ActionButton("全不选", Card2, TextMain);
                none.Click += delegate { SetAllChecked(false); };
                Controls.Add(none);
                none.Name = "selectNone";
                _collapseButton = ActionButton("任务队列", Card2, TextMain);
                _collapseButton.Click += delegate
                {
                    var handler = CollapseRequested;
                    if (handler != null) handler();
                };
                Controls.Add(_collapseButton);

                _modelTitle = TextLabel("模型 VRCA", 10.5f, FontStyle.Bold, Accent);
                _worldTitle = TextLabel("世界 VRCW · PC / Quest", 10.5f, FontStyle.Bold, Accent2);
                Controls.Add(_modelTitle);
                Controls.Add(_worldTitle);

                _modelFlow = ResultFlow();
                _worldFlow = ResultFlow();
                Controls.Add(_modelFlow);
                Controls.Add(_worldFlow);

                _uploadSelected = ActionButton("上传已选", Accent, Color.FromArgb(12, 28, 20));
                _uploadSelected.Click += delegate { UploadChecked(); };
                Controls.Add(_uploadSelected);
                _stopButton = ActionButton("停止扫描", Card2, TextMain);
                _stopButton.Click += delegate
                {
                    var handler = StopRequested;
                    if (handler != null) handler();
                };
                Controls.Add(_stopButton);

                Resize += delegate { LayoutWorkspace(); };
                LayoutWorkspace();
                UpdateSummary();
            }

            Label TextLabel(string text, float size, FontStyle style, Color color)
            {
                return new Label
                {
                    Text = text, ForeColor = color, BackColor = Color.Transparent,
                    Font = UiFont(size, style), AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    UseCompatibleTextRendering = false
                };
            }

            Button ActionButton(string text, Color back, Color fore)
            {
                var button = new Button
                {
                    Text = text, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat,
                    Font = UiFont(9f, FontStyle.Bold), Cursor = Cursors.Hand,
                    TextAlign = ContentAlignment.MiddleCenter,
                    UseCompatibleTextRendering = false,
                    Padding = Padding.Empty
                };
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = Color.FromArgb(72, back);
                return button;
            }

            FlowLayoutPanel ResultFlow()
            {
                var flow = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                    BackColor = HotUploadPreview.Theme.Input, Padding = new Padding(8)
                };
                flow.HorizontalScroll.Enabled = false;
                flow.HorizontalScroll.Visible = false;
                flow.Scroll += delegate { LoadVisibleThumbnails(flow); };
                flow.Resize += delegate { LoadVisibleThumbnails(flow); };
                return flow;
            }

            void LayoutWorkspace()
            {
                var width = Math.Max(620, ClientSize.Width);
                var height = Math.Max(360, ClientSize.Height);
                _title.SetBounds(24, 18, Math.Max(220, width - 420), 30);
                _summary.SetBounds(24, 48, Math.Max(300, width - 300), 24);
                var all = Controls["selectAll"] as Button;
                var none = Controls["selectNone"] as Button;
                if (all != null) all.SetBounds(width - 190, 22, 78, 34);
                if (none != null) none.SetBounds(width - 102, 22, 78, 34);
                _collapseButton.SetBounds(width - 278, 22, 78, 34);

                var gap = 16;
                var sideWidth = Math.Max(270, (width - 48 - gap) / 2);
                var rightX = 24 + sideWidth + gap;
                _modelTitle.SetBounds(24, 78, sideWidth, 25);
                _worldTitle.SetBounds(rightX, 78, sideWidth, 25);
                var flowHeight = Math.Max(190, height - 170);
                _modelFlow.SetBounds(24, 106, sideWidth, flowHeight);
                _worldFlow.SetBounds(rightX, 106, sideWidth, flowHeight);
                _uploadSelected.SetBounds(width - 254, height - 52, 118, 36);
                _stopButton.SetBounds(width - 126, height - 52, 102, 36);
                ResizeCards(_modelFlow);
                ResizeCards(_worldFlow);
            }

            void ResizeCards(FlowLayoutPanel flow)
            {
                var cardWidth = Math.Max(245, flow.ClientSize.Width - flow.Padding.Horizontal - 22);
                foreach (Control control in flow.Controls)
                {
                    control.Width = cardWidth;
                    LayoutCard(control as Panel);
                }
            }

            void LayoutCard(Panel panel)
            {
                if (panel == null || !(panel.Tag is ScanAssetCard)) return;
                var card = (ScanAssetCard)panel.Tag;
                var width = panel.ClientSize.Width;
                card.Thumbnail.SetBounds(10, 12, 88, 88);
                var actionX = Math.Max(180, width - 76);
                var contentWidth = Math.Max(80, actionX - 114);
                card.NameLabel.SetBounds(110, 12, contentWidth, 26);
                card.DescriptionLabel.SetBounds(110, 40, contentWidth, 64);
                card.Selected.SetBounds(actionX + 42, 8, 22, 22);
                card.Upload.SetBounds(actionX, 38, 64, 32);
                card.Cover.SetBounds(actionX, 76, 64, 26);
            }

            public void BeginScan(string rootFolder, string defaultImage)
            {
                CancelScan();
                ClearCards();
                lock (_pendingLock)
                {
                    _pendingRows.Clear();
                    _drainScheduled = false;
                    _completionRequested = false;
                    _completionError = null;
                }
                _rootFolder = rootFolder ?? "";
                _defaultImage = defaultImage ?? "";
                _cancelled = false;
                _scanRunning = true;
                _title.Text = "正在扫描，结果会逐项出现...";
                _stopButton.Text = "停止扫描";
                _stopButton.Enabled = true;
                UpdateSummary();
            }

            public void RestoreCachedResults(string rootFolder, string defaultImage, List<BatchUploadSelection> rows)
            {
                CancelScan();
                ClearCards();
                lock (_pendingLock)
                {
                    _pendingRows.Clear();
                    _drainScheduled = false;
                    _completionRequested = false;
                    _completionError = null;
                }
                _rootFolder = rootFolder ?? "";
                _defaultImage = defaultImage ?? "";
                _cancelled = false;
                _scanRunning = false;
                _title.Text = "已恢复上次扫描结果";
                if (rows != null)
                    foreach (var row in rows) AddRowCore(row);
                ResizeCards(_modelFlow);
                ResizeCards(_worldFlow);
                LoadVisibleThumbnails(_modelFlow);
                LoadVisibleThumbnails(_worldFlow);
                _stopButton.Text = "扫描完成";
                _stopButton.Enabled = false;
                UpdateSummary();
            }

            public void CancelScan()
            {
                _cancelled = true;
                _scanRunning = false;
                _stopButton.Text = "已停止";
                _stopButton.Enabled = false;
                UpdateSummary();
            }

            void ClearCards()
            {
                foreach (var card in _cards)
                {
                    try { if (card.Thumbnail.Image != null) card.Thumbnail.Image.Dispose(); } catch { }
                    try { card.Panel.Dispose(); } catch { }
                }
                _cards.Clear();
                _modelFlow.Controls.Clear();
                _worldFlow.Controls.Clear();
            }

            public void AddScannedFile(BatchUploadSelection row)
            {
                if (row == null || _cancelled || IsDisposed) return;
                var schedule = false;
                lock (_pendingLock)
                {
                    _pendingRows.Enqueue(row);
                    if (!_drainScheduled)
                    {
                        _drainScheduled = true;
                        schedule = true;
                    }
                }
                if (!schedule) return;
                try { BeginInvoke(new Action(DrainPending)); }
                catch (InvalidOperationException) { }
            }

            void DrainPending()
            {
                if (_cancelled || IsDisposed) return;
                var rows = new List<BatchUploadSelection>();
                var hasMore = false;
                var finish = false;
                Exception error = null;
                lock (_pendingLock)
                {
                    while (_pendingRows.Count > 0 && rows.Count < 24) rows.Add(_pendingRows.Dequeue());
                    hasMore = _pendingRows.Count > 0;
                    if (!hasMore)
                    {
                        _drainScheduled = false;
                        finish = _completionRequested;
                        error = _completionError;
                    }
                }
                _modelFlow.SuspendLayout();
                _worldFlow.SuspendLayout();
                try { foreach (var row in rows) AddRowCore(row); }
                finally
                {
                    _modelFlow.ResumeLayout();
                    _worldFlow.ResumeLayout();
                }
                ResizeCards(_modelFlow);
                ResizeCards(_worldFlow);
                LoadVisibleThumbnails(_modelFlow);
                LoadVisibleThumbnails(_worldFlow);
                UpdateSummary();
                if (hasMore)
                {
                    try { BeginInvoke(new Action(DrainPending)); } catch (InvalidOperationException) { }
                }
                else if (finish) FinishScan(error);
            }

            public void MarkScanComplete(Exception error)
            {
                if (_cancelled || IsDisposed) return;
                var schedule = false;
                lock (_pendingLock)
                {
                    _completionRequested = true;
                    _completionError = error;
                    if (!_drainScheduled)
                    {
                        _drainScheduled = true;
                        schedule = true;
                    }
                }
                if (!schedule) return;
                try { BeginInvoke(new Action(DrainPending)); } catch (InvalidOperationException) { }
            }

            void FinishScan(Exception error)
            {
                _scanRunning = false;
                _title.Text = error == null ? (_cards.Count == 0 ? "扫描完成，没有找到文件" : "扫描完成") :
                    "扫描结束，部分文件读取失败";
                _summary.ForeColor = error == null ? TextMuted : Warn;
                _stopButton.Text = "扫描完成";
                _stopButton.Enabled = false;
                UpdateSummary();
            }

            void AddRowCore(BatchUploadSelection row)
            {
                var merge = FindMergeCard(row);
                if (merge != null)
                {
                    MergeWorldRow(merge.Row, row);
                    RefreshCard(merge);
                    return;
                }
                var card = CreateCard(row);
                _cards.Add(card);
                (row.AssetType == "地图" ? _worldFlow : _modelFlow).Controls.Add(card.Panel);
            }

            ScanAssetCard FindMergeCard(BatchUploadSelection row)
            {
                if (row.AssetType != "地图") return null;
                foreach (var card in _cards)
                {
                    var current = card.Row;
                    if (current.AssetType != "地图") continue;
                    var same = (!string.IsNullOrEmpty(current.WorldId) && !string.IsNullOrEmpty(row.WorldId) &&
                            string.Equals(current.WorldId, row.WorldId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(current.PairKey) && !string.IsNullOrEmpty(row.PairKey) &&
                            string.Equals(current.PairKey, row.PairKey, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(current.CoverKey) && !string.IsNullOrEmpty(row.CoverKey) &&
                            string.Equals(current.CoverKey, row.CoverKey, StringComparison.OrdinalIgnoreCase));
                    if (!same) continue;
                    if ((!string.IsNullOrEmpty(row.VrcaPath) && string.IsNullOrEmpty(current.VrcaPath)) ||
                        (!string.IsNullOrEmpty(row.QuestVrcwPath) && string.IsNullOrEmpty(current.QuestVrcwPath)))
                        return card;
                }
                return null;
            }

            void MergeWorldRow(BatchUploadSelection target, BatchUploadSelection incoming)
            {
                if (!string.IsNullOrEmpty(incoming.VrcaPath))
                {
                    target.VrcaPath = incoming.VrcaPath;
                    target.DisplayName = incoming.DisplayName;
                    target.ImagePath = incoming.ImagePath;
                    target.Description = incoming.Description;
                }
                if (!string.IsNullOrEmpty(incoming.QuestVrcwPath)) target.QuestVrcwPath = incoming.QuestVrcwPath;
                if (string.IsNullOrEmpty(target.WorldId)) target.WorldId = incoming.WorldId;
                if (string.IsNullOrEmpty(target.PairKey)) target.PairKey = incoming.PairKey;
                if (string.IsNullOrEmpty(target.CoverKey)) target.CoverKey = incoming.CoverKey;
                target.PlatformLabel = !string.IsNullOrEmpty(target.VrcaPath) && !string.IsNullOrEmpty(target.QuestVrcwPath)
                    ? "PC + Quest" : (!string.IsNullOrEmpty(target.VrcaPath) ? "仅 PC" : "仅 Quest");
                if (target.PlatformLabel == "PC + Quest")
                    target.DisplayName = ChoosePairedWorldName(target.VrcaPath, target.QuestVrcwPath, target.DisplayName);
            }

            ScanAssetCard CreateCard(BatchUploadSelection row)
            {
                var panel = new Panel
                {
                    Height = 116, BackColor = Color.FromArgb(18, 24, 36),
                    Margin = new Padding(0, 0, 0, 8), BorderStyle = BorderStyle.FixedSingle
                };
                var card = new ScanAssetCard
                {
                    Row = row,
                    Panel = panel,
                    Thumbnail = new PictureBox { BackColor = Color.FromArgb(8, 12, 20), SizeMode = PictureBoxSizeMode.Zoom },
                    NameLabel = TextLabel("", 9.5f, FontStyle.Bold, TextMain),
                    DescriptionLabel = TextLabel("", 8.2f, FontStyle.Regular, TextMuted),
                    Selected = new CheckBox { BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat },
                    Upload = ActionButton("上传", Accent, Color.FromArgb(12, 28, 20)),
                    Cover = ActionButton("封面", Card2, TextMain)
                };
                panel.Tag = card;
                panel.Controls.Add(card.Thumbnail);
                panel.Controls.Add(card.NameLabel);
                panel.Controls.Add(card.DescriptionLabel);
                panel.Controls.Add(card.Selected);
                panel.Controls.Add(card.Upload);
                panel.Controls.Add(card.Cover);
                card.Selected.CheckedChanged += delegate { UpdateSummary(); };
                card.Upload.Click += delegate { UploadOne(card); };
                card.Cover.Click += delegate { PickCover(card); };
                panel.Click += delegate { if (card.Selected.Enabled) card.Selected.Checked = !card.Selected.Checked; };
                RefreshCard(card);
                return card;
            }

            void RefreshCard(ScanAssetCard card)
            {
                var row = card.Row;
                card.NameLabel.Text = row.DisplayName ?? "未命名";
                var bytes = SafeLength(row.VrcaPath) + SafeLength(row.QuestVrcwPath);
                var platform = row.AssetType == "地图" ? row.PlatformLabel : "模型 VRCA";
                card.DescriptionLabel.Text = (string.IsNullOrEmpty(row.Description) ? "暂无简介" : row.Description) +
                    "\r\n" + platform + " · " + FormatSize(bytes);
                var usable = !string.IsNullOrEmpty(row.VrcaPath) && File.Exists(row.VrcaPath);
                card.Selected.Enabled = usable;
                card.Selected.Checked = usable;
                card.Upload.Enabled = usable;
                card.Upload.Text = usable ? "上传" : "缺 PC";
                SetThumbnail(card, row.ImagePath);
                LayoutCard(card.Panel);
            }

            void SetThumbnail(ScanAssetCard card, string path)
            {
                var sourcePath = File.Exists(path) ? path : (File.Exists(_defaultImage) ? _defaultImage : null);
                if (string.Equals(card.ThumbnailSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    TryLoadThumbnail(card);
                    return;
                }
                card.ThumbnailSourcePath = sourcePath;
                card.ThumbnailPath = null;
                card.ThumbnailLoaded = false;
                try
                {
                    card.Thumbnail.CancelAsync();
                    card.Thumbnail.ImageLocation = null;
                    var old = card.Thumbnail.Image;
                    card.Thumbnail.Image = null;
                    if (old != null) old.Dispose();
                }
                catch { }
                TryLoadThumbnail(card);
            }

            void LoadVisibleThumbnails(FlowLayoutPanel flow)
            {
                if (flow == null || !flow.Visible) return;
                foreach (Control control in flow.Controls)
                {
                    var panel = control as Panel;
                    var card = panel == null ? null : panel.Tag as ScanAssetCard;
                    if (card != null) TryLoadThumbnail(card);
                }
            }

            void TryLoadThumbnail(ScanAssetCard card)
            {
                if (card == null || card.ThumbnailLoaded || string.IsNullOrEmpty(card.ThumbnailSourcePath)) return;
                var cachedPath = ThumbnailCachePath(card.ThumbnailSourcePath);
                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                {
                    if (LoadCachedThumbnail(card, cachedPath)) return;
                    try { File.Delete(cachedPath); } catch { }
                }
                QueueThumbnailBuild(card, card.ThumbnailSourcePath, cachedPath);
            }

            bool LoadCachedThumbnail(ScanAssetCard card, string cachedPath)
            {
                try
                {
                    Bitmap bitmap;
                    using (var stream = new FileStream(cachedPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var image = Image.FromStream(stream, true, true))
                        bitmap = new Bitmap(image);
                    var old = card.Thumbnail.Image;
                    card.Thumbnail.ImageLocation = null;
                    card.Thumbnail.Image = bitmap;
                    if (old != null) old.Dispose();
                    card.ThumbnailPath = cachedPath;
                    card.ThumbnailLoaded = true;
                    return true;
                }
                catch
                {
                    card.ThumbnailLoaded = false;
                    return false;
                }
            }

            void QueueThumbnailBuild(ScanAssetCard card, string sourcePath, string cachedPath)
            {
                if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(cachedPath)) return;
                var startBuild = false;
                lock (_thumbnailLock)
                {
                    List<ScanAssetCard> waiters;
                    if (!_thumbnailBuilds.TryGetValue(cachedPath, out waiters))
                    {
                        waiters = new List<ScanAssetCard>();
                        _thumbnailBuilds[cachedPath] = waiters;
                        startBuild = true;
                    }
                    if (!waiters.Contains(card)) waiters.Add(card);
                }
                if (!startBuild) return;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var built = false;
                    List<ScanAssetCard> waiters;
                    ThumbnailWorkers.Wait();
                    try
                    {
                        BuildThumbnail(sourcePath, cachedPath);
                        built = File.Exists(cachedPath);
                    }
                    catch { }
                    finally
                    {
                        ThumbnailWorkers.Release();
                        lock (_thumbnailLock)
                        {
                            if (!_thumbnailBuilds.TryGetValue(cachedPath, out waiters))
                                waiters = new List<ScanAssetCard>();
                            _thumbnailBuilds.Remove(cachedPath);
                        }
                    }
                    if (!built || IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            foreach (var waitingCard in waiters)
                            {
                                if (waitingCard.Panel == null || waitingCard.Panel.IsDisposed ||
                                    !string.Equals(waitingCard.ThumbnailSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                LoadCachedThumbnail(waitingCard, cachedPath);
                            }
                            LoadVisibleThumbnails(_modelFlow);
                            LoadVisibleThumbnails(_worldFlow);
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
            }

            static string ThumbnailCachePath(string sourcePath)
            {
                try
                {
                    var info = new FileInfo(sourcePath);
                    if (!info.Exists) return null;
                    var signature = info.FullName.ToLowerInvariant() + "|" + info.Length + "|" +
                        info.LastWriteTimeUtc.Ticks;
                    byte[] digest;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        digest = sha.ComputeHash(Encoding.UTF8.GetBytes(signature));
                    var name = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant() + ".jpg";
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                    return Path.Combine(local, "Mianchuan.VrcHotUpload", "thumbnail-cache", name);
                }
                catch { return null; }
            }

            static void BuildThumbnail(string sourcePath, string cachedPath)
            {
                var directory = Path.GetDirectoryName(cachedPath);
                Directory.CreateDirectory(directory);
                var temp = cachedPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var source = LoadThumbnailSource(sourcePath))
                    using (var target = new Bitmap(176, 176, PixelFormat.Format24bppRgb))
                    using (var graphics = Graphics.FromImage(target))
                    {
                        graphics.Clear(Color.FromArgb(8, 12, 20));
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighSpeed;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighSpeed;
                        var scale = Math.Min(176d / source.Width, 176d / source.Height);
                        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                        var x = (176 - width) / 2;
                        var y = (176 - height) / 2;
                        graphics.DrawImage(source, new Rectangle(x, y, width, height));
                        target.Save(temp, ImageFormat.Jpeg);
                    }
                    if (!File.Exists(cachedPath)) File.Move(temp, cachedPath);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }

            static Image LoadThumbnailSource(string sourcePath)
            {
                try
                {
                    using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var image = Image.FromStream(stream, true, true))
                        return new Bitmap(image);
                }
                catch
                {
                    // System.Drawing on .NET Framework cannot decode WebP. Windows Imaging
                    // Component can use the Microsoft WebP codec already present on Windows.
                    using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count == 0) throw new InvalidDataException("图片没有可解码帧。");
                        var source = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                            decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
                        var bitmap = new Bitmap(source.PixelWidth, source.PixelHeight, PixelFormat.Format32bppPArgb);
                        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                        try
                        {
                            source.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0,
                                Math.Abs(data.Stride) * bitmap.Height, data.Stride);
                        }
                        finally { bitmap.UnlockBits(data); }
                        return bitmap;
                    }
                }
            }

            void PickCover(ScanAssetCard card)
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择封面图片";
                    dlg.Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|所有文件 (*.*)|*.*";
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    card.Row.ImagePath = dlg.FileName;
                    SetThumbnail(card, card.Row.ImagePath);
                }
            }

            void UploadOne(ScanAssetCard card)
            {
                if (!card.Upload.Enabled) return;
                var handler = UploadRequested;
                if (handler != null) handler(new List<BatchUploadSelection> { CloneRow(card.Row) });
            }

            void UploadChecked()
            {
                var selected = new List<BatchUploadSelection>();
                foreach (var card in _cards)
                    if (card.Selected.Enabled && card.Selected.Checked) selected.Add(CloneRow(card.Row));
                if (selected.Count == 0)
                {
                    MessageBox.Show(FindForm(), "请先勾选需要上传的模型或世界。", "未选择文件");
                    return;
                }
                var handler = UploadRequested;
                if (handler != null) handler(selected);
            }

            BatchUploadSelection CloneRow(BatchUploadSelection row)
            {
                return new BatchUploadSelection
                {
                    AssetType = row.AssetType, VrcaPath = row.VrcaPath, QuestVrcwPath = row.QuestVrcwPath,
                    ImagePath = row.ImagePath, DisplayName = row.DisplayName, PlatformLabel = row.PlatformLabel,
                    WorldId = row.WorldId, PairKey = row.PairKey, CoverKey = row.CoverKey, Description = row.Description
                };
            }

            void SetAllChecked(bool value)
            {
                foreach (var card in _cards) if (card.Selected.Enabled) card.Selected.Checked = value;
                UpdateSummary();
            }

            void UpdateSummary()
            {
                var models = 0;
                var worlds = 0;
                var selected = 0;
                foreach (var card in _cards)
                {
                    if (card.Row.AssetType == "地图") worlds++; else models++;
                    if (card.Selected.Enabled && card.Selected.Checked) selected++;
                }
                _summary.Text = "模型 " + models + " 个 · 世界 " + worlds + " 个 · 已选 " + selected +
                    (_scanRunning ? " · 正在扫描..." : "");
                _uploadSelected.Enabled = selected > 0;
            }

            static long SafeLength(string path)
            {
                try { return string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length; }
                catch { return 0; }
            }

            static string FormatSize(long bytes)
            {
                if (bytes >= 1024L * 1024L * 1024L) return (bytes / (1024d * 1024d * 1024d)).ToString("0.0") + " GB";
                if (bytes >= 1024L * 1024L) return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
                if (bytes >= 1024L) return (bytes / 1024d).ToString("0.0") + " KB";
                return bytes + " B";
            }

            class ScanAssetCard
            {
                public BatchUploadSelection Row;
                public Panel Panel;
                public PictureBox Thumbnail;
                public Label NameLabel;
                public Label DescriptionLabel;
                public CheckBox Selected;
                public Button Upload;
                public Button Cover;
                public string ThumbnailSourcePath;
                public string ThumbnailPath;
                public bool ThumbnailLoaded;
            }
        }

        class VrcaBatchSelectDialog : Form
        {
            readonly List<BatchUploadSelection> _files = new List<BatchUploadSelection>();
            readonly string _rootFolder;
            readonly string _defaultImagePath;
            readonly ListView _modelList;
            readonly ListView _worldList;
            readonly Label _title;
            readonly Label _summary;
            readonly Button _addButton;
            readonly Button _cancelButton;
            readonly Queue<BatchUploadSelection> _pendingRows = new Queue<BatchUploadSelection>();
            readonly object _pendingRowsLock = new object();
            bool _scanRunning;
            volatile bool _scanCancelled;
            bool _drainScheduled;
            bool _completionRequested;
            Exception _completionError;

            public List<BatchUploadSelection> SelectedFiles { get; private set; }
            public bool ScanCancelled { get { return _scanCancelled; } }

            public VrcaBatchSelectDialog(List<BatchUploadSelection> files, string rootFolder,
                string defaultImagePath, bool liveScan)
            {
                _rootFolder = rootFolder ?? "";
                _defaultImagePath = defaultImagePath ?? "";
                _scanRunning = liveScan;
                SelectedFiles = new List<BatchUploadSelection>();

                Text = "文件夹扫描 · 模型与世界";
                StartPosition = FormStartPosition.CenterParent;
                Size = new Size(1040, 680);
                MinimumSize = new Size(900, 560);
                BackColor = Bg;
                ForeColor = TextMain;
                Font = UiFont(9f);
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;
                MinimizeBox = false;

                _title = new Label
                {
                    Text = liveScan ? "正在扫描，结果会逐项出现..." : "扫描结果",
                    Location = new Point(24, 20),
                    Size = new Size(560, 30),
                    ForeColor = TextMain,
                    BackColor = Color.Transparent,
                    Font = UiFont(13f, FontStyle.Bold)
                };
                Controls.Add(_title);

                _summary = new Label
                {
                    Location = new Point(24, 54),
                    Size = new Size(640, 24),
                    ForeColor = TextMuted,
                    BackColor = Color.Transparent
                };
                Controls.Add(_summary);

                var selectAll = DialogButton("全选", 730, 24, 82, 34, Accent2, Color.FromArgb(10, 26, 34));
                selectAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                selectAll.Click += delegate { SetAllChecked(true); };
                Controls.Add(selectAll);

                var selectNone = DialogButton("全不选", 820, 24, 82, 34, Card2, TextMain);
                selectNone.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                selectNone.Click += delegate { SetAllChecked(false); };
                Controls.Add(selectNone);

                var invert = DialogButton("反选", 910, 24, 82, 34, Card2, TextMain);
                invert.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                invert.Click += delegate { InvertChecked(); };
                Controls.Add(invert);

                Controls.Add(SectionTitle("模型 VRCA", Accent, 24));
                Controls.Add(SectionTitle("世界 VRCW · PC / Quest", Accent2, 520));

                _modelList = CreateResultList(new Point(24, 120), new Size(472, 440));
                _modelList.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom;
                _modelList.Columns.Add("名称", 220);
                _modelList.Columns.Add("封面", 145);
                _modelList.Columns.Add("大小", 80);
                Controls.Add(_modelList);

                _worldList = CreateResultList(new Point(520, 120), new Size(472, 440));
                _worldList.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
                _worldList.Columns.Add("名称", 190);
                _worldList.Columns.Add("平台", 92);
                _worldList.Columns.Add("封面", 115);
                _worldList.Columns.Add("大小", 70);
                Controls.Add(_worldList);

                if (files != null)
                    foreach (var row in files) AddScannedFileCore(row);

                var cover = DialogButton("设置封面", 24, 584, 104, 38, Accent2, Color.FromArgb(10, 26, 34));
                cover.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
                cover.Click += delegate { PickCoverForSelected(); };
                Controls.Add(cover);

                var defaultCover = DialogButton("用默认封面", 138, 584, 118, 38, Card2, TextMain);
                defaultCover.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
                defaultCover.Click += delegate { ResetCoverForSelected(); };
                Controls.Add(defaultCover);

                _addButton = DialogButton("加入上传", 762, 584, 110, 38, Accent, Color.FromArgb(12, 28, 20));
                _addButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                _addButton.Click += delegate { ConfirmSelection(); };
                Controls.Add(_addButton);

                _cancelButton = DialogButton(liveScan ? "停止并关闭" : "取消", 882, 584, 110, 38, Card2, TextMain);
                _cancelButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                _cancelButton.Click += delegate { _scanCancelled = true; DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(_cancelButton);

                AcceptButton = _addButton;
                CancelButton = _cancelButton;
                FormClosed += delegate { _scanCancelled = true; };
                UpdateSummary();
            }

            Label SectionTitle(string text, Color color, int x)
            {
                return new Label
                {
                    Text = text, Location = new Point(x, 88), Size = new Size(460, 26),
                    ForeColor = color, BackColor = Color.Transparent,
                    Font = UiFont(11f, FontStyle.Bold)
                };
            }

            ListView CreateResultList(Point location, Size size)
            {
                var list = new ListView
                {
                    Location = location, Size = size, View = View.Details, CheckBoxes = true,
                    FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle,
                    BackColor = HotUploadPreview.Theme.Input, ForeColor = TextMain,
                    GridLines = false, ShowItemToolTips = true
                };
                list.ItemChecked += delegate { UpdateSummary(); };
                list.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
                {
                    var row = list.Items[e.Index].Tag as BatchUploadSelection;
                    if (row != null && string.IsNullOrEmpty(row.VrcaPath)) e.NewValue = CheckState.Unchecked;
                };
                list.DoubleClick += delegate { PickCoverForSelected(); };
                return list;
            }

            public void AddScannedFile(BatchUploadSelection row)
            {
                if (row == null || _scanCancelled || IsDisposed) return;
                var schedule = false;
                lock (_pendingRowsLock)
                {
                    _pendingRows.Enqueue(row);
                    if (!_drainScheduled)
                    {
                        _drainScheduled = true;
                        schedule = true;
                    }
                }
                if (!schedule) return;
                try
                {
                    BeginInvoke(new Action(DrainPendingRows));
                }
                catch (InvalidOperationException) { }
            }

            void DrainPendingRows()
            {
                if (_scanCancelled || IsDisposed) return;
                var batch = new List<BatchUploadSelection>();
                var hasMore = false;
                var finish = false;
                Exception finishError = null;
                lock (_pendingRowsLock)
                {
                    while (_pendingRows.Count > 0 && batch.Count < 48)
                        batch.Add(_pendingRows.Dequeue());
                    hasMore = _pendingRows.Count > 0;
                    if (!hasMore)
                    {
                        _drainScheduled = false;
                        finish = _completionRequested;
                        finishError = _completionError;
                    }
                }

                _modelList.BeginUpdate();
                _worldList.BeginUpdate();
                try { foreach (var row in batch) AddScannedFileCore(row); }
                finally
                {
                    _modelList.EndUpdate();
                    _worldList.EndUpdate();
                }
                UpdateSummary();

                if (hasMore)
                {
                    try { BeginInvoke(new Action(DrainPendingRows)); }
                    catch (InvalidOperationException) { }
                }
                else if (finish)
                {
                    FinishScanComplete(finishError);
                }
            }

            void AddScannedFileCore(BatchUploadSelection row)
            {
                if (row == null || _scanCancelled) return;
                BatchUploadSelection merge = null;
                if (row.AssetType == "地图" && !string.IsNullOrEmpty(row.WorldId))
                {
                    merge = _files.Find(delegate(BatchUploadSelection current)
                    {
                        if (current.AssetType != "地图" ||
                            !string.Equals(current.WorldId, row.WorldId, StringComparison.OrdinalIgnoreCase)) return false;
                        return (!string.IsNullOrEmpty(row.VrcaPath) && string.IsNullOrEmpty(current.VrcaPath)) ||
                            (!string.IsNullOrEmpty(row.QuestVrcwPath) && string.IsNullOrEmpty(current.QuestVrcwPath));
                    });
                }
                if (merge != null)
                {
                    if (!string.IsNullOrEmpty(row.VrcaPath))
                    {
                        merge.VrcaPath = row.VrcaPath;
                        merge.DisplayName = row.DisplayName;
                        merge.ImagePath = row.ImagePath;
                    }
                    if (!string.IsNullOrEmpty(row.QuestVrcwPath)) merge.QuestVrcwPath = row.QuestVrcwPath;
                    merge.PlatformLabel = !string.IsNullOrEmpty(merge.VrcaPath) && !string.IsNullOrEmpty(merge.QuestVrcwPath)
                        ? "PC + Quest" : (!string.IsNullOrEmpty(merge.VrcaPath) ? "仅 PC" : row.PlatformLabel);
                    foreach (ListViewItem existing in _worldList.Items)
                    {
                        if (!object.ReferenceEquals(existing.Tag, merge)) continue;
                        existing.Text = merge.DisplayName;
                        existing.SubItems[1].Text = merge.PlatformLabel;
                        existing.SubItems[2].Text = CoverDisplayName(merge.ImagePath);
                        existing.SubItems[3].Text = FormatSize(SafeFileLength(merge.VrcaPath) + SafeFileLength(merge.QuestVrcwPath));
                        existing.ToolTipText = !string.IsNullOrEmpty(merge.VrcaPath) ? merge.VrcaPath : merge.QuestVrcwPath;
                        existing.ForeColor = string.IsNullOrEmpty(merge.VrcaPath) ? Warn : TextMain;
                        if (!string.IsNullOrEmpty(merge.VrcaPath)) existing.Checked = true;
                        break;
                    }
                    return;
                }
                _files.Add(row);
                var primary = !string.IsNullOrEmpty(row.VrcaPath) ? row.VrcaPath : row.QuestVrcwPath;
                if (string.IsNullOrEmpty(row.ImagePath)) row.ImagePath = _defaultImagePath;
                ListViewItem item;
                if (row.AssetType == "地图")
                {
                    item = new ListViewItem(row.DisplayName ?? Path.GetFileNameWithoutExtension(primary));
                    item.SubItems.Add(row.PlatformLabel ?? "平台未知");
                    item.SubItems.Add(CoverDisplayName(row.ImagePath));
                    item.SubItems.Add(FormatSize(SafeFileLength(row.VrcaPath) + SafeFileLength(row.QuestVrcwPath)));
                    _worldList.Items.Add(item);
                }
                else
                {
                    item = new ListViewItem(row.DisplayName ?? Path.GetFileNameWithoutExtension(primary));
                    item.SubItems.Add(CoverDisplayName(row.ImagePath));
                    item.SubItems.Add(FormatSize(SafeFileLength(row.VrcaPath)));
                    _modelList.Items.Add(item);
                }
                item.Tag = row;
                item.ToolTipText = primary ?? "";
                item.Checked = !string.IsNullOrEmpty(row.VrcaPath);
                if (string.IsNullOrEmpty(row.VrcaPath)) item.ForeColor = Warn;
            }

            public void MarkScanComplete(Exception error)
            {
                if (_scanCancelled || IsDisposed) return;
                var schedule = false;
                lock (_pendingRowsLock)
                {
                    _completionRequested = true;
                    _completionError = error;
                    if (!_drainScheduled)
                    {
                        _drainScheduled = true;
                        schedule = true;
                    }
                }
                if (!schedule) return;
                try
                {
                    BeginInvoke(new Action(DrainPendingRows));
                }
                catch (InvalidOperationException) { }
            }

            void FinishScanComplete(Exception error)
            {
                _scanRunning = false;
                _title.Text = error != null ? "扫描结束，部分文件读取失败" :
                    (_files.Count == 0 ? "扫描完成，没有找到 VRCA / VRCW" : "扫描完成");
                _summary.ForeColor = error == null ? TextMuted : Warn;
                _cancelButton.Text = "关闭";
                UpdateSummary();
            }

            IEnumerable<ListView> Lists()
            {
                yield return _modelList;
                yield return _worldList;
            }

            Button DialogButton(string text, int x, int y, int w, int h, Color back, Color fore)
            {
                var btn = new Button
                {
                    Text = text,
                    Location = new Point(x, y),
                    Size = new Size(w, h),
                    BackColor = back,
                    ForeColor = fore,
                    FlatStyle = FlatStyle.Flat,
                    Font = UiFont(9.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Color.FromArgb(72, back);
                return btn;
            }

            void SetAllChecked(bool value)
            {
                foreach (var list in Lists())
                {
                    list.BeginUpdate();
                    foreach (ListViewItem item in list.Items)
                    {
                        var row = item.Tag as BatchUploadSelection;
                        item.Checked = value && row != null && !string.IsNullOrEmpty(row.VrcaPath);
                    }
                    list.EndUpdate();
                }
                UpdateSummary();
            }

            void InvertChecked()
            {
                foreach (var list in Lists())
                    foreach (ListViewItem item in list.Items)
                    {
                        var row = item.Tag as BatchUploadSelection;
                        if (row != null && !string.IsNullOrEmpty(row.VrcaPath)) item.Checked = !item.Checked;
                    }
                UpdateSummary();
            }

            void PickCoverForSelected()
            {
                var targets = SelectedRows();
                if (targets.Count == 0)
                {
                    MessageBox.Show(this, "请先选中需要设置封面的项目。", "未选择项目");
                    return;
                }

                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择封面图片";
                    dlg.Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|所有文件 (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    foreach (var item in targets)
                    {
                        var row = item.Tag as BatchUploadSelection;
                        if (row == null) continue;
                        row.ImagePath = dlg.FileName;
                        SetCoverText(item, row);
                    }
                }
            }

            void ResetCoverForSelected()
            {
                var targets = SelectedRows();
                if (targets.Count == 0)
                {
                    MessageBox.Show(this, "请先选中需要恢复默认封面的项目。", "未选择项目");
                    return;
                }

                foreach (var item in targets)
                {
                    var row = item.Tag as BatchUploadSelection;
                    if (row == null) continue;
                    row.ImagePath = _defaultImagePath;
                    SetCoverText(item, row);
                }
            }

            List<ListViewItem> SelectedRows()
            {
                var result = new List<ListViewItem>();
                foreach (var list in Lists())
                    foreach (ListViewItem item in list.SelectedItems) result.Add(item);
                return result;
            }

            void UpdateSummary()
            {
                var selected = 0;
                foreach (var list in Lists())
                    foreach (ListViewItem item in list.Items) if (item.Checked) selected++;
                _summary.Text = "模型 " + _modelList.Items.Count + " 个  ·  世界 " + _worldList.Items.Count +
                    " 个  ·  已选择 " + selected + " 项" + (_scanRunning ? "  ·  正在扫描..." : "");
                if (_addButton != null) _addButton.Enabled = !_scanRunning && selected > 0;
            }

            void ConfirmSelection()
            {
                if (_scanRunning) return;
                SelectedFiles = new List<BatchUploadSelection>();
                foreach (var list in Lists())
                    foreach (ListViewItem item in list.Items)
                    {
                        if (!item.Checked) continue;
                        var row = item.Tag as BatchUploadSelection;
                        if (row == null || string.IsNullOrEmpty(row.VrcaPath) || !File.Exists(row.VrcaPath)) continue;
                        SelectedFiles.Add(new BatchUploadSelection
                        {
                            AssetType = row.AssetType,
                            VrcaPath = row.VrcaPath,
                            QuestVrcwPath = File.Exists(row.QuestVrcwPath) ? row.QuestVrcwPath : null,
                            ImagePath = File.Exists(row.ImagePath) ? row.ImagePath : _defaultImagePath,
                            DisplayName = row.DisplayName,
                            PlatformLabel = row.PlatformLabel,
                            WorldId = row.WorldId
                        });
                    }

                if (SelectedFiles.Count == 0)
                {
                    MessageBox.Show(this, "没有选择可上传的模型或带 PC 包的地图。", "未选择文件");
                    return;
                }

                _scanCancelled = true;
                DialogResult = DialogResult.OK;
                Close();
            }

            void SetCoverText(ListViewItem item, BatchUploadSelection row)
            {
                var index = row.AssetType == "地图" ? 2 : 1;
                while (item.SubItems.Count <= index) item.SubItems.Add("");
                item.SubItems[index].Text = CoverDisplayName(row.ImagePath);
            }

            string CoverDisplayName(string imagePath)
            {
                if (!string.IsNullOrEmpty(imagePath) &&
                    !string.Equals(imagePath, _defaultImagePath, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(imagePath);
                return "默认封面";
            }

            string DisplayFolder(string file)
            {
                try
                {
                    var dir = Path.GetDirectoryName(file) ?? "";
                    var root = Path.GetFullPath(_rootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (fullDir.Equals(root, StringComparison.OrdinalIgnoreCase))
                        return ".";
                    if (fullDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return "." + fullDir.Substring(root.Length);
                    return dir;
                }
                catch
                {
                    return Path.GetDirectoryName(file) ?? "";
                }
            }

            static long SafeFileLength(string file)
            {
                try { return new FileInfo(file).Length; } catch { return 0; }
            }

            static string FormatSize(long bytes)
            {
                if (bytes < 1024) return bytes + " B";
                var kb = bytes / 1024.0;
                if (kb < 1024) return kb.ToString("F1") + " KB";
                var mb = kb / 1024.0;
                if (mb < 1024) return mb.ToString("F1") + " MB";
                return (mb / 1024.0).ToString("F2") + " GB";
            }
        }

        partial class DownloadDialog : Form
        {
            readonly MainForm _owner;
            readonly string _baseDir;
            readonly List<VrcaClient.VrcaFileEntry> _allFiles = new List<VrcaClient.VrcaFileEntry>();

            TextBox _searchBox;
            ListView _list;
            Button _refreshBtn;
            Button _selectAllBtn;
            Button _clearSelectionBtn;
            Button _downloadBtn;
            Label _status;
            ProgressStrip _progress;
            Panel _previewCard;
            PictureBox _previewImage;
            Label _previewName;
            Label _previewMeta;
            Label _previewDescription;
            Label _previewHint;
            readonly Dictionary<string, Image> _previewCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
            Image _previewPlaceholder;
            int _previewRequestVersion;
            bool _busy;

            public DownloadDialog(MainForm owner, string baseDir)
            {
                _owner = owner;
                _baseDir = baseDir;

                Text = "模型下载";
                StartPosition = FormStartPosition.CenterParent;
                Size = new Size(1120, 680);
                MinimumSize = new Size(1000, 680);
                BackColor = Bg;
                ForeColor = TextMain;
                Font = UiFont(9.5f);

                BuildView();
                Shown += delegate { if (!WorkspaceSelfTest && _owner._loggedIn) RefreshFiles(); };
                FormClosed += delegate { DisposePreviewImages(); };
            }





            static Bitmap CreatePreviewPlaceholder(int width, int height)
            {
                var bitmap = new Bitmap(width, height);
                using (var graphics = Graphics.FromImage(bitmap))
                using (var gradient = new LinearGradientBrush(
                    new Rectangle(0, 0, width, height),
                    Color.FromArgb(14, 22, 34),
                    Color.FromArgb(7, 38, 42),
                    25f))
                using (var linePen = new Pen(Color.FromArgb(40, 73, 91), 1f))
                using (var font = UiFont(11f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(126, 174, 187)))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.FillRectangle(gradient, 0, 0, width, height);
                    for (var x = -height; x < width; x += 28)
                        graphics.DrawLine(linePen, x, height, x + height, 0);
                    var text = "AVATAR PREVIEW";
                    var size = graphics.MeasureString(text, font);
                    graphics.DrawString(text, font, brush, (width - size.Width) / 2f, (height - size.Height) / 2f);
                }
                return bitmap;
            }

            TextBox MakeDialogTextBox(int x, int y, int w)
            {
                return new TextBox
                {
                    Location = new Point(x, y),
                    Size = new Size(w, 30),
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = HotUploadPreview.Theme.Input,
                    ForeColor = TextMain,
                    Font = UiFont(10f)
                };
            }

            void RefreshFiles()
            {
                if (_busy) return;
                SetBusy(true);
                SetStatus("正在获取模型列表...");
                SetProgress(0);

                var thread = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        var client = new VrcaClient(_baseDir, delegate(string message) { });
                        var files = client.ListVrcaFiles();
                        OnDialogUi(delegate
                        {
                            _allFiles.Clear();
                            _allFiles.AddRange(files);
                            ApplyFilter();
                            var coverCount = 0;
                            foreach (var entry in _allFiles)
                                if (!string.IsNullOrEmpty(entry.ImageUrl) || !string.IsNullOrEmpty(entry.ThumbnailImageUrl))
                                    coverCount++;
                            SetStatus("已找到 " + _allFiles.Count + " 个可下载模型，其中 " + coverCount + " 个可使用原始封面。");
                            SetProgress(0);
                        });
                    }
                    catch (VrcaClient.NotLoggedInException)
                    {
                        OnDialogUi(delegate
                        {
                            SetStatus("登录已过期，请重新登录。");
                            MessageBox.Show(this, "登录已过期，请重新登录后再下载。", "需要登录");
                            Close();
                            _owner.MarkLoggedOut("登录已过期，请重新登录。");
                            _owner.ShowLoginView();
                        });
                    }
                    catch (Exception ex)
                    {
                        OnDialogUi(delegate
                        {
                            SetStatus("获取列表失败，请稍后重试。");
                            MessageBox.Show(this, "获取模型列表失败：\n" + ex.Message, "失败");
                        });
                    }
                    finally
                    {
                        OnDialogUi(delegate { SetBusy(false); });
                    }
                }));
                thread.IsBackground = true;
                thread.Start();
            }

            void ApplyFilter()
            {
                if (_list == null) return;
                var keyword = _searchBox == null ? "" : (_searchBox.Text ?? "").Trim();
                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    foreach (var entry in _allFiles)
                    {
                        var displayName = FirstNonEmpty(entry.AvatarName, entry.Name);
                        if (!string.IsNullOrEmpty(keyword) &&
                            (displayName ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                            (entry.Name ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                            (entry.AvatarId ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                            (entry.Description ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var item = new ListViewItem(displayName ?? "VRCA");
                        item.SubItems.Add(entry.Version > 0 ? "v" + entry.Version.ToString() : "-");
                        item.SubItems.Add(FormatBytes(entry.SizeBytes));
                        item.SubItems.Add(FormatTime(FirstNonEmpty(entry.CreatedAt, entry.UpdatedAt)));
                        item.SubItems.Add(string.IsNullOrEmpty(entry.ImageUrl) && string.IsNullOrEmpty(entry.ThumbnailImageUrl)
                            ? "默认封面" : "原始封面");
                        item.Tag = entry;
                        _list.Items.Add(item);
                    }
                }
                finally
                {
                    _list.EndUpdate();
                }
                ResizeColumns();
                if (_list.Items.Count > 0 && _list.SelectedItems.Count == 0)
                    _list.Items[0].Selected = true;
                UpdateButtons();
            }

            void StartDownload()
            {
                if (_busy) return;
                var entries = SelectedEntries();
                if (entries.Count == 0)
                {
                    MessageBox.Show(this, "请先选择一个或多个模型。", "未选择模型");
                    return;
                }

                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择模型和封面的保存文件夹";
                    dlg.ShowNewFolderButton = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    var outputDirectory = dlg.SelectedPath;
                    SetBusy(true);
                    SetStatus("准备下载 " + entries.Count + " 个模型...");
                    SetProgress(1);

                    var thread = new Thread(new ThreadStart(delegate
                    {
                        try
                        {
                            DownloadEntries(entries, outputDirectory);
                        }
                        catch (Exception ex)
                        {
                            OnDialogUi(delegate
                            {
                                SetProgress(0);
                                SetStatus("下载失败，请稍后重试。");
                                MessageBox.Show(this, "下载失败：\n" + ex.Message, "失败");
                            });
                        }
                        finally
                        {
                            OnDialogUi(delegate { SetBusy(false); });
                        }
                    }));
                    thread.IsBackground = true;
                    thread.Start();
                }
            }

            void DownloadEntries(List<VrcaClient.VrcaFileEntry> entries, string outputDirectory)
            {
                Directory.CreateDirectory(outputDirectory);
                var targets = BuildDownloadTargets(entries, outputDirectory);
                var percentages = new int[entries.Count];
                var progressLock = new object();
                var resultLock = new object();
                var failures = new List<string>();
                var coverWarnings = new List<string>();
                var nextIndex = -1;
                var completed = 0;
                var succeeded = 0;
                var coversSaved = 0;
                var defaultCoversSaved = 0;
                var metadataSaved = 0;
                var loginExpired = 0;
                var lastOverall = -1;

                Action<int, int> reportProgress = delegate(int index, int value)
                {
                    int overall;
                    int finished;
                    lock (progressLock)
                    {
                        percentages[index] = Math.Max(percentages[index], Math.Max(0, Math.Min(100, value)));
                        var sum = 0;
                        foreach (var percent in percentages) sum += percent;
                        overall = percentages.Length == 0 ? 0 : sum / percentages.Length;
                        finished = completed;
                        if (overall == lastOverall) return;
                        lastOverall = overall;
                    }
                    OnDialogUi(delegate
                    {
                        SetProgress(overall);
                        SetStatus("正在下载 " + entries.Count + " 个模型，已完成 " + finished + "/" + entries.Count + "  " + overall + "%");
                    });
                };

                var workerCount = Math.Min(3, entries.Count);
                var workers = new Thread[workerCount];
                for (var worker = 0; worker < workerCount; worker++)
                {
                    workers[worker] = new Thread(new ThreadStart(delegate
                    {
                        while (Volatile.Read(ref loginExpired) == 0)
                        {
                            var index = Interlocked.Increment(ref nextIndex);
                            if (index >= entries.Count) break;

                            var entry = entries[index];
                            var displayName = FirstNonEmpty(entry.AvatarName, entry.Name);
                            try
                            {
                                var client = new VrcaClient(_baseDir, delegate(string message) { });
                                client.DownloadVrcaFile(entry, targets[index], delegate(long done, long total)
                                {
                                    var percent = total > 0 ? (int)Math.Round(done * 92.0 / total) : 45;
                                    reportProgress(index, percent);
                                });

                                reportProgress(index, 94);
                                var coverPath = Path.ChangeExtension(targets[index], ".png");
                                var originalCoverSaved = false;
                                try
                                {
                                    var imageBytes = client.DownloadVrcaImageBytes(entry, true);
                                    if (imageBytes != null && imageBytes.Length > 0)
                                    {
                                        SaveImageAsPng(imageBytes, coverPath);
                                        originalCoverSaved = true;
                                    }
                                }
                                catch (VrcaClient.NotLoggedInException) { throw; }
                                catch (Exception ex)
                                {
                                    lock (resultLock) coverWarnings.Add(displayName + "：原始封面读取失败，已改用默认封面（" + ex.Message + "）");
                                }

                                if (!originalCoverSaved)
                                {
                                    SaveDefaultCoverAsPng(coverPath, displayName);
                                    Interlocked.Increment(ref defaultCoversSaved);
                                }
                                Interlocked.Increment(ref coversSaved);

                                if (SaveMetadataText(entry, targets[index]))
                                    Interlocked.Increment(ref metadataSaved);

                                Interlocked.Increment(ref succeeded);
                            }
                            catch (VrcaClient.NotLoggedInException)
                            {
                                Interlocked.Exchange(ref loginExpired, 1);
                                lock (resultLock) failures.Add(displayName + "：登录已过期");
                                CleanupEmptyDownloadDirectory(targets[index]);
                            }
                            catch (Exception ex)
                            {
                                lock (resultLock) failures.Add(displayName + "：" + ex.Message);
                                CleanupEmptyDownloadDirectory(targets[index]);
                            }
                            finally
                            {
                                Interlocked.Increment(ref completed);
                                reportProgress(index, 100);
                            }
                        }
                    }));
                    workers[worker].IsBackground = true;
                    workers[worker].Start();
                }

                foreach (var worker in workers) worker.Join();

                OnDialogUi(delegate
                {
                    SetProgress(entries.Count > 0 && succeeded == entries.Count ? 100 : Math.Max(0, lastOverall));
                    if (loginExpired != 0)
                    {
                        SetStatus("登录已过期，请重新登录。");
                        MessageBox.Show(this, "下载过程中登录已过期，请重新登录后继续。", "需要登录");
                        Close();
                        _owner.MarkLoggedOut("登录已过期，请重新登录。");
                        _owner.ShowLoginView();
                        return;
                    }

                    var summary = "模型下载完成：" + succeeded + "/" + entries.Count +
                        "\n封面已保存：" + coversSaved + " 张（默认封面 " + defaultCoversSaved + " 张）" +
                        "\n简介资料已保存：" + metadataSaved + " 份" +
                        "\n保存位置：" + outputDirectory;
                    if (coverWarnings.Count > 0)
                        summary += "\n\n有 " + coverWarnings.Count + " 个原始封面不可用，已自动生成默认封面。";
                    if (failures.Count > 0)
                        summary += "\n\n失败 " + failures.Count + " 个：\n" + string.Join("\n", failures.ToArray());

                    SetStatus(failures.Count == 0 ? "下载完成，模型与封面已保存。" : "部分模型下载失败，请查看提示。");
                    MessageBox.Show(this, summary, failures.Count == 0 ? "下载完成" : "部分完成");
                });
            }

            static List<string> BuildDownloadTargets(List<VrcaClient.VrcaFileEntry> entries, string outputDirectory)
            {
                var targets = new List<string>();
                var reservedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    var name = FirstNonEmpty(entry.AvatarName, entry.Name);
                    var version = entry.Version > 0 ? "_v" + entry.Version : "_latest";
                    var baseName = SafeBaseFileName(name) + version;
                    var folderName = baseName;
                    var modelDirectory = Path.Combine(outputDirectory, folderName);
                    var suffix = 2;
                    while (reservedDirectories.Contains(modelDirectory) || Directory.Exists(modelDirectory))
                    {
                        folderName = baseName + "_" + suffix;
                        modelDirectory = Path.Combine(outputDirectory, folderName);
                        suffix++;
                    }
                    reservedDirectories.Add(modelDirectory);
                    Directory.CreateDirectory(modelDirectory);
                    targets.Add(Path.Combine(modelDirectory, folderName + ".vrca"));
                }
                return targets;
            }

            static void CleanupEmptyDownloadDirectory(string targetPath)
            {
                try
                {
                    var directory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory) &&
                        Directory.GetFileSystemEntries(directory).Length == 0)
                        Directory.Delete(directory, false);
                }
                catch { }
            }

            static void SaveImageAsPng(byte[] bytes, string path)
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var source = Image.FromStream(stream, true, true))
                    source.Save(path, ImageFormat.Png);
            }

            static void SaveDefaultCoverAsPng(string path, string modelName)
            {
                SaveWorkspaceCover(path);
            }

            static bool SaveMetadataText(VrcaClient.VrcaFileEntry entry, string vrcaPath)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Description)) return false;
                var text = "模型名称：" + FirstNonEmpty(entry.AvatarName, entry.Name) + "\r\n" +
                    (string.IsNullOrEmpty(entry.AvatarId) ? "" : "Avatar ID：" + entry.AvatarId + "\r\n") +
                    (string.IsNullOrEmpty(entry.ReleaseStatus) ? "" : "发布状态：" + entry.ReleaseStatus + "\r\n") +
                    "\r\n简介：\r\n" + entry.Description.Trim() + "\r\n";
                File.WriteAllText(Path.ChangeExtension(vrcaPath, ".txt"), text, Encoding.UTF8);
                return true;
            }

            VrcaClient.VrcaFileEntry SelectedEntry()
            {
                if (_list == null || _list.SelectedItems.Count == 0) return null;
                return _list.SelectedItems[0].Tag as VrcaClient.VrcaFileEntry;
            }

            List<VrcaClient.VrcaFileEntry> SelectedEntries()
            {
                var result = new List<VrcaClient.VrcaFileEntry>();
                if (_list == null) return result;
                if (_list.CheckedItems.Count > 0)
                {
                    foreach (ListViewItem item in _list.CheckedItems)
                    {
                        var entry = item.Tag as VrcaClient.VrcaFileEntry;
                        if (entry != null) result.Add(entry);
                    }
                }
                else
                {
                    foreach (ListViewItem item in _list.SelectedItems)
                    {
                        var entry = item.Tag as VrcaClient.VrcaFileEntry;
                        if (entry != null) result.Add(entry);
                    }
                }
                return result;
            }

            void SetAllItemsChecked(bool value)
            {
                if (_list == null || _busy) return;
                _list.BeginUpdate();
                try
                {
                    foreach (ListViewItem item in _list.Items)
                        item.Checked = value;
                }
                finally
                {
                    _list.EndUpdate();
                }
                UpdateButtons();
            }

            void ShowSelectedPreview()
            {
                var entry = SelectedEntry();
                var requestVersion = Interlocked.Increment(ref _previewRequestVersion);
                if (entry == null)
                {
                    SetPreviewImage(_previewPlaceholder);
                    if (_previewName != null) _previewName.Text = "尚未选择模型";
                    if (_previewMeta != null) _previewMeta.Text = "选择列表中的模型查看封面";
                    if (_previewDescription != null) _previewDescription.Text = "暂无简介";
                    if (_previewHint != null) _previewHint.Text = "下载时会同时保存同名 PNG 封面";
                    return;
                }

                var displayName = FirstNonEmpty(entry.AvatarName, entry.Name);
                if (_previewName != null) _previewName.Text = displayName;
                if (_previewMeta != null)
                {
                    var release = string.IsNullOrEmpty(entry.ReleaseStatus) ? "历史文件" : entry.ReleaseStatus;
                    _previewMeta.Text = release + "  ·  " + FormatBytes(entry.SizeBytes) +
                        (entry.Version > 0 ? "  ·  v" + entry.Version : "");
                }
                if (_previewDescription != null)
                    _previewDescription.Text = string.IsNullOrWhiteSpace(entry.Description)
                        ? "暂无简介" : entry.Description.Trim();

                var imageUrl = FirstNonEmpty(entry.ThumbnailImageUrl, entry.ImageUrl);
                if (string.IsNullOrEmpty(imageUrl))
                {
                    SetPreviewImage(_previewPlaceholder);
                    if (_previewHint != null) _previewHint.Text = "无原始封面，下载时生成默认封面";
                    return;
                }

                Image cached;
                if (_previewCache.TryGetValue(imageUrl, out cached))
                {
                    SetPreviewImage(cached);
                    if (_previewHint != null) _previewHint.Text = "下载时会同时保存同名 PNG 封面";
                    return;
                }

                SetPreviewImage(_previewPlaceholder);
                if (_previewHint != null) _previewHint.Text = "正在加载封面...";
                var thread = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        var client = new VrcaClient(_baseDir, delegate(string message) { });
                        var bytes = client.DownloadVrcaImageBytes(entry, false);
                        var image = DecodePreviewImage(bytes);
                        OnDialogUi(delegate
                        {
                            if (image == null)
                            {
                                if (requestVersion == _previewRequestVersion && _previewHint != null)
                                    _previewHint.Text = "封面无法读取";
                                return;
                            }

                            Image old;
                            if (_previewCache.TryGetValue(imageUrl, out old))
                            {
                                image.Dispose();
                                image = old;
                            }
                            else
                            {
                                _previewCache[imageUrl] = image;
                            }

                            if (requestVersion == _previewRequestVersion)
                            {
                                SetPreviewImage(image);
                                if (_previewHint != null) _previewHint.Text = "下载时会同时保存同名 PNG 封面";
                            }
                        });
                    }
                    catch
                    {
                        OnDialogUi(delegate
                        {
                            if (requestVersion == _previewRequestVersion && _previewHint != null)
                                _previewHint.Text = "封面加载失败，模型仍可下载";
                        });
                    }
                }));
                thread.IsBackground = true;
                thread.Start();
            }

            static Image DecodePreviewImage(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0) return null;
                using (var stream = new MemoryStream(bytes, false))
                using (var source = Image.FromStream(stream, true, true))
                    return new Bitmap(source);
            }

            void SetPreviewImage(Image image)
            {
                if (_previewImage != null) _previewImage.Image = image ?? _previewPlaceholder;
            }

            void DisposePreviewImages()
            {
                Interlocked.Increment(ref _previewRequestVersion);
                if (_previewImage != null) _previewImage.Image = null;
                foreach (var image in _previewCache.Values)
                    if (image != null) image.Dispose();
                _previewCache.Clear();
                if (_previewPlaceholder != null) _previewPlaceholder.Dispose();
                _previewPlaceholder = null;
            }

            void SetBusy(bool busy)
            {
                _busy = busy;
                if (_refreshBtn != null) _refreshBtn.Enabled = !busy;
                if (_selectAllBtn != null) _selectAllBtn.Enabled = !busy && _list != null && _list.Items.Count > 0;
                if (_clearSelectionBtn != null) _clearSelectionBtn.Enabled = !busy && _list != null && _list.CheckedItems.Count > 0;
                if (_searchBox != null) _searchBox.Enabled = !busy;
                UpdateButtons();
                Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            }

            void UpdateButtons()
            {
                if (_downloadBtn == null) return;
                var checkedCount = _list == null ? 0 : _list.CheckedItems.Count;
                var selectedCount = _list == null ? 0 : _list.SelectedItems.Count;
                var count = checkedCount > 0 ? checkedCount : selectedCount;
                _downloadBtn.Enabled = !_busy && count > 0;
                _downloadBtn.Text = count > 1 ? "下载选中模型 (" + count + ")" : "下载选中模型";
                if (_selectAllBtn != null)
                    _selectAllBtn.Enabled = !_busy && _list != null && _list.Items.Count > 0 && checkedCount < _list.Items.Count;
                if (_clearSelectionBtn != null)
                    _clearSelectionBtn.Enabled = !_busy && checkedCount > 0;
            }

            void SetStatus(string text)
            {
                if (_status != null) _status.Text = text ?? "";
            }

            void SetProgress(int value)
            {
                if (_progress != null) _progress.Value = value;
            }



            void OnDialogUi(Action action)
            {
                if (IsDisposed) return;
                try
                {
                    if (InvokeRequired) BeginInvoke(action);
                    else action();
                }
                catch { }
            }

            static string FirstNonEmpty(string first, string second)
            {
                return string.IsNullOrEmpty(first) ? (second ?? "") : first;
            }

            static string FormatBytes(long value)
            {
                if (value < 0) return "-";
                if (value >= 1024L * 1024L * 1024L) return (value / 1073741824.0).ToString("F2") + " GB";
                if (value >= 1024L * 1024L) return (value / 1048576.0).ToString("F1") + " MB";
                if (value >= 1024L) return (value / 1024.0).ToString("F1") + " KB";
                return value + " B";
            }

            static string FormatTime(string value)
            {
                if (string.IsNullOrEmpty(value)) return "-";
                DateTime dt;
                if (!DateTime.TryParse(value, out dt)) return value;
                if (dt.Kind == DateTimeKind.Utc) dt = dt.ToLocalTime();
                return dt.ToString("yyyy-MM-dd HH:mm");
            }

            static string SafeBaseFileName(string name)
            {
                if (string.IsNullOrEmpty(name)) name = "model";
                foreach (var c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                name = name.Trim().TrimEnd('.');
                if (name.Length > 120)
                    name = name.Substring(0, 120).TrimEnd();
                return string.IsNullOrEmpty(name) ? "model" : name;
            }
        }

        class UploadJob
        {
            public int Id;
            public string Name;
            public string VrcaPath;
            public string ImagePath;
            public string State;
            public string Stage;
            public int Progress;
            public int LocalProgress;
            public int UploadProgress;
            public int SecurityProgress;
            public string AvatarId;
            public string Error;
            public string LogPath;
            public DateTime StartedAt;
            public DateTime FinishedAt;
            public VrcaClient.UploadRequest Request;
            public bool IsWorldUpload;
            public VrcaClient.WorldUploadRequest WorldRequest;
            public VrcaClient.WorldUploadRequest WorldQuestRequest;
            public volatile bool PcPlatformCompleted;
            public volatile bool QuestPhaseActive;
            public VrcaClient Client;
            public Thread WorkerThread;
            public volatile bool CancelRequested;
            public Panel ProgressCard;
            public PictureBox CardThumb;
            public Label CardNameLabel;
            public Label CardIdLabel;
            public Label CardOverallLabel;
            public ProgressStrip CardOverallProgress;
            public Label CardStepLabel;
            public ProgressStrip CardStepProgress;
            public Label CardHintLabel;
            public Button CardStopButton;
            public Button CardRemoveButton;
            public Button CardSwitchButton;
            public Button CardCopyButton;
        }
    }
}
