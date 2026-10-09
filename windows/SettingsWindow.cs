using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexQuotaBar
{
    sealed class SettingsView
    {
        public string Theme, Bucket, Status, Error, Preview, AutoStartError;
        public int Detail;
        public bool Dark, Paused, Alerts, AutoStart, Refreshing, Stale, TargetFound, AutoStartBusy;
        public bool AutoStartKnown=true;
        public DateTime? Updated;
        public DateTime RetryUtc;
        public Dictionary<string,string> Buckets=new Dictionary<string,string>();
        public List<QuotaWindow> Windows=new List<QuotaWindow>();
    }
    sealed class BucketOption
    {
        public string Id, Label;
        public override string ToString() { return Label; }
    }

    // Measure content at its actual width, including Chinese wrapping and DPI.
    // The page reads cached quota state; its timer never requests the server.
    sealed class SettingsWindow : Form
    {
        readonly Func<SettingsView> read;
        readonly Func<string,object,string> apply;
        readonly Func<Task> refresh;
        readonly Action<string> command;
        readonly Timer ticker=new Timer { Interval=1000 };
        readonly ToolTip tips=new ToolTip { AutoPopDelay=20000 };
        readonly List<Font> fonts=new List<Font>();
        readonly List<Button> nav=new List<Button>();
        readonly List<RoundedCard> cards=new List<RoundedCard>();
        readonly List<QuotaTile> tiles=new List<QuotaTile>();
        readonly Dictionary<string,ToggleSwitch> switches=new Dictionary<string,ToggleSwitch>();
        readonly Dictionary<string,Label> switchStates=new Dictionary<string,Label>();
        Panel sidebar, header, footer;
        FlowLayoutPanel body;
        Label title, subtitle, feedback, statusLabel, updatedLabel, previewLabel;
        Button refreshButton;
        ComboBox bucketChoice, themeChoice, detailChoice;
        SettingsView view;
        string page="overview", bucketKey, message="设置修改后立即生效并自动保存";
        float unit=1;
        bool updating, paletteDark, layingOut, rebuilding, pendingRefresh, pendingStartup, messageFailure;
        int shellFontCount;
        int scrollbarWidth;
        Color canvas, cardColor, ink, muted, border, accent;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index,uint dpi);

        public SettingsWindow(Func<SettingsView> read,Func<string,object,string> apply,Func<Task> refresh,Action<string> command,Rectangle workArea)
        {
            this.read=read; this.apply=apply; this.refresh=refresh; this.command=command;
            Text="Codex 额度条 · 设置"; Name="QuotaSettings";
            ShowIcon=false; ShowInTaskbar=true; DoubleBuffered=true;
            AutoScaleMode=AutoScaleMode.None; StartPosition=FormStartPosition.Manual;
            KeyPreview=true; KeyDown += delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) { Close(); e.Handled=true; e.SuppressKeyPress=true; } };
            unit=Math.Max(1,Native.Dpi(Handle)/96F); FitInitialWindow(workArea);
            var monitorUnit=Math.Max(1,Native.Dpi(Handle)/96F);
            if(Math.Abs(monitorUnit-unit)>0.01) { unit=monitorUnit; FitInitialWindow(workArea); }
            view=read(); BuildShell();
            ticker.Tick += delegate { UpdateLive(); }; ticker.Start();
        }
        int S(float value) { return (int)Math.Round(value*unit); }
        void FitInitialWindow(Rectangle area)
        {
            MinimumSize=new Size(Math.Min(S(560),area.Width),Math.Min(S(420),area.Height));
            Size=new Size(Math.Min(S(840),area.Width),Math.Min(S(720),area.Height));
            Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
        }
        Font Face(float size,bool bold)
        {
            var font=new Font("Microsoft YaHei UI",size*unit,bold ? FontStyle.Bold : FontStyle.Regular,GraphicsUnit.Pixel);
            fonts.Add(font); return font;
        }
        void DisposeFonts(int keep) { for(int i=fonts.Count-1;i>=keep;i--) { fonts[i].Dispose(); fonts.RemoveAt(i); } }
        void Colors()
        {
            paletteDark=view.Dark;
            // Neutral tokens match Codex's native settings rather than a separate
            // colored dashboard. Accent is reserved for quota data and feedback.
            canvas=view.Dark ? Color.FromArgb(33,33,33) : Color.White;
            cardColor=canvas;
            ink=view.Dark ? Color.White : Color.FromArgb(13,13,13);
            muted=view.Dark ? Color.FromArgb(205,205,205) : Color.FromArgb(93,93,93);
            border=view.Dark ? Color.FromArgb(66,66,66) : Color.FromArgb(230,230,230);
            accent=view.Dark ? Color.FromArgb(106,206,163) : Color.FromArgb(0,120,87);
            int dark=view.Dark ? 1 : 0; try { DwmSetWindowAttribute(Handle,20,ref dark,sizeof(int)); } catch { }
        }
        Label TextLabel(string text,float size,bool bold,bool quiet)
        {
            return new Label { Text=text,AutoSize=false,AutoEllipsis=false,UseMnemonic=false,
                Font=Face(size,bold),ForeColor=quiet ? muted : ink,BackColor=Color.Transparent };
        }
        static int LabelHeight(Label label,int width) { return label.GetPreferredSize(new Size(Math.Max(1,width),0)).Height; }
        Button ActionButton(string text,Action click,bool primary)
        {
            var button=new Button { Text=text,Name=text,AccessibleName=text,Size=new Size(S(140),S(38)),
                Font=Face(13,false),FlatStyle=FlatStyle.Flat,ForeColor=primary ? canvas : ink,
                BackColor=primary ? ink : cardColor,Cursor=Cursors.Hand,UseVisualStyleBackColor=false };
            button.FlatAppearance.BorderColor=border; button.FlatAppearance.BorderSize=primary ? 0 : 1;
            button.Click += delegate { click(); }; return button;
        }
        void BuildShell()
        {
            if(rebuilding || IsDisposed) return; rebuilding=true;
            int scroll=body==null ? 0 : -body.AutoScrollPosition.Y;
            bool hadFocus=ContainsFocus;
            string focus=ActiveControl==null ? null : ActiveControl.Name;
            SuspendLayout();
            try {
                foreach(Control control in Controls.Cast<Control>().ToArray()) control.Dispose(); Controls.Clear(); DisposeFonts(0);
                nav.Clear(); cards.Clear(); tiles.Clear(); switches.Clear(); switchStates.Clear();
                Colors(); BackColor=canvas; Font=Face(14,false);
                try { scrollbarWidth=GetSystemMetricsForDpi(2,(uint)Native.Dpi(Handle)); }
                catch { scrollbarWidth=Math.Max(S(18),SystemInformation.VerticalScrollBarWidth); }
                sidebar=new Panel { BackColor=view.Dark ? Color.FromArgb(24,24,24) : Color.FromArgb(249,249,249) }; Controls.Add(sidebar);
                var brand=TextLabel("Codex",22,true,false); brand.Name="Brand";
                brand.Bounds=new Rectangle(S(22),S(28),S(132),S(46)); sidebar.Controls.Add(brand);
                var name=TextLabel("额度条设置",13,false,true); name.Bounds=new Rectangle(S(22),S(84),S(132),S(30)); sidebar.Controls.Add(name);
                string[] keys={"overview","display","alerts","runtime"}, names={"额度概览","外观显示","额度提醒","运行设置"};
                for(int i=0;i<keys.Length;i++) {
                    var key=keys[i]; var button=ActionButton(names[i],delegate { SelectPage(key); },false);
                    button.Bounds=new Rectangle(S(12),S(140+i*46),S(142),S(38)); button.Tag=key;
                    button.TextAlign=ContentAlignment.MiddleLeft; button.Padding=new Padding(S(12),0,0,0); button.FlatAppearance.BorderSize=0;
                    sidebar.Controls.Add(button); nav.Add(button);
                }
                var version=TextLabel("版本 "+Program.Version,10,false,true); version.Name="Version"; sidebar.Controls.Add(version);
                sidebar.Resize += delegate { version.Bounds=new Rectangle(S(22),sidebar.Height-S(48),S(132),S(36)); };
                header=new Panel(); Controls.Add(header);
                title=TextLabel("",22,true,false); subtitle=TextLabel("",13,false,true); header.Controls.Add(title); header.Controls.Add(subtitle);
                footer=new Panel(); Controls.Add(footer);
                feedback=TextLabel(message,11,false,true); feedback.Name="Feedback"; footer.Controls.Add(feedback);
                body=new FlowLayoutPanel { FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,
                    Padding=new Padding(S(24),S(4),S(24),S(20)),BackColor=canvas };
                Controls.Add(body);
                shellFontCount=fonts.Count; LayoutShell(); BuildPage(); Notice(message,messageFailure);
                body.AutoScrollPosition=new Point(0,scroll);
                if(hadFocus && focus!=null) { var restored=FindControl(this,focus); if(restored!=null && restored.CanFocus) restored.Focus(); }
            } finally { rebuilding=false; ResumeLayout(true); }
        }
        static Control FindControl(Control parent,string name)
        {
            foreach(Control child in parent.Controls) { if(child.Name==name) return child; var nested=FindControl(child,name); if(nested!=null) return nested; } return null;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if(body!=null && !layingOut && !rebuilding) LayoutShell(); }
        void LayoutShell()
        {
            if(layingOut || body==null || body.IsDisposed) return; layingOut=true;
            try {
                int left=S(166),right=Math.Max(S(150),ClientSize.Width-left),width=Math.Max(1,right-S(48));
                int titleHeight=LabelHeight(title,width),subHeight=LabelHeight(subtitle,width);
                int headerHeight=S(22)+titleHeight+S(10)+subHeight+S(22);
                int feedbackHeight=LabelHeight(feedback,width),footerHeight=S(14)+feedbackHeight+S(16);
                sidebar.Bounds=new Rectangle(0,0,left,ClientSize.Height);
                header.Bounds=new Rectangle(left,0,right,headerHeight);
                title.Bounds=new Rectangle(S(24),S(22),width,titleHeight);
                subtitle.Bounds=new Rectangle(S(24),S(22)+titleHeight+S(10),width,subHeight);
                footer.Bounds=new Rectangle(left,Math.Max(headerHeight,ClientSize.Height-footerHeight),right,footerHeight);
                feedback.Bounds=new Rectangle(S(24),S(14),width,feedbackHeight);
                body.Bounds=new Rectangle(left,headerHeight,right,Math.Max(0,footer.Top-headerHeight));
                int cardWidth=CardWidth();
                foreach(var card in cards) if(!card.IsDisposed) { card.Width=cardWidth; card.PerformLayout(); }
            } finally { layingOut=false; }
        }
        // Use the outer width so scrollbar visibility cannot feed back into layout.
        int CardWidth() { return Math.Max(1,body.Width-body.Padding.Horizontal-scrollbarWidth); }
        void ApplyDpi(float next)
        {
            var area=Screen.FromHandle(Handle).WorkingArea;
            unit=Math.Max(1,next); MinimumSize=new Size(Math.Min(S(560),area.Width),Math.Min(S(420),area.Height)); BuildShell();
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if(m.Msg==0x02E0 && body!=null && !IsDisposed) ApplyDpi(Native.Dpi(Handle)/96F);
        }
        RoundedCard Card(string heading,string note)
        {
            var card=new RoundedCard { Fill=cardColor,Stroke=border,Unit=unit,Margin=new Padding(0,0,0,S(16)),BackColor=canvas,
                Width=CardWidth() };
            card.Heading=TextLabel(heading,15,true,false); card.Controls.Add(card.Heading);
            if(note!=null) card.AddContent(TextLabel(note,13,false,true));
            body.Controls.Add(card); cards.Add(card); return card;
        }
        void AddText(RoundedCard card,string text,float size,bool quiet) { card.AddContent(TextLabel(text,size,false,quiet)); }
        void AddActions(RoundedCard card,params Button[] buttons)
        {
            var row=new ActionRow { Unit=unit,BackColor=Color.Transparent }; row.Controls.AddRange(buttons); card.AddContent(row);
        }
        void SelectPage(string selected) { page=selected; BuildPage(); }
        void BuildPage()
        {
            body.SuspendLayout();
            try {
                foreach(Control control in body.Controls.Cast<Control>().ToArray()) control.Dispose(); body.Controls.Clear(); DisposeFonts(shellFontCount);
                cards.Clear(); tiles.Clear(); switches.Clear(); switchStates.Clear();
                statusLabel=null; updatedLabel=null; previewLabel=null; refreshButton=null; bucketChoice=null; themeChoice=null; detailChoice=null; bucketKey=null;
                body.AutoScrollPosition=Point.Empty;
                foreach(var button in nav) {
                    bool selected=(string)button.Tag==page;
                    button.BackColor=selected ? (view.Dark ? Color.FromArgb(48,48,48) : Color.FromArgb(235,235,235)) : sidebar.BackColor;
                    button.ForeColor=selected ? ink : muted;
                }
                if(page=="overview") Overview(); if(page=="display") DisplayPage(); if(page=="alerts") AlertsPage(); if(page=="runtime") RuntimePage();
                UpdateControls(); LayoutShell();
            } finally { body.ResumeLayout(true); }
        }
        ComboBox Choice(RoundedCard card,string name,object[] values,Action<ComboBox> changed)
        {
            var box=new ComboBox { Name=name,AccessibleName=name,DropDownStyle=ComboBoxStyle.DropDownList,Font=Face(12,false),
                BackColor=cardColor,ForeColor=ink,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=S(26),IntegralHeight=false,DropDownHeight=S(220) };
            box.DrawItem += delegate(object sender,DrawItemEventArgs e) {
                if(e.Index<0 || e.Index>=box.Items.Count) return;
                bool selected=(e.State&DrawItemState.Selected)!=0;
                using(var brush=new SolidBrush(selected ? (view.Dark ? Color.FromArgb(37,65,59) : Color.FromArgb(222,242,235)) : cardColor)) e.Graphics.FillRectangle(brush,e.Bounds);
                var bounds=e.Bounds; bounds.X+=S(6); bounds.Width-=S(6);
                TextRenderer.DrawText(e.Graphics,Convert.ToString(box.Items[e.Index]),box.Font,bounds,ink,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
                if((e.State&DrawItemState.Focus)!=0) e.DrawFocusRectangle();
            };
            box.Items.AddRange(values); card.AddContent(box);
            box.SelectedIndexChanged += delegate { if(!updating && box.SelectedIndex>=0) { tips.SetToolTip(box,Convert.ToString(box.SelectedItem)); changed(box); } };
            box.DropDown += delegate {
                int width=box.Width;
                foreach(var value in box.Items) width=Math.Max(width,TextRenderer.MeasureText(Convert.ToString(value),box.Font).Width+S(40));
                box.DropDownWidth=Math.Min(width,Screen.FromControl(box).WorkingArea.Width-S(24));
            };
            return box;
        }
        void Overview()
        {
            title.Text="额度概览"; subtitle.Text="查看当前剩余额度与重置时间";
            var connection=Card("连接状态",null);
            statusLabel=TextLabel("",12,false,false); connection.AddContent(statusLabel);
            updatedLabel=TextLabel("",11,false,true); connection.AddContent(updatedLabel);
            refreshButton=ActionButton("立即刷新",async delegate { await RefreshClicked(); },true); refreshButton.Width=S(112);
            connection.HeaderAccessory=refreshButton; connection.Controls.Add(refreshButton);
            var quota=Card("当前额度","按账户实际返回的额度显示；重置时间采用北京时间。");
            bucketChoice=Choice(quota,"额度类型",new object[0],delegate(ComboBox box) { var option=box.SelectedItem as BucketOption; if(option!=null) Change("bucket",option.Id); });
            for(int i=0;i<Math.Max(1,view.Windows.Count);i++) {
                var tile=new QuotaTile { Font=Face(12,false),LargeFont=Face(25,true),ForeColor=ink,Muted=muted,Accent=accent,TrackColor=border,BackColor=cardColor,Unit=unit };
                quota.AddContent(tile); tiles.Add(tile);
            }
            var help=Card("随 Codex 自动显示","额度条在 Codex 前台时显示。左键拖动调整位置，右键打开此窗口。");
            AddText(help,"前台约每分钟更新，后台适当降低刷新频率。",11,true);
        }
        void DisplayPage()
        {
            title.Text="外观显示"; subtitle.Text="让额度条适合你的聊天界面";
            var preview=Card("额度条预览","随设置即时更新。实际额度条会根据聊天窗口宽度自动精简。");
            previewLabel=TextLabel("",12,true,false); previewLabel.ForeColor=accent; preview.AddContent(previewLabel);
            var theme=Card("主题","自动主题会融入 Codex 背景。");
            themeChoice=Choice(theme,"主题",new object[]{"自动 · 融入 Codex 背景","深色","浅色"},box=>Change("theme",new[]{"auto","dark","light"}[box.SelectedIndex]));
            var detail=Card("显示内容","空间不足时优先保留额度数字，悬停仍可查看完整信息。");
            detailChoice=Choice(detail,"显示内容",new object[]{"仅额度","额度和倒计时","完整信息"},box=>Change("detail",box.SelectedIndex));
            var position=Card("显示位置","拖动额度条可微调位置；恢复后会重新对齐输入框工具栏。");
            AddActions(position,ActionButton("恢复默认位置",delegate { Change("position",null); },false));
        }
        void ToggleCard(string heading,string note,string name)
        {
            var card=Card(heading,note);
            var toggle=new ToggleSwitch { Name=name,AccessibleName=heading,Accent=ink,OffColor=border,BackColor=cardColor,ForeColor=canvas,
                Size=new Size(S(56),S(30)),Cursor=Cursors.Hand };
            card.HeaderAccessory=toggle; card.Controls.Add(toggle); switches[name]=toggle;
            var state=TextLabel("",11,false,true); card.AddContent(state); switchStates[name]=state;
            toggle.CheckedChanged += async delegate {
                if(updating) return;
                if(name=="AutoStart") await StartupClicked(toggle.Checked);
                else Change(name=="Paused" ? "paused" : "alerts",toggle.Checked);
            };
        }
        void AlertsPage()
        {
            title.Text="额度提醒"; subtitle.Text="在接近额度上限时及时获得提醒";
            ToggleCard("低额度提醒","剩余额度降至 20% 或 10% 时，通过 Windows 通知提醒。","LowQuotaAlerts");
            var rules=Card("提醒方式","每个额度窗口在同一重置周期内，每个阈值最多提醒一次。");
            AddText(rules,"20%  ·  提前留意剩余使用量\n10%  ·  准备等待额度重置\n重置时间未知时，暂不发送阈值提醒。",12,false);
        }
        void RuntimePage()
        {
            title.Text="运行设置"; subtitle.Text="管理启动方式、显示状态和程序文件";
            ToggleCard("随 Windows 登录启动","登录后在后台等待 Codex 打开，自动显示额度条。","AutoStart");
            ToggleCard("暂时隐藏额度条","仅影响当前运行。重新启动会恢复显示；可通过系统托盘打开设置取消隐藏。","Paused");
            var files=Card("帮助与备份","程序更新时自动备份，现有偏好设置会保留。");
            AddActions(files,ActionButton("打开使用说明",delegate { RunCommand("docs"); },false),ActionButton("打开备份目录",delegate { RunCommand("backups"); },false));
            var exit=Card("退出额度条","退出会停止额度条和后台更新。关闭此设置窗口会继续运行。");
            AddActions(exit,ActionButton("退出额度条",delegate { RunCommand("exit"); },false));
        }
        void Change(string key,object value)
        {
            string failure; try { failure=apply(key,value); } catch { failure="设置未能完成，请稍后重试。"; }
            if(IsDisposed) return;
            Notice(failure ?? (key=="position" ? "已恢复默认位置" : key=="paused" ? "显示状态已更新" : "已保存，设置已生效"),failure!=null);
            UpdateLive();
        }
        async Task StartupClicked(bool enabled)
        {
            if(pendingStartup) return; pendingStartup=true; UpdateControls();
            string failure=null;
            try { failure=await Task.Run(()=>apply("autostart",enabled)); }
            catch { failure="自动启动设置失败，请稍后重试。"; }
            OnUi(delegate { pendingStartup=false; Notice(failure ?? "自动启动设置已保存",failure!=null); UpdateLive(); });
        }
        async Task RefreshClicked()
        {
            if(pendingRefresh || view.Refreshing) return;
            pendingRefresh=true; UpdateControls();
            string failure=null;
            try { await refresh(); } catch { failure="刷新失败，请稍后重试。"; }
            OnUi(delegate {
                pendingRefresh=false; var current=read(); var error=failure ?? current.Error;
                Notice(error ?? "额度已更新",error!=null); UpdateLive();
            });
        }
        void OnUi(Action action)
        {
            if(IsDisposed || Disposing || !IsHandleCreated) return;
            if(InvokeRequired) { try { BeginInvoke((Action)delegate { if(!IsDisposed && !Disposing) action(); }); } catch(InvalidOperationException) { } }
            else action();
        }
        void Notice(string text,bool failure)
        {
            message=text; messageFailure=failure;
            if(feedback==null || feedback.IsDisposed) return;
            feedback.Text=text; feedback.ForeColor=failure ? Color.FromArgb(226,133,97) : accent; LayoutShell();
        }
        void RunCommand(string name) { try { command(name); } catch { if(!IsDisposed) Notice("无法打开，请检查文件是否存在。",true); } }
        public void UpdateLive()
        {
            if(InvokeRequired) { OnUi(UpdateLive); return; }
            if(IsDisposed) return; view=read();
            if(view.Dark!=paletteDark) { BuildShell(); return; }
            if(page=="overview" && Math.Max(1,view.Windows.Count)!=tiles.Count) { BuildPage(); return; }
            UpdateControls();
        }
        void UpdateControls()
        {
            updating=true;
            try {
                if(statusLabel!=null) {
                    statusLabel.Text=view.Error ?? (pendingRefresh || view.Refreshing ? "正在更新额度…" : view.Paused ? "额度条已暂时隐藏" : !view.TargetFound ? "等待 Codex 打开" : view.Stale ? "数据待更新" : !view.Updated.HasValue ? "正在连接 Codex…" : view.Windows.Count==0 ? "账户暂未返回额度数据" : "已连接 Codex · 自动更新中");
                    statusLabel.ForeColor=view.Error!=null || view.Stale ? Color.FromArgb(226,133,97) : ink;
                    updatedLabel.Text=(view.Updated.HasValue ? "上次更新 "+view.Updated.Value.ToString("MM/dd HH:mm:ss") : "尚未读取到额度数据")+
                        (view.Error!=null ? "\n下次重试 "+view.RetryUtc.ToLocalTime().ToString("HH:mm:ss") : "");
                    refreshButton.Enabled=!pendingRefresh && !view.Refreshing; refreshButton.Text=refreshButton.Enabled ? "立即刷新" : "正在刷新…";
                }
                if(bucketChoice!=null) {
                    var key=String.Join("|",view.Buckets.Select(pair=>pair.Key+":"+pair.Value))+"#"+view.Bucket;
                    if(bucketKey!=key) {
                        bucketChoice.Items.Clear();
                        foreach(var pair in view.Buckets) bucketChoice.Items.Add(new BucketOption { Id=pair.Key,Label=pair.Value });
                        if(bucketChoice.Items.Count==0) bucketChoice.Items.Add("等待额度数据");
                        int selected=0;
                        for(int i=0;i<bucketChoice.Items.Count;i++) { var option=bucketChoice.Items[i] as BucketOption; if(option!=null && option.Id==view.Bucket) selected=i; }
                        bucketChoice.SelectedIndex=selected; bucketChoice.Enabled=view.Buckets.Count>1; bucketKey=key;
                        tips.SetToolTip(bucketChoice,Convert.ToString(bucketChoice.SelectedItem));
                    }
                    for(int i=0;i<tiles.Count;i++) tiles[i].SetQuota(i<view.Windows.Count ? view.Windows[i] : null,view.Stale);
                }
                if(previewLabel!=null) previewLabel.Text=view.Preview;
                if(themeChoice!=null) themeChoice.SelectedIndex=view.Theme=="dark" ? 1 : view.Theme=="light" ? 2 : 0;
                if(detailChoice!=null) detailChoice.SelectedIndex=Math.Max(0,Math.Min(2,view.Detail));
                foreach(var pair in switches) {
                    bool state=pair.Key=="AutoStart" ? view.AutoStart : pair.Key=="Paused" ? view.Paused : view.Alerts;
                    pair.Value.Checked=state;
                    bool waiting=pair.Key=="AutoStart" && (pendingStartup || view.AutoStartBusy || !view.AutoStartKnown);
                    bool failed=pair.Key=="AutoStart" && view.AutoStartError!=null;
                    pair.Value.Enabled=!waiting && !failed;
                    switchStates[pair.Key].Text=failed ? view.AutoStartError : waiting ? (pendingStartup ? "正在保存…" : "正在读取启动状态…") : state ? "已开启" : "已关闭";
                }
            } finally { updating=false; }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { ticker.Stop(); ticker.Dispose(); tips.Dispose(); }
            base.Dispose(disposing); if(disposing) DisposeFonts(0);
        }
    }

    sealed class RoundedCard : Panel
    {
        public Color Fill,Stroke; public float Unit=1;
        public Label Heading;
        Control headerAccessory;
        Size accessoryNatural;
        public Control HeaderAccessory { get { return headerAccessory; } set { headerAccessory=value; accessoryNatural=value==null ? Size.Empty : value.Size; } }
        readonly List<Control> content=new List<Control>();
        bool arranging;
        public RoundedCard() { DoubleBuffered=true; ResizeRedraw=true; }
        int S(float value) { return (int)Math.Round(value*Unit); }
        public void AddContent(Control control)
        {
            content.Add(control); Controls.Add(control);
            control.TextChanged += delegate { if(!IsDisposed) PerformLayout(); }; PerformLayout();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if(arranging || Heading==null || Width<=0) return; arranging=true;
            try {
                int width=Math.Max(1,Width-S(44)),y=S(18);
                bool stack=HeaderAccessory!=null && Heading.GetPreferredSize(Size.Empty).Width+accessoryNatural.Width+S(14)>width;
                int accessoryWidth=Math.Min(width,accessoryNatural.Width);
                int accessoryHeight=HeaderAccessory==null ? 0 : Math.Max(accessoryNatural.Height,HeaderAccessory.GetPreferredSize(new Size(accessoryWidth,0)).Height);
                int headingWidth=HeaderAccessory==null || stack ? width : Math.Max(1,width-accessoryWidth-S(14));
                int headingHeight=Heading.GetPreferredSize(new Size(headingWidth,0)).Height;
                if(stack) {
                    Heading.Bounds=new Rectangle(S(22),y,headingWidth,headingHeight);
                    y+=headingHeight+S(12);
                    HeaderAccessory.Bounds=new Rectangle(S(22),y,accessoryWidth,accessoryHeight);
                    y+=accessoryHeight+S(14);
                } else {
                    int rowHeight=Math.Max(headingHeight,accessoryHeight);
                    Heading.Bounds=new Rectangle(S(22),y+(rowHeight-headingHeight)/2,headingWidth,headingHeight);
                    if(HeaderAccessory!=null) HeaderAccessory.Bounds=new Rectangle(Width-S(22)-accessoryWidth,y+(rowHeight-accessoryHeight)/2,accessoryWidth,accessoryHeight);
                    y+=rowHeight+S(14);
                }
                foreach(var control in content) {
                    int height; var label=control as Label;
                    if(label!=null) height=label.GetPreferredSize(new Size(width,0)).Height;
                    else if(control is ComboBox) height=control.PreferredSize.Height;
                    else height=control.GetPreferredSize(new Size(width,0)).Height;
                    control.Bounds=new Rectangle(S(22),y,width,Math.Max(1,height));
                    control.PerformLayout(); y+=height+S(12);
                }
                Height=y-S(12)+S(20);
            } finally { arranging=false; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=BarDesign.Round(new RectangleF(0.5F,0.5F,Width-1,Height-1),S(8)))
            using(var brush=new SolidBrush(Fill)) using(var pen=new Pen(Stroke)) { e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path); }
            base.OnPaint(e);
        }
    }
    sealed class ActionRow : Panel
    {
        public float Unit=1;
        readonly Dictionary<Control,Size> naturalSizes=new Dictionary<Control,Size>();
        bool arranging;
        int S(float n) { return (int)Math.Round(n*Unit); }
        protected override void OnControlAdded(ControlEventArgs e) { naturalSizes[e.Control]=e.Control.Size; base.OnControlAdded(e); }
        protected override void OnControlRemoved(ControlEventArgs e) { naturalSizes.Remove(e.Control); base.OnControlRemoved(e); }
        Size ActionSize(Control button,int available)
        {
            Size natural; if(!naturalSizes.TryGetValue(button,out natural)) natural=button.Size;
            int width=Math.Min(natural.Width,Math.Max(1,available));
            int height=Math.Max(natural.Height,button.GetPreferredSize(new Size(width,0)).Height);
            return new Size(width,height);
        }
        public override Size GetPreferredSize(Size proposed)
        {
            int x=0,y=0,height=0,width=Math.Max(1,proposed.Width);
            foreach(Control button in Controls) {
                var size=ActionSize(button,width);
                if(x>0 && x+size.Width>width) { x=0; y+=height+S(10); height=0; }
                height=Math.Max(height,size.Height); x+=size.Width+S(12);
            }
            return new Size(width,y+height);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if(arranging) return; arranging=true;
            try {
                int x=0,y=0,height=0;
                foreach(Control button in Controls) {
                    var size=ActionSize(button,ClientSize.Width);
                    if(x>0 && x+size.Width>ClientSize.Width) { x=0; y+=height+S(10); height=0; }
                    button.Bounds=new Rectangle(new Point(x,y),size); height=Math.Max(height,size.Height); x+=size.Width+S(12);
                }
            } finally { arranging=false; }
        }
    }
    sealed class QuotaTile : Control
    {
        public QuotaWindow Quota; public bool Stale; public Font LargeFont; public Color Muted,Accent,TrackColor; public float Unit=1;
        string resetText="等待账户返回数据",renderKey;
        static readonly TextFormatFlags Wrap=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl;
        public QuotaTile() { DoubleBuffered=true; AccessibleRole=AccessibleRole.StaticText; }
        int S(float n) { return (int)Math.Round(n*Unit); }
        public void SetQuota(QuotaWindow quota,bool stale)
        {
            Quota=quota; Stale=stale;
            resetText=quota==null ? "等待账户返回数据" : quota.ResetText==null ? "接口未提供重置时间" : quota.Countdown(DateTime.UtcNow)+" · "+quota.ResetText+"（北京时间）";
            if(stale) resetText="数据待更新\n"+resetText;
            AccessibleName=(quota==null ? "额度暂不可用" : quota.Text)+" "+resetText;
            if(renderKey!=AccessibleName) { renderKey=AccessibleName; if(Parent!=null) Parent.PerformLayout(); Invalidate(); }
        }
        int TextHeight(string text,Font font,int width) { return TextRenderer.MeasureText(text,font,new Size(Math.Max(1,width),Int32.MaxValue),Wrap).Height; }
        public override Size GetPreferredSize(Size proposed)
        {
            int width=Math.Max(1,proposed.Width),heading=TextHeight(Quota==null ? "额度暂不可用" : Quota.Label,Font,width);
            return new Size(width,heading+S(4)+TextHeight("100%",LargeFont,width)+S(8)+TextHeight(resetText,Font,width)+S(18)+S(6));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var quota=Quota; string heading=quota==null ? "额度暂不可用" : quota.Label;
            string percent=quota!=null && quota.Remaining.HasValue ? quota.Remaining.Value.ToString("0.#")+"%" : "—";
            int top=0,height=TextHeight(heading,Font,Width);
            TextRenderer.DrawText(e.Graphics,heading,Font,new Rectangle(0,top,Width,height),Muted,Wrap);
            top+=height+S(4); height=TextHeight(percent,LargeFont,Width);
            TextRenderer.DrawText(e.Graphics,percent,LargeFont,new Rectangle(0,top,Width,height),ForeColor,Wrap);
            top+=height+S(8); height=TextHeight(resetText,Font,Width);
            TextRenderer.DrawText(e.Graphics,resetText,Font,new Rectangle(0,top,Width,height),Muted,Wrap);
            var track=new RectangleF(0,Height-S(6),Width,S(6));
            using(var path=BarDesign.Round(track,S(3))) using(var brush=new SolidBrush(TrackColor)) e.Graphics.FillPath(brush,path);
            if(quota!=null && quota.Remaining.HasValue && quota.Remaining.Value>0) {
                var filled=new RectangleF(track.X,track.Y,Math.Max(S(6),(float)(track.Width*quota.Remaining.Value/100)),track.Height);
                using(var path=BarDesign.Round(filled,S(3))) using(var brush=new SolidBrush(quota.Remaining.Value<=20 ? Color.FromArgb(226,133,97) : Accent)) e.Graphics.FillPath(brush,path);
            }
            base.OnPaint(e);
        }
    }
    sealed class ToggleSwitch : Button
    {
        bool selected; public Color Accent,OffColor;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return selected; } set { if(selected==value) return; selected=value; AccessibleDescription=value ? "已开启" : "已关闭"; Invalidate(); if(CheckedChanged!=null) CheckedChanged(this,EventArgs.Empty); } }
        public ToggleSwitch() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); FlatStyle=FlatStyle.Flat; }
        protected override void OnClick(EventArgs e) { Checked=!Checked; base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var rect=new RectangleF(2,2,Width-4,Height-4);
            using(var path=BarDesign.Round(rect,rect.Height/2)) using(var brush=new SolidBrush(Checked ? Accent : OffColor)) e.Graphics.FillPath(brush,path);
            float diameter=rect.Height-6;
            using(var brush=new SolidBrush(!Enabled ? Color.FromArgb(155,155,155) : Checked ? ForeColor : Color.White)) e.Graphics.FillEllipse(brush,Checked ? rect.Right-diameter-3 : rect.Left+3,rect.Top+3,diameter,diameter);
            if(Focused) ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle,ForeColor,BackColor);
        }
    }
}
