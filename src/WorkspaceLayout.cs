using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HotUploadPreview;

namespace VrcaUploader
{
    public partial class MainForm
    {
        static bool WorkspaceSelfTest { get { return Environment.GetCommandLineArgs().Contains("--ui-self-test"); } }
        Panel devSidebar, devHeader, devBody, devEditor, devActions, devScanPage, devDownloadPage;
        Label devHeading, devSubtitle;
        ScrollViewport devEditorScroll;
        readonly List<HotUploadPreview.ActionButton> devNav = new List<HotUploadPreview.ActionButton>();
        readonly Dictionary<int, Panel> devActionPanels = new Dictionary<int, Panel>();
        DownloadDialog devDownload;
        Panel devDownloadEmpty;
        PictureBox devModelCover, devWorldCover;
        int devPage;
        System.Windows.Forms.Timer devRefresh;
        public MainForm()
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _authClient = new VrcaClient(_baseDir, AppendActivity);
            Text = "VRC 热上传 · 眠川" + (_isolatedGuiAuthSession ? " · 独立账号" : "");
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1440, 900); MinimumSize = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Bg; Font = Theme.Font(15);
            BuildLoginView(); BuildAppView();
            if (!WorkspaceSelfTest) RestoreLastScanFolder();
            DevNavigate(0);
            Load += delegate {
                if (WorkspaceSelfTest) { BeginInvoke(new Action(RunWorkspaceUiTests)); return; }
                _authClient.StartNewLog("眠川 / VRChat login");
                CheckCachedLogin();
            };
            devRefresh = new System.Windows.Forms.Timer { Interval = 700 };
            devRefresh.Tick += delegate { if (_appUserLabel != null) _appUserLabel.Text = _loggedIn ? _currentUser : "未登录 VRChat"; };
            devRefresh.Start();
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (WorkspaceSelfTest) return;
                lock (_jobsLock) {
                    if (_jobs.Any(j => IsJobActive(j)) || _scanBusy || _preflightBusy || (devDownload != null && devDownload.Busy)) {
                        e.Cancel = true;
                        MessageBox.Show(this, "仍有上传、扫描、预检查或下载任务，请先停止任务或等待完成，再关闭窗口。", "任务仍在运行");
                    }
                }
            };
            FormClosed += delegate { devRefresh.Dispose(); if (devDownload != null) devDownload.Dispose(); if(devModelCover.Image!=null)devModelCover.Image.Dispose(); if(devWorldCover.Image!=null)devWorldCover.Image.Dispose(); foreach(var j in _jobs)DisposeJobCardImage(j); };
        }
        Label DevLabel(Control parent, string text, int x, int y, int w, int h, float size = 15, Color? color = null, bool bold = false)
        {
            var l = new Label { Text = text, Bounds = new Rectangle(x,y,w,h), ForeColor = color ?? TextMain, BackColor = Color.Transparent, Font = Theme.Font(size,bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            parent.Controls.Add(l); return l;
        }
        Button DevButton(Control parent, string text, int x,int y,int w,int h,Action action,bool primary=false)
        {
            var b = MakeButton(text,x,y,w,h,primary ? Accent : Card,primary ? Bg : TextMain); parent.Controls.Add(b); b.Click += delegate { action(); }; return b;
        }
        TextBox DevField(Control parent,string label,int y,bool multi=false)
        {
            DevLabel(parent,label,24,y,parent.Width-48,25,15,TextMuted);
            var t=MakeTextBox(24,y+34,parent.Width-48,false); t.Height=multi?78:34; t.Multiline=multi;
            t.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right; t.AccessibleName=label; parent.Controls.Add(t); return t;
        }
        TextBox DevFile(Control parent,string label,int y,string filter,TextBox name=null,Action picker=null)
        {
            DevLabel(parent,label,24,y,parent.Width-48,25,15,TextMuted);
            var t=MakeTextBox(24,y+34,parent.Width-178,false); t.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            parent.Controls.Add(t); var b=DevButton(parent,"选择文件",parent.Width-139,y+30,115,42,delegate { if(picker!=null)picker();else PickFile(t,filter,name); });
            b.Anchor=AnchorStyles.Top|AnchorStyles.Right; return t;
        }
        void BuildLoginView()
        {
            _loginView = new Panel { BackColor=Bg, Size=new Size(1120,720) };
            _loginFormCard=MakeCard(new Point(0,0),new Size(650,628)); _loginView.Controls.Add(_loginFormCard);
            DevLabel(_loginFormCard,"VRChat 账号",24,18,580,42,25,null,true);
            DevLabel(_loginFormCard,"登录 VRChat 账号，上传你的模型与世界。",24,69,580,27,14,TextMuted);
            _user=DevField(_loginFormCard,"用户名 / 邮箱",119);
            _pass=DevField(_loginFormCard,"密码",215); _pass.UseSystemPasswordChar=true;
            _twoFa=DevField(_loginFormCard,"验证码（需要时再填写）",311); _twoFa.Enabled=false;
            _loginBtn=DevButton(_loginFormCard,"登录",24,414,174,47,delegate { OnLoginClick(this,EventArgs.Empty); },true);
            _loginStatus=DevLabel(_loginFormCard,"未登录",24,483,600,91,15,TextMuted);
            _loginStatus.AutoEllipsis=false;
            _loginSuccessCard=MakeCard(new Point(0,0),new Size(650,330)); _loginView.Controls.Add(_loginSuccessCard); _loginSuccessCard.Visible=false;
            DevLabel(_loginSuccessCard,"已连接 VRChat",24,24,590,40,25,Accent,true);
            _loggedUserLabel=DevLabel(_loginSuccessCard,"",24,92,590,80,17); _loggedUserLabel.AutoEllipsis=false;
            _enterAppBtn=DevButton(_loginSuccessCard,"进入工作空间",24,199,180,47,ShowAppView,true);
            DevButton(_loginSuccessCard,"退出账号",223,199,140,47,LogoutAndReturnToLogin);
            DevLabel(_loginSuccessCard,"登录会话独立保存，可在此退出当前账号。",24,265,590,37,14,TextMuted);
        }
        void BuildAppView()
        {
            _appView=new Panel { Dock=DockStyle.Fill,BackColor=Bg }; Controls.Add(_appView);
            devSidebar=new Panel { BackColor=Theme.Side }; devHeader=new Panel { BackColor=Bg }; devBody=new Panel { BackColor=Bg };
            _appView.Controls.Add(devSidebar); _appView.Controls.Add(devHeader); _appView.Controls.Add(devBody);
            DevLabel(devSidebar,"V",29,27,54,46,34,Accent,true); DevLabel(devSidebar,"热上传",90,28,109,33,23,null,true);
            DevLabel(devSidebar,"MIANCHUAN",91,63,114,19,12,TextMuted);
            DevLabel(devSidebar,"工作空间",26,121,150,25,13,TextMuted);
            string[] nav={"01   模型上传","02   世界上传","03   文件夹扫描","04   上传队列","05   模型下载"};
            for(int i=0;i<nav.Length;i++) { int index=i; var b=(HotUploadPreview.ActionButton)DevButton(devSidebar,nav[i],16,163+i*59,178,47,delegate { if(index==2)OpenScanWorkspace();else DevNavigate(index); }); b.Quiet=true; b.BackColor=Theme.Side; devNav.Add(b); }
            var userCard=MakeCard(new Point(16,700),new Size(178,142)); devSidebar.Controls.Add(userCard);
            DevLabel(userCard,"本地工作空间",13,10,155,27,16,null,true);
            _appUserLabel=DevLabel(userCard,"未登录 VRChat",13,42,155,40,12,TextMuted);
            DevButton(userCard,"账号管理  ›",9,91,158,36,delegate { DevNavigate(5); });
            devSidebar.Resize+=delegate { userCard.Top=devSidebar.Height-162; };
            devHeading=DevLabel(devHeader,"模型上传",0,10,540,45,29,null,true);
            devSubtitle=DevLabel(devHeader,"",0,62,730,29,14,TextMuted);
            var badge=DevLabel(devHeader,"作者：眠川",810,23,245,34,14,Accent,true);
            badge.TextAlign=ContentAlignment.MiddleRight; devHeader.Resize+=delegate { badge.Left=devHeader.Width-245; };
            var logs=DevButton(devHeader,"打开日志目录",810,58,145,34,delegate {
                var path=Path.Combine(_baseDir,"_runtime","logs");Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe","\""+path+"\"");
            });
            devHeader.Resize+=delegate {logs.Left=devHeader.Width-145;devSubtitle.Width=Math.Max(200,devHeader.Width-165);};
            _customerStatus=DevLabel(_appView,"准备就绪。先登录 VRChat，再选择文件。",240,850,1100,35,13,TextMuted);
            BuildTaskEditor(); BuildQueuePanel();
            devDownloadPage=new Panel { BackColor=Bg }; devBody.Controls.Add(devDownloadPage);
            devDownloadEmpty=new Panel { Dock=DockStyle.Fill,BackColor=Bg };devDownloadPage.Controls.Add(devDownloadEmpty);
            DevLabel(devDownloadEmpty,"登录后可以查看模型封面、简介并批量下载。",24,50,750,40,20);
            DevButton(devDownloadEmpty,"登录 VRChat",24,116,180,46,delegate { DevNavigate(5); },true);
            devBody.Controls.Add(_loginView);
            _appView.Resize+=delegate { LayoutAppPanels(); }; LayoutAppPanels();
        }
        void BuildTaskEditor()
        {
            devEditor=new Panel { BackColor=Bg,Width=700,Height=746 }; _taskEditorCard=devEditor;
            _vrcaModePanel=MakeCard(Point.Empty,new Size(700,691)); devEditor.Controls.Add(_vrcaModePanel);
            DevLabel(_vrcaModePanel,"01   模型文件",24,14,560,36,18,null,true);
            _vrca=DevFile(_vrcaModePanel,"VRCA 文件",67,"VRCA 文件|*.vrca");
            DevLabel(_vrcaModePanel,"02   展示信息",24,172,550,34,18,null,true);
            devModelCover=new PictureBox { Bounds=new Rectangle(24,222,124,100),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Input }; _vrcaModePanel.Controls.Add(devModelCover);
            _image=MakeTextBox(174,223,502,false); _image.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right; _vrcaModePanel.Controls.Add(_image);
            DevButton(_vrcaModePanel,"更换封面",174,274,117,42,delegate { PickFile(_image,"图片|*.png;*.jpg;*.jpeg",null); });
            _name=DevField(_vrcaModePanel,"模型名称",347); _desc=DevField(_vrcaModePanel,"简介（可选）",447,true);
            _release=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Theme.Input,ForeColor=TextMain,Font=Theme.Font(15),FlatStyle=FlatStyle.Flat,Bounds=new Rectangle(24,594,206,34) }; _release.Items.AddRange(new object[]{"private","public"}); _release.SelectedIndex=0; _vrcaModePanel.Controls.Add(_release);
            StyleWorkspaceRelease(_release);
            var releaseHint=DevLabel(_vrcaModePanel,"private：私有 / public：公开",247,594,429,33,14,TextMuted);releaseHint.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            _vrca.TextChanged+=delegate { if(!string.IsNullOrWhiteSpace(_vrca.Text)) _name.Text=Path.GetFileNameWithoutExtension(_vrca.Text); };
            _image.TextChanged+=delegate { DevCover(devModelCover,_image.Text); };
            _image.Text=EnsureDefaultCoverFile();
            _vrcaModePanel.AllowDrop=true; _vrcaModePanel.DragEnter+=delegate(object s,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
            _vrcaModePanel.DragDrop+=delegate(object s,DragEventArgs e){ var f=e.Data.GetData(DataFormats.FileDrop) as string[]; if(f!=null && f.Length==1 && Path.GetExtension(f[0]).Equals(".vrca",StringComparison.OrdinalIgnoreCase))_vrca.Text=f[0]; else SetCustomerStatus("请拖入一个 VRCA 文件；批量任务请使用文件夹扫描。"); };
            _vrcwModePanel=MakeCard(Point.Empty,new Size(700,924)); devEditor.Controls.Add(_vrcwModePanel);
            DevLabel(_vrcwModePanel,"01   PC / Quest 平台文件",24,14,570,36,18,null,true);
            _vrcw=DevFile(_vrcwModePanel,"PC 世界（必选，可同时选择两个平台）",65,"世界文件|*.vrcw",null,delegate{PickAndPairWorldFiles(false);});
            _vrcwQuest=DevFile(_vrcwModePanel,"Quest 世界（可选，可清空路径移除）",162,"世界文件|*.vrcw",null,delegate{PickAndPairWorldFiles(true);});
            _worldPairStatus=DevLabel(_vrcwModePanel,"尚未选择世界文件",24,251,640,36,14,Accent2);
            DevLabel(_vrcwModePanel,"02   世界信息",24,301,550,34,18,null,true);
            devWorldCover=new PictureBox { Bounds=new Rectangle(24,355,124,92),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Input }; _vrcwModePanel.Controls.Add(devWorldCover);
            _vrcwImage=MakeTextBox(174,355,502,false); _vrcwImage.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right; _vrcwModePanel.Controls.Add(_vrcwImage);
            DevButton(_vrcwModePanel,"更换封面",174,401,117,42,delegate { PickFile(_vrcwImage,"图片|*.png;*.jpg;*.jpeg",null); });
            _vrcwName=DevField(_vrcwModePanel,"世界名称",474);
            _worldIdBox=DevField(_vrcwModePanel,"更新现有世界 / World ID（留空则新建）",570);
            var worldRelease=new ComboBox { Name="_worldRelease",Bounds=new Rectangle(24,664,206,34),DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Theme.Input,ForeColor=TextMain,Font=Theme.Font(15),FlatStyle=FlatStyle.Flat }; worldRelease.Items.AddRange(new object[]{"private","public"}); worldRelease.SelectedIndex=0; _vrcwModePanel.Controls.Add(worldRelease);
            StyleWorkspaceRelease(worldRelease);
            _worldSignatureInputs=new WorldSignatureInputs(this) { Bounds=new Rectangle(24,730,652,176),Visible=false }; _vrcwModePanel.Controls.Add(_worldSignatureInputs);
            _worldIdBox.TextChanged+=delegate { _worldSignatureInputs.SetTarget(_worldIdBox.Text,!string.IsNullOrWhiteSpace(_vrcwQuest.Text)); UpdateWorkspaceEditorHeight(); };
            _vrcw.TextChanged+=delegate { _worldSignatureInputs.ClearPc(); if(!string.IsNullOrWhiteSpace(_vrcw.Text))_vrcwName.Text=Path.GetFileNameWithoutExtension(_vrcw.Text); _worldPairStatus.Text=string.IsNullOrEmpty(_vrcwQuest.Text)?"已选择 PC，未添加 Quest":"已选择两个文件；上传时会校验实际平台"; };
            _vrcwQuest.TextChanged+=delegate { _worldSignatureInputs.ClearQuest(); _worldSignatureInputs.SetTarget(_worldIdBox.Text,!string.IsNullOrWhiteSpace(_vrcwQuest.Text)); _worldPairStatus.Text=string.IsNullOrEmpty(_vrcwQuest.Text)?"未添加 Quest 文件":"已选择两个文件；上传时会校验实际平台"; };
            _vrcwImage.TextChanged+=delegate { DevCover(devWorldCover,_vrcwImage.Text); };
            _vrcwImage.Text=EnsureDefaultCoverFile();
            devEditorScroll=new ScrollViewport(devEditor); devBody.Controls.Add(devEditorScroll);
            devActions=new Panel { BackColor=Bg,Width=700,Height=56 }; devBody.Controls.Add(devActions);
            for(int mode=0;mode<2;mode++) {
                var row=new Panel { Dock=DockStyle.Fill,BackColor=Bg,Width=700,Height=56 }; devActions.Controls.Add(row); devActionPanels[mode]=row;
                if(mode==0) {
                    _addTaskBtn=DevButton(row,"开始上传   →",510,2,190,48,delegate { AddTaskFromForm(); },true); _addTaskBtn.Anchor=AnchorStyles.Top|AnchorStyles.Right;
                    _preflightBtn=DevButton(row,"预检查",385,2,112,48,PreflightCurrentFile); _preflightBtn.Anchor=AnchorStyles.Top|AnchorStyles.Right;
                } else {var b=DevButton(row,"开始上传   →",510,2,190,48,delegate { if(!DevRequireLogin())return; AddWorldTaskFromForm(); },true); b.Anchor=AnchorStyles.Top|AnchorStyles.Right;}
                var hint=DevLabel(row,"LZMA 标准打包",0,4,300,42,14,TextMuted);
                row.Resize+=delegate {hint.Width=Math.Max(1,row.Width-328);};
            }
            _modeVrcaBtn=new Button(); _modeVrcwBtn=new Button(); _modeScanBtn=new Button(); _downloadBtn=new Button();
            devScanPage=new Panel { BackColor=Bg }; devBody.Controls.Add(devScanPage); _scanModePanel=devScanPage;
            DevLabel(devScanPage,"模型 / 世界所在文件夹",0,0,800,27,15,TextMuted);
            _scanFolder=MakeTextBox(0,38,650,false); devScanPage.Controls.Add(_scanFolder);
            var browse=DevButton(devScanPage,"选择文件夹",668,33,133,44,BrowseScanFolder);
            _scanStartBtn=DevButton(devScanPage,"开始扫描",816,33,132,44,StartFolderScan,true);
            _scanStatus=DevLabel(devScanPage,"识别结果会逐条显示；普通版与面捕版独立列出。",0,90,1000,32,14,TextMuted);
            devScanPage.Resize+=delegate { _scanFolder.Width=Math.Max(200,devScanPage.Width-318); browse.Left=devScanPage.Width-300; _scanStartBtn.Left=devScanPage.Width-151; _scanStatus.Width=devScanPage.Width; };
        }
        void StyleWorkspaceRelease(ComboBox combo)
        {
            var row=new Panel { Bounds=new Rectangle(combo.Left,combo.Top,combo.Width,38),BackColor=Card };
            combo.Parent.Controls.Add(row);combo.Visible=false;
            var first=(HotUploadPreview.ActionButton)DevButton(row,"私有",0,0,99,38,delegate{combo.SelectedIndex=0;});
            var second=(HotUploadPreview.ActionButton)DevButton(row,"公开",107,0,99,38,delegate{combo.SelectedIndex=1;});
            first.AccessibleName="发布状态：私有";second.AccessibleName="发布状态：公开";
            Action sync=delegate{first.Selected=combo.SelectedIndex==0;second.Selected=combo.SelectedIndex==1;first.Invalidate();second.Invalidate();};
            combo.SelectedIndexChanged+=delegate{sync();};sync();
        }
        void DevCover(PictureBox box,string path)
        {
            Image next=null;
            try { if(File.Exists(path))using(var source=Image.FromFile(path))next=new Bitmap(source); } catch(Exception e){SetCustomerStatus("封面预览失败："+e.Message);}
            var old=box.Image; box.Image=next; if(old!=null)old.Dispose();
        }
        void BuildQueuePanel()
        {
            _detailCard=new Panel { BackColor=Bg }; devBody.Controls.Add(_detailCard);
            DevLabel(_detailCard,"任务队列",0,0,260,38,21,null,true);
            _emptyProgressLabel=DevLabel(_detailCard,"还没有任务\r\n选择文件后开始上传。",0,90,310,110,17,TextMuted);
            _progressList=new FlowLayoutPanel { AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,BackColor=Bg,Padding=Padding.Empty };
            _detailCard.Controls.Add(_progressList);
            _detailCard.Resize+=delegate {ResizeProgressList();};
            UpdateEmptyProgressLabel();
            _scanWorkspace=new FolderScanWorkspace(); devScanPage.Controls.Add(_scanWorkspace);
            _scanWorkspace.UploadRequested+=delegate(List<BatchUploadSelection> selection){ if(DevRequireLogin())QueueBatchSelections(selection,ResolveImagePath()); };
            _scanWorkspace.StopRequested+=delegate{StopFolderScan();};
            _scanWorkspace.CollapseRequested+=delegate{DevNavigate(3);};
            devScanPage.Resize+=delegate{_scanWorkspace.SetBounds(0,138,devScanPage.Width,Math.Max(360,devScanPage.Height-138));};
        }
        bool DevRequireLogin()
        {
            if(_loggedIn)return true;
            DevNavigate(5); SetLoginStatus("请先登录 VRChat 账号。",Warn); return false;
        }
        void DevNavigate(int page)
        {
            if(page<0 || page>5)return;
            devPage=page; if(page<3)_uploadMode=page;
            string[] titles={"模型上传","世界上传","文件夹扫描","上传队列","模型下载","账号管理"};
            string[] sub={"选择模型、配好封面，交给上传队列。","同一世界，依次上传 PC 与 Quest。","封面、平台与版本清楚分组，扫描结果逐条显示。","本地处理、文件传输、服务器检测分阶段展示。","多选模型，每个文件夹同时保存模型、封面和简介。","管理 VRChat 登录状态与账号会话。"};
            devHeading.Text=titles[page]; devSubtitle.Text=sub[page];
            for(int i=0;i<devNav.Count;i++){devNav[i].Selected=page==i;devNav[i].Invalidate();}
            _vrcaModePanel.Visible=page==0; _vrcwModePanel.Visible=page==1;
            devEditorScroll.Visible=devActions.Visible=page<2;
            foreach(var p in devActionPanels)p.Value.Visible=p.Key==page;
            _detailCard.Visible=page<2 || page==3;
            devScanPage.Visible=page==2; _scanWorkspace.Visible=page==2;
            devDownloadPage.Visible=page==4; _loginView.Visible=page==5;
            if(devDownload!=null && devDownload.IsDisposed)devDownload=null;
            if(page==4 && (_loggedIn || WorkspaceSelfTest) && devDownload==null) {
                devDownload=new DownloadDialog(this,_baseDir) { TopLevel=false,FormBorderStyle=FormBorderStyle.None,MinimumSize=Size.Empty,Dock=DockStyle.Fill };
                devDownloadPage.Controls.Add(devDownload); devDownload.Show(); devDownload.BringToFront();
            }
            devDownloadEmpty.Visible=devDownload==null;
            UpdateWorkspaceEditorHeight(); LayoutAppPanels();
        }
        void SwitchUploadMode(int mode) { DevNavigate(mode); }
        void SetScanWorkspaceExpanded(bool expanded) { _scanWorkspaceExpanded=expanded; LayoutAppPanels(); }
        void ShowLoginView(){DevNavigate(5);}
        void ShowAppView(){DevNavigate(0);}
        void ShowDownloadDialog(){DevNavigate(4);}
        void LogoutAndReturnToLogin()
        {
            lock(_jobsLock) if(_jobs.Any(j=>IsJobActive(j)) || (devDownload!=null && devDownload.Busy)) { SetCustomerStatus("请先停止上传或等待下载完成，再切换账号。"); return; }
            try {_authClient.Logout();}catch(Exception e){SetCustomerStatus(e.Message);return;}
            if(devDownload!=null){devDownload.Close();devDownload.Dispose();devDownload=null;}
            MarkLoggedOut("已退出账号，请重新登录。"); DevNavigate(5);
        }
        void UpdateWorkspaceEditorHeight()
        {
            if(devEditor==null)return;
            devEditor.Height=devPage==1 ? (string.IsNullOrWhiteSpace(_worldIdBox.Text)?725:924) : 665;
            _vrcaModePanel.Height=665; _vrcwModePanel.Height=devEditor.Height;
            _vrcaModePanel.Width=_vrcwModePanel.Width=devEditor.Width;
            if(devEditorScroll!=null)devEditorScroll.RefreshViewport();
        }
        void LayoutAppPanels()
        {
            if(devBody==null)return;
            int rail=212,pad=30;
            devSidebar.SetBounds(0,0,rail,ClientSize.Height);
            devHeader.SetBounds(rail+pad,12,ClientSize.Width-rail-pad*2,95);
            devBody.SetBounds(rail+pad,119,ClientSize.Width-rail-pad*2,ClientSize.Height-174);
            _customerStatus.SetBounds(rail+pad,ClientSize.Height-42,ClientSize.Width-rail-pad*2,32);
            int queueWidth=Math.Max(322,Math.Min(380,devBody.Width/3));
            int editorWidth=devBody.Width-queueWidth-24;
            devEditorScroll.SetBounds(0,0,editorWidth,devBody.Height-69);
            devActions.SetBounds(0,devBody.Height-53,editorWidth-14,53);
            _vrcaModePanel.Width=_vrcwModePanel.Width=devEditor.Width;
            _detailCard.SetBounds(devPage==3?0:devBody.Width-queueWidth,0,devPage==3?devBody.Width:queueWidth,devBody.Height);
            devScanPage.Bounds=devDownloadPage.Bounds=_loginView.Bounds=new Rectangle(0,0,devBody.Width,devBody.Height);
            _scanWorkspace.SetBounds(0,138,devScanPage.Width,Math.Max(360,devScanPage.Height-138));
            _worldPairStatus.Width=_vrcwModePanel.Width-48;
            _worldSignatureInputs.Width=_vrcwModePanel.Width-48;
            ResizeProgressList();
        }
        void ResizeProgressList()
        {
            if(_detailCard==null || _progressList==null)return;
            _progressList.SetBounds(0,55,_detailCard.Width,_detailCard.Height-55);
            int w=Math.Max(302,_progressList.ClientSize.Width-26);
            foreach(Control c in _progressList.Controls)ResizeProgressCard(c as Panel,w);
        }
        void EnsureProgressCard(UploadJob job)
        {
            if(job.ProgressCard!=null)return;
            var card=MakeProgressItemCard(Point.Empty,new Size(338,298));card.Tag=job;card.Margin=new Padding(0,0,0,15);
            job.CardThumb=new PictureBox{Bounds=new Rectangle(14,16,52,52),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Input};card.Controls.Add(job.CardThumb);
            job.CardNameLabel=DevLabel(card,"",80,12,220,29,16,null,true);
            job.CardIdLabel=DevLabel(card,"",80,43,220,25,12,TextMuted);
            job.CardOverallLabel=DevLabel(card,"",14,82,300,27,13,Accent2,true);
            job.CardOverallProgress=MakeLargeProgress(14,115,300,6);card.Controls.Add(job.CardOverallProgress);
            job.CardStepLabel=DevLabel(card,"",14,139,300,26,13);
            job.CardStepProgress=MakeLargeProgress(14,171,300,6);card.Controls.Add(job.CardStepProgress);
            job.CardHintLabel=DevLabel(card,"",14,190,300,37,12,TextMuted);
            job.CardStopButton=DevButton(card,"停止",12,247,63,35,delegate{StopUploadJob(job);});
            job.CardRemoveButton=DevButton(card,"移除",82,247,63,35,delegate{RemoveProgressJob(job);});
            job.CardSwitchButton=DevButton(card,"日志",152,247,63,35,delegate{ShowWorkspaceTask(job);});
            job.CardCopyButton=DevButton(card,"复制 ID",222,247,78,35,delegate{if(!string.IsNullOrEmpty(job.AvatarId))Clipboard.SetText(job.AvatarId);});
            job.ProgressCard=card;_progressList.Controls.Add(card);ResizeProgressList();SetJobCardCover(job,job.ImagePath);
        }
        void ResizeProgressCard(Panel card,int width)
        {
            if(card==null)return;card.Size=new Size(width,298);var j=card.Tag as UploadJob;if(j==null)return;
            j.CardNameLabel.Width=j.CardIdLabel.Width=width-96;
            j.CardOverallLabel.Width=j.CardOverallProgress.Width=j.CardStepLabel.Width=j.CardStepProgress.Width=j.CardHintLabel.Width=width-28;
        }
        void ShowWorkspaceTask(UploadJob job)
        {
            using(var form=new WorkspaceTaskLogForm(job))form.ShowDialog(this);
        }
        void WriteWorkspaceFailure(UploadJob job,Exception ex)
        {
            try {
                if(job.Client!=null)job.Client.LogWorkspaceException("task="+job.Id+" name="+job.Name+" phase="+job.Stage,ex);
                else if(!string.IsNullOrEmpty(job.LogPath))File.AppendAllText(job.LogPath,VrcaClient.FormatDiagnosticEntry(VrcaClient.ProtectDiagnosticText("[TASK_ERROR] "+ex.ToString())),Encoding.UTF8);
            }catch{}
        }
        Panel MakeCard(Point location,Size size){return new Surface{Location=location,Size=size,Fill=Card,BackColor=Bg};}
        Panel MakeProgressShell(Point location,Size size){return MakeCard(location,size);}
        Panel MakeProgressItemCard(Point location,Size size){return MakeCard(location,size);}
        Button MakeButton(string text,int x,int y,int w,int h,Color back,Color fore){return new HotUploadPreview.ActionButton(text,back==Accent){Bounds=new Rectangle(x,y,w,h),BackColor=back,ForeColor=fore};}

        sealed partial class WorldSignatureInputs
        {
            public WorldSignatureInputs(MainForm owner)
            {
                BackColor=Color.Transparent;Size=new Size(238,104);
                var pcLabel=owner.DevLabel(this,"PC 签名文本文件（可选）",0,0,238,22,14,TextMuted);
                pcFile=owner.MakeTextBox(0,22,162,false);pcFile.Name="pcWorldSignatureFile";Controls.Add(pcFile);
                var pcBrowse=owner.DevButton(this,"选择",172,20,66,34,delegate{Pick(pcFile);});
                questLabel=owner.DevLabel(this,"Quest 签名文本文件（可选）",0,48,238,22,14,TextMuted);
                questFile=owner.MakeTextBox(0,70,162,false);questFile.Name="questWorldSignatureFile";Controls.Add(questFile);
                questBrowse=owner.DevButton(this,"选择",172,68,66,34,delegate{Pick(questFile);});
                Resize+=delegate {
                    int row=Height>=150?82:48;
                    pcLabel.Width=questLabel.Width=Width;
                    pcFile.Width=questFile.Width=Math.Max(100,Width-82);
                    pcBrowse.Left=questBrowse.Left=Width-66;
                    questLabel.Top=row;questFile.Top=row+22;questBrowse.Top=row+20;
                };
            }
        }

        partial class DownloadDialog
        {
            public bool Busy {get{return _busy;}}
            public void TestWorkspaceDownload(Action<bool,string> check)
            {
                _allFiles.Add(new VrcaClient.VrcaFileEntry {AvatarName="普通模型",Name="model-a",Description="普通版"});
                _allFiles.Add(new VrcaClient.VrcaFileEntry {AvatarName="面捕模型",Name="model-b",Description="面捕版"});
                ApplyFilter();check(_list.Items.Count==2,"download rows shown");
                SetAllItemsChecked(true);check(SelectedEntries().Count==2,"download multi-select");
                SetAllItemsChecked(false);check(_list.CheckedItems.Count==0,"download clear selection");
                _searchBox.Text="面捕";check(_list.Items.Count==1,"download search name or description");
                _searchBox.Clear();SetBusy(true);check(!_downloadBtn.Enabled && !_refreshBtn.Enabled,"download busy actions disabled");SetBusy(false);
                _allFiles.Clear();ApplyFilter();
            }
            void BuildView()
            {
                _searchBox=MakeDialogTextBox(0,35,300);Controls.Add(_searchBox);_searchBox.TextChanged+=delegate{ApplyFilter();};
                _owner.DevLabel(this,"搜索模型名称",0,0,300,25,14,TextMuted);
                _refreshBtn=_owner.DevButton(this,"刷新",316,31,85,42,RefreshFiles);
                _selectAllBtn=_owner.DevButton(this,"全选",413,31,85,42,delegate{SetAllItemsChecked(true);});
                _clearSelectionBtn=_owner.DevButton(this,"取消选择",510,31,113,42,delegate{SetAllItemsChecked(false);});
                _downloadBtn=_owner.DevButton(this,"下载选中模型",680,31,198,42,StartDownload,true);
                _list=new ListView{View=View.Details,FullRowSelect=true,MultiSelect=true,CheckBoxes=true,HideSelection=false,BackColor=Theme.Input,ForeColor=TextMain,Font=Theme.Font(15),BorderStyle=BorderStyle.FixedSingle};
                _list.OwnerDraw=true;
                _list.DrawColumnHeader+=delegate(object sender,DrawListViewColumnHeaderEventArgs e) {
                    using(var brush=new SolidBrush(Theme.Surface))e.Graphics.FillRectangle(brush,e.Bounds);
                    TextRenderer.DrawText(e.Graphics,e.Header.Text,_list.Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-8,e.Bounds.Height),Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis);
                };
                _list.DrawItem+=delegate(object sender,DrawListViewItemEventArgs e){e.DrawDefault=true;};
                _list.DrawSubItem+=delegate(object sender,DrawListViewSubItemEventArgs e){e.DrawDefault=true;};
                _list.Columns.Add("名称",230);_list.Columns.Add("版本",58);_list.Columns.Add("大小",88);_list.Columns.Add("时间",130);_list.Columns.Add("状态",70);Controls.Add(_list);
                _list.SelectedIndexChanged+=delegate{UpdateButtons();ShowSelectedPreview();};_list.ItemChecked+=delegate{if(IsHandleCreated)BeginInvoke(new Action(UpdateButtons));};
                BuildPreviewCard();
                _progress=_owner.MakeLargeProgress(0,580,860,8);Controls.Add(_progress);
                _status=_owner.DevLabel(this,"点击刷新获取当前账号模型。",0,602,890,34,14,TextMuted);
                Resize+=delegate{ResizeColumns();};UpdateButtons();ResizeColumns();
            }
            void BuildPreviewCard()
            {
                _previewCard=_owner.MakeCard(new Point(700,102),new Size(310,490));Controls.Add(_previewCard);
                _previewPlaceholder=CreatePreviewPlaceholder(280,210);
                _previewImage=new PictureBox{Bounds=new Rectangle(16,17,278,204),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Input,Image=_previewPlaceholder};_previewCard.Controls.Add(_previewImage);
                _previewName=_owner.DevLabel(_previewCard,"尚未选择模型",16,241,278,50,18,null,true);
                _previewMeta=_owner.DevLabel(_previewCard,"",16,301,278,28,13,TextMuted);
                _previewDescription=_owner.DevLabel(_previewCard,"暂无简介",16,345,278,95,14,TextMuted);
                _previewHint=_owner.DevLabel(_previewCard,"下载包含封面和简介",16,450,278,30,13,Accent2);
            }
            void ResizeColumns()
            {
                if(_list==null)return;int w=ClientSize.Width,h=ClientSize.Height;int side=310;
                _downloadBtn.Left=w-204;
                _list.SetBounds(0,102,Math.Max(400,w-side-24),Math.Max(230,h-170));
                _previewCard.SetBounds(w-side,102,side,Math.Max(400,h-170));
                _previewImage.Height=Math.Min(204,Math.Max(132,_previewCard.Height/3));
                _previewName.Top=_previewImage.Bottom+16;_previewMeta.Top=_previewName.Bottom+9;
                _previewDescription.Top=_previewMeta.Bottom+14;
                _previewHint.Top=_previewCard.Height-42;
                _previewDescription.Height=Math.Max(40,_previewHint.Top-_previewDescription.Top-14);
                _list.Columns[0].Width=Math.Max(155,_list.ClientSize.Width-360);
                _progress.SetBounds(0,h-55,w,7);_status.SetBounds(0,h-39,w,32);
            }
        }

        void RunWorkspaceUiTests()
        {
            string dir=Environment.GetEnvironmentVariable("HOTUPLOAD_UI_QA_DIR");
            if(string.IsNullOrWhiteSpace(dir)) {Environment.ExitCode=2;Close();return;}
            try{
                Directory.CreateDirectory(dir);var report=new List<string>();int count=0;
                Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception(name);report.Add("PASS "+name);count++;};
                foreach(var size in new[]{new Size(1440,900),new Size(1200,800)}){
                    ClientSize=size;
                    for(int page=0;page<6;page++){
                        DevNavigate(page);Application.DoEvents();check(devBody.Right<=ClientSize.Width,"page bounds "+page+" / "+size.Width);
                        check(devBody.Bottom<_customerStatus.Top,"status outside workspace "+page);
                        if(page<2){
                            var row=devActionPanels[page];
                            foreach(Control c in row.Controls){
                                check(c.Left>=0 && c.Right<=row.Width && c.Bottom<=row.Height,"action fits "+c.Text+" / "+size.Width);
                                foreach(Control other in row.Controls)if(c!=other)check(!c.Bounds.IntersectsWith(other.Bounds),"actions do not overlap / "+size.Width);
                            }
                            var file=page==0?_vrca:_vrcw;
                            check(file.Width>=180,"usable file path width");
                            check(_emptyProgressLabel.Visible && !_progressList.Visible,"queue empty state visible");
                        }
                        if(page==2)check(_scanWorkspace.Width==devBody.Width && _scanWorkspace.Bottom<=devBody.Height,"scan full width and fits");
                        if(page==4){
                            check(!devDownloadEmpty.Visible,"download placeholder not covering list");
                            check(devDownload.Controls.Cast<Control>().All(c=>c.Right<=devDownload.Width && c.Bottom<=devDownload.Height),"download controls fit");
                        }
                        using(var bmp=new Bitmap(_appView.Width,_appView.Height)){_appView.DrawToBitmap(bmp,_appView.ClientRectangle);bmp.Save(Path.Combine(dir,size.Width+"-page-"+page+".png"));}
                    }
                }
                DevNavigate(0);_name.Text="保留的模型名";DevNavigate(2);DevNavigate(0);check(_name.Text=="保留的模型名","preserve form values");
                _release.SelectedIndex=1;check(SelectedRelease()=="public","public release still mapped to API value");_release.SelectedIndex=0;check(SelectedRelease()=="private","private release still mapped to API value");
                DevNavigate(1);_worldIdBox.Text="wrld_11111111-2222-4333-8444-555555555555";check(_worldSignatureInputs.Visible,"inline world signature");_worldIdBox.Clear();check(!_worldSignatureInputs.Visible,"new world no signature input");
                DevNavigate(5);_pass.Text="offline-test-only";MarkLoggedIn("离线账号状态测试");check(!_loginFormCard.Visible && _loginSuccessCard.Visible,"logged-in account card");check(_pass.Text.Length==0,"password cleared from UI");MarkLoggedOut("离线测试");check(_loginFormCard.Visible && !_loginSuccessCard.Visible,"logged-out account card");
                SetLoginBusy(true);check(!_loginBtn.Enabled && !_user.Enabled,"login double-click blocked");SetLoginBusy(false);check(_loginBtn.Enabled && _user.Enabled,"login controls restored");
                DevNavigate(0);check(!DevRequireLogin() && devPage==5,"upload requires VRChat login");
                for(int i=0;i<4;i++){DevNavigate(2);DevNavigate(3);OpenScanWorkspace();check(devPage==2 && _scanWorkspace.Visible,"scan reopen "+i);}
                var fake=new UploadJob{Name="离线界面测试 / 这是一个很长的模型名称",State="失败",Stage="上传文件",Error="离线模拟错误，仅用于检查按钮与任务布局",Progress=55,LocalProgress=100,UploadProgress=60};
                _jobs.Add(fake);UpdateDetailPanel(fake);DevNavigate(0);check(fake.ProgressCard.Visible,"real queue renderer");check(!fake.CardStopButton.Enabled && fake.CardRemoveButton.Enabled,"failed task action state");
                check(fake.CardCopyButton.Right<=fake.ProgressCard.Width,"queue buttons fit");
                using(var bmp=new Bitmap(_appView.Width,_appView.Height)){_appView.DrawToBitmap(bmp,_appView.ClientRectangle);bmp.Save(Path.Combine(dir,"queue-with-task.png"));}
                fake.LogPath=Path.Combine(dir,"synthetic-task.log");File.WriteAllText(fake.LogPath,"[API_TRACE] response status=400\r\n[hotswap] CAB-test Compression=LZMA\r\n[TASK_ERROR] synthetic offline trace",Encoding.UTF8);
                using(var viewer=new WorkspaceTaskLogForm(fake)){
                    viewer.Show(this);Application.DoEvents();
                    foreach(var s in new[]{new Size(1100,730),new Size(900,600)}){
                        viewer.ClientSize=s;Application.DoEvents();
                        foreach(Control c in viewer.Controls)check(c.Right<=viewer.ClientSize.Width && c.Bottom<=viewer.ClientSize.Height,"log viewer control fits "+c.GetType().Name);
                        check(viewer.Controls.OfType<RichTextBox>().Single().Text.Contains("CAB-test"),"live log viewer renders raw technical line");
                        using(var bmp=new Bitmap(viewer.Width,viewer.Height)){viewer.DrawToBitmap(bmp,new Rectangle(0,0,viewer.Width,viewer.Height));bmp.Save(Path.Combine(dir,"log-view-"+s.Width+".png"));}
                    }
                    viewer.Close();
                }
                RemoveProgressJob(fake);check(_jobs.Count==0 && _progressList.Controls.Count==0,"task removal restores empty queue");
                devDownload.TestWorkspaceDownload(check);
                string fixture=Environment.GetEnvironmentVariable("HOTUPLOAD_UI_SCAN_TEST_DIR");
                if(!string.IsNullOrWhiteSpace(fixture)) {
                    DevNavigate(2);_scanFolder.Text=fixture;StartFolderScan();
                    var limit=DateTime.UtcNow.AddSeconds(80);
                    while(_scanBusy && DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(40);}
                    check(!_scanBusy && _scanWorkspace.HasResults,"real offline folder scan finishes without account or card");
                    var rows=ScanUploadAssets(fixture,EnsureDefaultCoverFile());
                    check(rows.Count==2 && rows.Count(r=>r.AssetType=="模型")==1 && rows.Count(r=>r.AssetType=="地图")==1,"real model and world separated");
                    check(rows.First(r=>r.AssetType=="地图").PlatformLabel=="仅 PC","real world platform probe");
                    check(rows.First(r=>r.AssetType=="地图").Description.Contains("缓存"),"world platform cache reused");
                    string savedFolder;List<BatchUploadSelection> savedRows;
                    check(TryLoadScanCatalog(out savedFolder,out savedRows) && savedRows.Count==2,"scan catalog persisted independently");
                    StopFolderScan();DevNavigate(3);OpenScanWorkspace();check(devPage==2 && _scanWorkspace.HasResults,"scan stop and reopen preserves rows");
                }
                var scanRows=new List<BatchUploadSelection>{
                    new BatchUploadSelection{AssetType="模型",DisplayName="同目录普通版",VrcaPath=Path.Combine(dir,"normal.vrca"),PlatformLabel="PC"},
                    new BatchUploadSelection{AssetType="模型",DisplayName="同目录面捕版",VrcaPath=Path.Combine(dir,"face.vrca"),PlatformLabel="PC"},
                    new BatchUploadSelection{AssetType="地图",DisplayName="双端世界",VrcaPath=Path.Combine(dir,"pc.vrcw"),QuestVrcwPath=Path.Combine(dir,"quest.vrcw"),PlatformLabel="PC + Quest"}
                };
                DevNavigate(2);_scanWorkspace.RestoreCachedResults(dir,EnsureDefaultCoverFile(),scanRows);Application.DoEvents();check(_scanWorkspace.HasResults,"scan rows restored");DevNavigate(3);OpenScanWorkspace();check(_scanWorkspace.HasResults && _scanWorkspace.Visible,"scan results survive page switch");
                using(var bmp=new Bitmap(_appView.Width,_appView.Height)){_appView.DrawToBitmap(bmp,_appView.ClientRectangle);bmp.Save(Path.Combine(dir,"scan-with-results.png"));}
                check(_jobs.Count==0,"no demo or real upload task created");
                check(!_authClient.HasCachedLogin(),"isolated empty QA session");
                File.WriteAllLines(Path.Combine(dir,"ui-checks.txt"),report.Concat(new[]{"MIANCHUAN_UI_OK checks="+count}).ToArray(),Encoding.UTF8);
            }catch(Exception ex){File.WriteAllText(Path.Combine(dir,"ui-failure.txt"),ex.ToString());Environment.ExitCode=1;}
            finally{Close();}
        }
    }
}
