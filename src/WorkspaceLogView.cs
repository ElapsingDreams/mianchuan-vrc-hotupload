using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using HotUploadPreview;

namespace VrcaUploader
{
    public partial class MainForm
    {
        sealed class WorkspaceTaskLogForm : Form
        {
            readonly UploadJob job;
            readonly RichTextBox log;
            readonly Label state;
            readonly Label path;
            readonly Label hint;
            readonly CheckBox follow;
            readonly System.Windows.Forms.Timer timer;
            readonly WorkspaceLogTail tail = new WorkspaceLogTail();

            public WorkspaceTaskLogForm(UploadJob upload)
            {
                job=upload;Text="任务详情 / 实时技术日志";
                ClientSize=new Size(1100,730);MinimumSize=new Size(900,600);
                StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Back;ForeColor=Theme.Ink;Font=Theme.Font(15);
                var title=new Label{Text=job.Name,Bounds=new Rectangle(22,14,1046,36),Font=Theme.Font(23,true),AutoEllipsis=true,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};Controls.Add(title);
                state=new Label{Bounds=new Rectangle(22,58,1046,46),AutoEllipsis=true,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};Controls.Add(state);
                path=new Label{Bounds=new Rectangle(22,105,1046,26),ForeColor=Theme.Muted,AutoEllipsis=true,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};Controls.Add(path);
                log=new RichTextBox{Bounds=new Rectangle(22,145,1056,499),BackColor=Theme.Input,ForeColor=Theme.Ink,ReadOnly=true,WordWrap=false,DetectUrls=false,Font=new Font("Cascadia Mono",14,FontStyle.Regular,GraphicsUnit.Pixel),BorderStyle=BorderStyle.FixedSingle,Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right};Controls.Add(log);
                follow=new CheckBox{Text="实时刷新并滚动到底部",Checked=true,Bounds=new Rectangle(22,666,250,38),Anchor=AnchorStyles.Bottom|AnchorStyles.Left};Controls.Add(follow);
                hint=new Label{Text="显示最新日志；完整内容保存在日志文件。",Bounds=new Rectangle(275,669,430,30),ForeColor=Theme.Muted,Anchor=AnchorStyles.Bottom|AnchorStyles.Left};Controls.Add(hint);
                var open=new ActionButton("打开完整文件",false){Bounds=new Rectangle(776,662,160,44),Anchor=AnchorStyles.Bottom|AnchorStyles.Right};Controls.Add(open);
                open.Click+=delegate{if(File.Exists(job.LogPath))System.Diagnostics.Process.Start("notepad.exe","\""+job.LogPath+"\"");};
                var close=new ActionButton("关闭",true){Bounds=new Rectangle(949,662,128,44),Anchor=AnchorStyles.Bottom|AnchorStyles.Right};Controls.Add(close);close.Click+=delegate{Close();};
                Resize+=delegate{hint.Width=Math.Max(80,open.Left-hint.Left-14);};
                timer=new System.Windows.Forms.Timer{Interval=500};timer.Tick+=delegate{RefreshLog();};Shown+=delegate{RefreshLog();timer.Start();};
            }

            void RefreshLog()
            {
                state.Text="状态："+job.State+"    阶段："+job.Stage+"    ID："+job.AvatarId+Environment.NewLine+VrcaClient.ProtectDiagnosticText(job.Error??"");
                path.Text=job.LogPath??"日志尚未创建";
                if(!follow.Checked)return;
                try
                {
                    string next=tail.Read(job.LogPath);
                    if(next.Length==0)return;
                    log.AppendText(next);
                    if(log.TextLength>600000){log.Select(0,log.TextLength-400000);log.SelectedText="";hint.Text="界面保留最新片段；完整内容请打开文件。";}
                    log.SelectionStart=log.TextLength;log.SelectionLength=0;log.ScrollToCaret();
                }
                catch(IOException ex){hint.Text="等待日志文件："+ex.Message;}
                catch(UnauthorizedAccessException ex){hint.Text=ex.Message;}
            }

            protected override void Dispose(bool disposing)
            {
                if(disposing){timer.Dispose();tail.Dispose();}base.Dispose(disposing);
            }
        }
    }

    public sealed class WorkspaceLogTail : IDisposable
    {
        string current;
        StreamReader reader;
        public string Read(string path)
        {
            if(string.IsNullOrEmpty(path) || !File.Exists(path))return "";
            string prefix="";
            if(reader==null || current!=path || reader.BaseStream.Length<reader.BaseStream.Position)
            {
                Dispose();current=path;
                var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                bool skip=file.Length>262144;
                if(skip)file.Seek(-262144,SeekOrigin.End);
                reader=new StreamReader(file,Encoding.UTF8,true,4096);
                if(skip){reader.ReadLine();prefix="[VIEW] 显示最近 256 KiB，完整历史请打开文件。"+Environment.NewLine;}
            }
            var chars=new char[65536];int read=reader.Read(chars,0,chars.Length);
            return prefix+new string(chars,0,read);
        }
        public void Dispose(){if(reader!=null)reader.Dispose();reader=null;current=null;}
    }
}
