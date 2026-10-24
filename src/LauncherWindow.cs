// The launcher window. Display only: every action goes through LauncherController (LauncherCore.cs).
// Plain WinForms (no extra libraries): a dark theme with a few custom-drawn controls, sized for the monitor's DPI.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Theme {
    public static readonly Color Back=Color.FromArgb(17,20,25),Card=Color.FromArgb(27,32,40),Field=Color.FromArgb(36,42,52),
        Border=Color.FromArgb(52,60,74),Hover=Color.FromArgb(44,52,64),Text=Color.FromArgb(230,234,240),Muted=Color.FromArgb(138,148,163),
        Accent=Color.FromArgb(56,150,240),AccentHover=Color.FromArgb(84,168,248),Good=Color.FromArgb(76,195,138),Warn=Color.FromArgb(229,168,75),
        Bad=Color.FromArgb(229,83,75);
    public static float Scale=1f;
    public static int S(int px){return (int)Math.Round(px*Scale);}
    public static Font Font(float px,FontStyle style){return new Font("Segoe UI",px*Scale,style,GraphicsUnit.Pixel);}
    public static Font Semibold(float px){try{var f=new Font("Segoe UI Semibold",px*Scale,FontStyle.Regular,GraphicsUnit.Pixel);if(f.Name=="Segoe UI Semibold")return f;}catch{}return Font(px,FontStyle.Bold);}
    public static GraphicsPath Round(RectangleF r,float radius) {
        var p=new GraphicsPath();float d=radius*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    public static Color Tone(LaunchTone t){return t==LaunchTone.Good?Good:t==LaunchTone.Busy?Warn:t==LaunchTone.Error?Bad:Muted;}
}

// A rounded card; child controls sit on it with BackColor = Theme.Card.
class Card : Panel {
    public Color Fill=Theme.Card;
    public Card(){DoubleBuffered=true;BackColor=Theme.Back;}
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var p=Theme.Round(new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f),Theme.S(10)))using(var b=new SolidBrush(Fill))using(var pen=new Pen(Theme.Border)){e.Graphics.FillPath(b,p);e.Graphics.DrawPath(pen,p);}
    }
}

// Flat rounded button (primary: accent fill; otherwise outlined).
class FlatButton : Control,IButtonControl {
    public bool Primary;bool hover,down;
    public DialogResult DialogResult{get;set;}
    public FlatButton(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);Cursor=Cursors.Hand;ForeColor=Theme.Text;BackColor=Theme.Card;}
    public void NotifyDefault(bool value){}
    public void PerformClick(){if(Enabled)OnClick(EventArgs.Empty);}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=down=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){down=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){down=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnEnabledChanged(EventArgs e){Cursor=Enabled?Cursors.Hand:Cursors.Default;Invalidate();base.OnEnabledChanged(e);}
    protected override void OnTextChanged(EventArgs e){Invalidate();base.OnTextChanged(e);}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
        Color fill=Primary?(hover&&!down?Theme.AccentHover:Theme.Accent):(hover?Theme.Hover:Theme.Field),text=Theme.Text;
        if(!Enabled){fill=Primary?Color.FromArgb(40,70,100):Theme.Field;text=Theme.Muted;}
        using(var p=Theme.Round(new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f),Theme.S(8)))using(var b=new SolidBrush(fill)) {
            g.FillPath(b,p);if(!Primary)using(var pen=new Pen(Theme.Border))g.DrawPath(pen,p);
        }
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
    }
}

// A selectable tile for one way to play.
class ModeTile : Control {
    public LaunchMode Mode;public bool Selected;public string Badge;bool hover;
    public Font TitleFont,DetailFont;
    public ModeTile(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true);Cursor=Cursors.Hand;BackColor=Theme.Back;}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
        Color fill=Selected?Color.FromArgb(28,48,72):hover&&Enabled?Theme.Hover:Theme.Card,edge=Selected?Theme.Accent:Theme.Border;
        using(var p=Theme.Round(new RectangleF(1,1,Width-2.5f,Height-2.5f),Theme.S(10)))using(var b=new SolidBrush(fill))using(var pen=new Pen(edge,Selected?2f:1f)){g.FillPath(b,p);g.DrawPath(pen,p);}
        int pad=Theme.S(14),dot=Theme.S(10);
        // Radio dot.
        var rd=new Rectangle(pad,Theme.S(16),dot,dot);
        using(var pen=new Pen(Selected?Theme.Accent:Theme.Muted,1.5f))g.DrawEllipse(pen,rd);
        if(Selected)using(var b=new SolidBrush(Theme.Accent))g.FillEllipse(b,rd.X+Theme.S(3)-0.5f,rd.Y+Theme.S(3)-0.5f,dot-Theme.S(6)+1,dot-Theme.S(6)+1);
        Color text=Enabled?Theme.Text:Theme.Muted;
        int x=pad+dot+Theme.S(10);
        TextRenderer.DrawText(g,Mode.Title,TitleFont,new Point(x,Theme.S(11)),text,TextFormatFlags.NoPadding);
        if(Badge!=null) {
            var size=TextRenderer.MeasureText(Mode.Title,TitleFont,Size.Empty,TextFormatFlags.NoPadding);var bs=TextRenderer.MeasureText(Badge,DetailFont,Size.Empty,TextFormatFlags.NoPadding);
            var br=new Rectangle(x+size.Width+Theme.S(8),Theme.S(12),bs.Width+Theme.S(12),bs.Height+Theme.S(4));
            using(var p=Theme.Round(br,Theme.S(6)))using(var b=new SolidBrush(Color.FromArgb(30,76,195,138)))g.FillPath(b,p);
            TextRenderer.DrawText(g,Badge,DetailFont,new Point(br.X+Theme.S(6),br.Y+Theme.S(2)),Theme.Good,TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g,Mode.Detail,DetailFont,new Rectangle(x,Theme.S(34),Width-x-pad,Height-Theme.S(36)),Theme.Muted,TextFormatFlags.WordBreak|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);
    }
}

// On/off switch.
class Toggle : Control {
    bool on;public bool Checked{get{return on;}set{if(on!=value){on=value;Invalidate();var h=CheckedChanged;if(h!=null)h(this,EventArgs.Empty);}}}
    public event EventHandler CheckedChanged;
    public Toggle(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true);Cursor=Cursors.Hand;BackColor=Theme.Card;}
    protected override void OnClick(EventArgs e){if(Enabled)Checked=!Checked;base.OnClick(e);}
    protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
        int h=Theme.S(20),w=Theme.S(36),y=(Height-h)/2;
        using(var p=Theme.Round(new RectangleF(0.5f,y+0.5f,w-1,h-1),h/2f-0.5f))using(var b=new SolidBrush(on?(Enabled?Theme.Accent:Color.FromArgb(40,70,100)):Theme.Field))using(var pen=new Pen(on?Theme.Accent:Theme.Border)){g.FillPath(b,p);g.DrawPath(pen,p);}
        int k=h-Theme.S(6);using(var b=new SolidBrush(Enabled?Color.White:Theme.Muted))g.FillEllipse(b,on?w-k-Theme.S(3):Theme.S(3),y+Theme.S(3),k,k);
        TextRenderer.DrawText(g,Text,Font,new Rectangle(w+Theme.S(10),0,Width-w-Theme.S(10),Height),Enabled?Theme.Text:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
    }
}

// Drop-down chevron drawn over a combo box's own button.
class Chevron : Control {
    public Chevron(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);BackColor=Theme.Field;Cursor=Cursors.Hand;}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
        float cx=Width/2f,cy=Height/2f,d=Theme.S(4);
        using(var pen=new Pen(Theme.Muted,1.6f*Theme.Scale)){pen.StartCap=pen.EndCap=LineCap.Round;g.DrawLines(pen,new[]{new PointF(cx-d,cy-d/2),new PointF(cx,cy+d/2),new PointF(cx+d,cy-d/2)});}
    }
}

// A text box inside a rounded field.
class Field : Card {
    public readonly TextBox Box=new TextBox();
    public Field() {
        Fill=Theme.Field;BackColor=Theme.Card;
        Box.BorderStyle=BorderStyle.None;Box.BackColor=Theme.Field;Box.ForeColor=Theme.Text;Controls.Add(Box);
        Resize+=(s,e)=>{Box.SetBounds(Theme.S(10),(Height-Box.PreferredHeight)/2,Width-Theme.S(20),Box.PreferredHeight);};
    }
    public override string Text{get{return Box.Text;}set{Box.Text=value;}}
}

class LauncherWindow : Form {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    readonly LauncherController core=new LauncherController();
    readonly List<ModeTile> tiles=new List<ModeTile>();
    Label prepared,statusText,latency,targetLabel,targetHint,campaignNone;Panel statusDot;
    FlatButton prepare,start,stop,find,browse,details,update,saveLogs;readonly ToolTip logTip=new ToolTip();
    Field address,campaignDir,playerName;ComboBox friend,playerColor;Toggle campaign;
    TextBox log;Card targetCard,playCard,youCard;
    LaunchTone tone=LaunchTone.Idle;bool ready,showDetails;int collapsedHeight;

    public LauncherWindow() {
        try{Theme.Scale=GetDpiForSystem()/96f;}catch{Theme.Scale=1f;}
        if(Theme.Scale<1f)Theme.Scale=1f;
        Text="StarCraft II Co-op "+Program.Version;BackColor=Theme.Back;ForeColor=Theme.Text;Font=Theme.Font(13,FontStyle.Regular);
        FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;DoubleBuffered=true;
        try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
        core.LoadChoices();
        Build();
        core.StatusChanged+=(t,k)=>Ui(()=>SetStatus(t,k));
        core.LatencyChanged+=t=>Ui(()=>{latency.Text=t??"";});
        core.LogLine+=line=>Ui(()=>log.AppendText(line+Environment.NewLine));
        core.RunningChanged+=on=>Ui(()=>SetRunning(on));
        FormClosing+=(s,e)=>{if(core.StopForClose())e.Cancel=true;};
        Shown+=async (s,e)=>{
            ActiveControl=null;RefreshReady();
            // Update notice: a newer release on GitHub shows a button next to the title (opens the release page).
            var newer=await LauncherController.NewerRelease();
            if(newer!=null){update.Text="Update available: "+newer.Tag;update.Tag=newer.Url;update.Visible=true;update.BringToFront();
                Program.Log("A newer SC2Coop is out: "+newer.Tag+" ("+newer.Url+"). Both players need the same version.");}
            // Not prepared (first run, or a new version): prepare from the StarCraft II folder found by itself; Prepare… is the
            // fallback when none is found or this fails.
            string found=core.AutoPrepareFolder();
            if(found!=null){Program.Log("Found StarCraft II in "+found+"; preparing automatically.");await core.Prepare(found);}
            ulong invited;
            if(LauncherController.InviteFromCommandLine(out invited)){SelectMode("steam-join");friend.Text=invited.ToString();Program.Log("Steam invite: press Start to join lobby "+invited+".");}
            // Test hook SC2_COOP_TEST_UI=<mode id>[,single][,start]: what clicking that tile (the Campaign switch off, Start) does,
            // without the mouse.
            var test=(Environment.GetEnvironmentVariable("SC2_COOP_TEST_UI")??"").Split(',');
            if(test[0]!=""){SelectMode(test[0]);if(test.Contains("single"))campaign.Checked=false;if(test.Contains("start"))start.PerformClick();}
        };
    }

    void Ui(Action a){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke(a);}catch(InvalidOperationException){}}

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        // Windows 10 20H1+/11: dark title bar (20) and rounded corners (33); ignored where unsupported.
        try{int on=1;DwmSetWindowAttribute(Handle,20,ref on,4);int round=2;DwmSetWindowAttribute(Handle,33,ref round,4);}catch{}
    }

    Label AddLabel(Control parent,string text,int x,int y,int w,Font font,Color color) {
        var l=new Label{Text=text,Font=font,ForeColor=color,BackColor=parent==this?Theme.Back:Theme.Card,AutoSize=false};
        l.SetBounds(Theme.S(x),Theme.S(y),Theme.S(w),font.Height+Theme.S(4));parent.Controls.Add(l);return l;
    }
    Label Section(string text,int y){return AddLabel(this,text.ToUpperInvariant(),24,y,600,Theme.Semibold(11),Theme.Muted);}
    // A combo box inside a rounded field; the clip panel hides the system's 1 px flat border.
    ComboBox Combo(Control parent,int x,int y,int w,bool editable) {
        var field=new Card{Fill=Theme.Field,BackColor=Theme.Card};field.SetBounds(Theme.S(x),Theme.S(y),Theme.S(w),Theme.S(36));parent.Controls.Add(field);
        var c=new ComboBox{DropDownStyle=editable?ComboBoxStyle.DropDown:ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=Theme.Field,ForeColor=Theme.Text,Font=Font};
        var clip=new Panel{BackColor=Theme.Field};field.Controls.Add(clip);clip.Controls.Add(c);
        // Its real height depends on font and draw mode (set after this); keep the clip 1 px inside it on every side.
        Action fit=()=>{int h=c.Height;clip.SetBounds(Theme.S(6),(field.Height-h)/2+1,field.Width-Theme.S(12),h-2);c.Location=new Point(-1,-1);c.Width=clip.Width+2;};
        c.SizeChanged+=(s,e)=>fit();fit();
        // A painted chevron over the system's light drop-down button; clicking it opens the list.
        var arrow=new Chevron();arrow.SetBounds(field.Width-Theme.S(34),Theme.S(2),Theme.S(30),field.Height-Theme.S(4));field.Controls.Add(arrow);arrow.BringToFront();
        arrow.Click+=(s,e)=>{c.Focus();c.DroppedDown=true;};
        field.Tag=c;c.Tag=field;return c;
    }

    void Build() {
        const int W=760,M=24,inner=W-2*M;
        SuspendLayout();
        int y=18;
        AddLabel(this,"StarCraft II Co-op",M,y,inner,Theme.Semibold(24),Theme.Text);
        update=new FlatButton{Text="Update available",Primary=true,Font=Theme.Semibold(12),BackColor=Theme.Back,Visible=false};
        update.SetBounds(Theme.S(M+inner-250),Theme.S(y),Theme.S(250),Theme.S(34));
        update.Click+=(s,e)=>{try{System.Diagnostics.Process.Start((update.Tag as string)??LauncherController.ReleasesPage);}catch(Exception error){Program.Log("Could not open the release page: "+error.Message);}};
        Controls.Add(update);y+=36;
        AddLabel(this,"Two players share one army in the Wings of Liberty campaign. Both need StarCraft II 5.0.17 (English); nothing is copied between PCs.",M,y,inner,Theme.Font(13,FontStyle.Regular),Theme.Muted).Height=Theme.S(44);
        y+=52;

        // Setup state.
        var setup=new Card();setup.SetBounds(Theme.S(M),Theme.S(y),Theme.S(inner),Theme.S(58));Controls.Add(setup);
        statusDotSetup=new Panel{BackColor=Theme.Muted};statusDotSetup.SetBounds(Theme.S(18),Theme.S(25),Theme.S(9),Theme.S(9));Round(statusDotSetup);setup.Controls.Add(statusDotSetup);
        prepared=AddLabel(setup,"",36,19,inner-36-190,Theme.Font(13,FontStyle.Regular),Theme.Text);prepared.AutoEllipsis=true;
        prepare=new FlatButton{Text="Prepare…",Font=Font};prepare.SetBounds(Theme.S(inner-170),Theme.S(11),Theme.S(156),Theme.S(36));prepare.Click+=OnPrepare;setup.Controls.Add(prepare);
        y+=74;

        Section("How do you want to play?",y);y+=24;
        // Steam first (works through any router); address and local test below.
        int half=(inner-12)/2,third=(inner-24)/3;
        AddTile("steam-host",M,y,half,72,"Recommended");AddTile("steam-join",M+half+12,y,half,72,null);y+=84;
        AddTile("host",M,y,third,72,null);AddTile("join",M+third+12,y,third,72,null);AddTile("local",M+2*(third+12),y,third,72,null);y+=84;

        // What the chosen mode needs.
        targetCard=new Card();targetCard.SetBounds(Theme.S(M),Theme.S(y),Theme.S(inner),Theme.S(58));Controls.Add(targetCard);
        targetLabel=AddLabel(targetCard,"",18,19,150,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        address=new Field();address.SetBounds(Theme.S(170),Theme.S(11),Theme.S(inner-188),Theme.S(36));address.Box.Font=Font;address.Text=core.Address;targetCard.Controls.Add(address);
        friend=Combo(targetCard,170,11,inner-188-110,true);
        find=new FlatButton{Text="Find",Font=Font};find.SetBounds(Theme.S(inner-120),Theme.S(11),Theme.S(102),Theme.S(36));find.Click+=OnFind;targetCard.Controls.Add(find);
        targetHint=AddLabel(targetCard,"",170,19,inner-188,Theme.Font(13,FontStyle.Regular),Theme.Text);targetHint.AutoEllipsis=true;
        y+=74;

        Section("What to play",y);y+=24;
        playCard=new Card();playCard.SetBounds(Theme.S(M),Theme.S(y),Theme.S(inner),Theme.S(58));Controls.Add(playCard);
        campaign=new Toggle{Text="Campaign",Font=Font};campaign.SetBounds(Theme.S(18),Theme.S(14),Theme.S(140),Theme.S(30));playCard.Controls.Add(campaign);
        campaignDir=new Field();campaignDir.SetBounds(Theme.S(170),Theme.S(11),Theme.S(inner-188-110-110),Theme.S(36));campaignDir.Box.Font=Font;campaignDir.Text=core.CampaignDir;playCard.Controls.Add(campaignDir);
        reset=new FlatButton{Text="Reset…",Font=Font};reset.SetBounds(Theme.S(inner-230),Theme.S(11),Theme.S(102),Theme.S(36));reset.Click+=OnReset;playCard.Controls.Add(reset);
        resetTip.SetToolTip(reset,"Start this campaign over from the first mission (your co-op progress is kept in a backup folder)");
        browse=new FlatButton{Text="Browse…",Font=Font};browse.SetBounds(Theme.S(inner-120),Theme.S(11),Theme.S(102),Theme.S(36));browse.Click+=OnBrowse;playCard.Controls.Add(browse);
        campaignNone=AddLabel(playCard,"The Outlaws (single mission). Turn on Campaign to play a custom WoL campaign folder.",170,19,inner-188,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        campaign.Checked=core.Campaign;campaign.CheckedChanged+=(s,e)=>{UpdateCampaign();FillColors(SelectedColor());UpdateSeparate();};
        campaignDir.Box.TextChanged+=(s,e)=>{if(Directory.Exists(campaignDir.Text.Trim()))FillColors(SelectedColor());UpdateSeparate();};
        y+=74;

        // Separate armies: only for a campaign with missions made for it (a second human player in the map). Hidden, the
        // rows below move up by its height.
        separateTitle=Section("Separate armies",y);
        sepCard=new Card();sepCard.SetBounds(Theme.S(M),Theme.S(y+24),Theme.S(inner),Theme.S(58));Controls.Add(sepCard);
        AddLabel(sepCard,"Units the mission gives",18,19,170,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        separateUnits=Combo(sepCard,186,11,160,false);separateUnits.Items.AddRange(UnitChoiceNames);
        separateUnits.SelectedIndex=Math.Max(0,Array.IndexOf(SeparateOptions.UnitChoices,core.SeparateUnits));
        AddLabel(sepCard,"Mission money",362,19,104,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        separateMoney=Combo(sepCard,466,11,130,false);separateMoney.Items.AddRange(MoneyChoiceNames);
        separateMoney.SelectedIndex=Math.Max(0,Array.IndexOf(SeparateOptions.MoneyChoices,core.SeparateMoney));
        separateHint=AddLabel(sepCard,"Host's choice",610,20,inner-624,Theme.Font(12,FontStyle.Regular),Theme.Muted);
        y+=98;separateShift=Theme.S(98);firstBelowSeparate=Controls.Count;

        Section("You",y);y+=24;
        youCard=new Card();youCard.SetBounds(Theme.S(M),Theme.S(y),Theme.S(inner),Theme.S(58));Controls.Add(youCard);
        AddLabel(youCard,"Name",18,19,48,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        playerName=new Field();playerName.SetBounds(Theme.S(70),Theme.S(11),Theme.S(200),Theme.S(36));playerName.Box.Font=Font;playerName.Box.MaxLength=20;playerName.Text=core.PlayerName;youCard.Controls.Add(playerName);
        AddLabel(youCard,"Color",290,19,50,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        playerColor=Combo(youCard,340,11,170,false);
        playerColor.DrawMode=DrawMode.OwnerDrawFixed;playerColor.DrawItem+=DrawColorItem;
        FillColors(core.PlayerColor);playerColor.SelectedIndexChanged+=OnColorPicked;
        colorTip.SetToolTip(playerColor,"Host: the army's color. Buddy: your pings and chat.");
        // Game speed of the missions (the host's choice); -coopspeed in a mission changes it for both.
        AddLabel(youCard,"Speed",530,19,52,Theme.Font(13,FontStyle.Regular),Theme.Muted);
        gameSpeed=Combo(youCard,584,11,120,false);gameSpeed.Items.AddRange(new object[]{"Slower","Slow","Normal","Fast","Faster"});
        gameSpeed.SelectedIndex=Math.Max(0,Array.IndexOf(Program.GameSpeeds,Program.CleanSpeed(core.GameSpeed)));
        colorTip.SetToolTip(gameSpeed,"Game speed of the missions; the host's choice counts. Type -coopspeed normal (or slower, slow, fast, faster) in a mission to change it for both.");
        y+=78;

        // Status and the main buttons.
        statusDot=new Panel{BackColor=Theme.Muted};statusDot.SetBounds(Theme.S(M+2),Theme.S(y+15),Theme.S(10),Theme.S(10));Round(statusDot);Controls.Add(statusDot);
        statusText=AddLabel(this,"Not started",M+20,y+7,inner-20-300,Theme.Semibold(14),Theme.Text);statusText.AutoEllipsis=true;
        latency=AddLabel(this,"",M+20,y+27,inner-20-300,Theme.Font(12,FontStyle.Regular),Theme.Muted);
        stop=new FlatButton{Text="Stop",Font=Font,BackColor=Theme.Back,Enabled=false};stop.SetBounds(Theme.S(M+inner-280),Theme.S(y),Theme.S(110),Theme.S(44));stop.Click+=(s,e)=>core.Stop();Controls.Add(stop);
        start=new FlatButton{Text="Start",Primary=true,Font=Theme.Semibold(15),BackColor=Theme.Back};start.SetBounds(Theme.S(M+inner-160),Theme.S(y),Theme.S(160),Theme.S(44));start.Click+=OnStart;Controls.Add(start);
        AcceptButton=start;
        y+=60;
        details=new FlatButton{Text="Show details",Font=Theme.Font(12,FontStyle.Regular),BackColor=Theme.Back};details.SetBounds(Theme.S(M),Theme.S(y),Theme.S(130),Theme.S(30));details.Click+=(s,e)=>ToggleDetails();Controls.Add(details);
        saveLogs=new FlatButton{Text="Save logs…",Font=Theme.Font(12,FontStyle.Regular),BackColor=Theme.Back};saveLogs.SetBounds(Theme.S(M+140),Theme.S(y),Theme.S(130),Theme.S(30));saveLogs.Click+=OnSaveLogs;Controls.Add(saveLogs);
        logTip.SetToolTip(saveLogs,"Zip the recent launcher and StarCraft II logs into one file, to send with a bug report");
        y+=42;
        log=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Theme.Card,ForeColor=Color.FromArgb(200,206,214),Font=new Font("Consolas",12*Theme.Scale,GraphicsUnit.Pixel),Visible=false};
        log.SetBounds(Theme.S(M),Theme.S(y),Theme.S(inner),Theme.S(220));Controls.Add(log);
        collapsedHeight=fullHeight=Theme.S(y);
        ClientSize=new Size(Theme.S(W),collapsedHeight);
        for(int i=firstBelowSeparate;i<Controls.Count;i++)belowSeparate.Add(new KeyValuePair<Control,int>(Controls[i],Controls[i].Top));
        SelectMode(core.ModeId);UpdateCampaign();UpdateSeparate();
        ResumeLayout();
    }
    ComboBox gameSpeed;readonly ToolTip colorTip=new ToolTip();
    Label separateTitle,separateHint;Card sepCard;ComboBox separateUnits,separateMoney;
    int separateShift,firstBelowSeparate,fullHeight;bool separateShown=true;string separateFor;
    readonly List<KeyValuePair<Control,int>> belowSeparate=new List<KeyValuePair<Control,int>>();
    static readonly string[] UnitChoiceNames={"Who found them","Take turns","Both get them"},MoneyChoiceNames={"Both get it","Split it"};

    // Shows the separate-armies row when the campaign folder has missions made for it (checked once per folder).
    void UpdateSeparate() {
        string dir=campaign.Checked?campaignDir.Text.Trim():"";int count=0;
        if(dir!=""&&Directory.Exists(dir)) {
            if(dir!=separateFor){separateFor=dir;try{separateMissions=CampaignMaps.SeparateMissions(dir);}catch{separateMissions=new List<string>();}}
            count=separateMissions.Count;
        }
        bool show=count>0;
        if(show){separateTitle.Text=("Separate armies ("+count+(count==1?" mission":" missions")+")").ToUpperInvariant();
            separateTip.SetToolTip(sepCard,"Missions where each player has their own army: "+string.Join(", ",separateMissions));}
        if(show==separateShown)return;
        separateShown=show;separateTitle.Visible=sepCard.Visible=show;
        SuspendLayout();
        foreach(var c in belowSeparate)c.Key.Top=c.Value-(show?0:separateShift);
        collapsedHeight=fullHeight-(show?0:separateShift);
        ClientSize=new Size(ClientSize.Width,showDetails?collapsedHeight+Theme.S(236):collapsedHeight);
        ResumeLayout();
    }
    List<string> separateMissions=new List<string>();readonly ToolTip separateTip=new ToolTip();
    Panel statusDotSetup;

    static void Round(Control c){using(var p=new GraphicsPath()){p.AddEllipse(0,0,c.Width,c.Height);c.Region=new Region(p);}}

    void AddTile(string id,int x,int y,int w,int h,string badge) {
        var t=new ModeTile{Mode=LauncherController.Mode(id),Badge=badge,TitleFont=Theme.Semibold(14),DetailFont=Theme.Font(12,FontStyle.Regular)};
        t.SetBounds(Theme.S(x),Theme.S(y),Theme.S(w),Theme.S(h));t.Click+=(s,e)=>{if(!core.Running)SelectMode(id);};
        tiles.Add(t);Controls.Add(t);
    }

    void SelectMode(string id) {
        core.ModeId=LauncherController.Mode(id).Id;
        foreach(var t in tiles){t.Selected=t.Mode.Id==core.ModeId;t.Invalidate();}
        var m=LauncherController.Mode(core.ModeId);
        address.Visible=m.Address;((Control)friend.Tag).Visible=find.Visible=m.Friend;targetHint.Visible=!m.Address&&!m.Friend;
        targetLabel.Text=m.Address?"Host's address":m.Friend?"Steam friend":"Next";
        targetHint.Text=m.Id=="steam-host"?"Press Start; your friend then picks you under \"Join a Steam friend\".":
            m.Id=="host"?"Press Start; the details show the address your buddy enters.":
            m.Id=="local"?"Opens two StarCraft II windows on this PC (for testing).":"";
    }

    void UpdateCampaign(){bool on=campaign.Checked;campaignDir.Visible=browse.Visible=reset.Visible=on;campaignNone.Visible=!on;}
    FlatButton reset;readonly ToolTip resetTip=new ToolTip();

    // Reset progress: the host's co-op save of this campaign starts over (CampaignSaves.Reset keeps the old one as a backup).
    void OnReset(object sender,EventArgs e) {
        string dir=campaignDir.Text.Trim();
        if(dir==""||!Directory.Exists(dir)){MessageBox.Show(this,"Choose the campaign folder first.","Reset progress",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
        string name=CampaignSaves.Id(dir);
        if(MessageBox.Show(this,"Start \""+name+"\" over from the first mission?\n\nThe next time you host it, credits, upgrades, research and mission progress start fresh. "+
            "Only the host's progress counts, so this matters on the PC that hosts.\n\nYour current progress is moved to a backup folder, not deleted.",
            "Reset progress",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
        try {
            string backup=CampaignSaves.Reset(Program.Load(core.Config),dir);
            Program.Log(backup!=null?"Reset "+name+": the next session starts from the first mission. The old progress is in "+backup:"Reset "+name+": the next session starts from the first mission.");
        } catch(Exception error){Program.Log("ERROR: could not reset "+name+": "+error.GetBaseException().Message);}
    }

    void DrawColorItem(object sender,DrawItemEventArgs e) {
        if(e.Index<0)return;
        bool sel=(e.State&DrawItemState.Selected)!=0&&(e.State&DrawItemState.ComboBoxEdit)==0;
        using(var b=new SolidBrush(sel?Theme.Hover:Theme.Field))e.Graphics.FillRectangle(b,e.Bounds);
        int sw=Theme.S(12),x=e.Bounds.X+Theme.S(4);
        var item=playerColor.Items[e.Index] as TeamColor;
        if(item==null){TextRenderer.DrawText(e.Graphics,playerColor.Items[e.Index].ToString(),playerColor.Font,new Point(x,e.Bounds.Y+(e.Bounds.Height-playerColor.Font.Height)/2),Theme.Text,TextFormatFlags.NoPadding);return;}
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var b=new SolidBrush(item.Swatch))e.Graphics.FillEllipse(b,x,e.Bounds.Y+(e.Bounds.Height-sw)/2,sw,sw);
        TextRenderer.DrawText(e.Graphics,item.ToString(),playerColor.Font,new Point(x+sw+Theme.S(8),e.Bounds.Y+(e.Bounds.Height-playerColor.Font.Height)/2),Theme.Text,TextFormatFlags.NoPadding);
    }
    // The color list follows the selected campaign (its mods can rename, recolor and add team colors; TeamColors.cs): its named
    // colors as presets, then any RGB through "Custom color..." (added to each mission's own color table at build time).
    List<TeamColor> colors=TeamColors.Base();string colorsFor="";TeamColor custom;int lastColorItem;
    const string CustomItem="Custom color...";
    int SelectedColor(){var c=playerColor.SelectedItem as TeamColor;return c==null?-1:c.Index;}
    void FillColors(int keep) {
        string dir=campaign.Checked?campaignDir.Text.Trim():"";
        if(playerColor.Items.Count>0&&dir==colorsFor)return;
        string install=null;try{install=Program.Load(core.Config).Install;}catch{}
        colors=dir==""?TeamColors.Base():TeamColors.For(install,dir);colorsFor=dir;
        if(TeamColors.IsRgb(keep))custom=new TeamColor{Index=keep,Name="Custom "+TeamColors.Format(keep),Swatch=TeamColors.ToColor(keep,colors)};
        fillingColors=true;
        playerColor.Items.Clear();playerColor.Items.Add("Campaign default");playerColor.Items.AddRange(colors.ToArray());
        if(custom!=null)playerColor.Items.Add(custom);
        playerColor.Items.Add(CustomItem);
        int at=0;for(int i=0;i<playerColor.Items.Count;i++){var c=playerColor.Items[i] as TeamColor;if(c!=null&&c.Index==keep)at=i;}
        playerColor.SelectedIndex=lastColorItem=at;fillingColors=false;
    }
    bool fillingColors;
    void OnColorPicked(object sender,EventArgs e) {
        if(fillingColors||playerColor.SelectedIndex<0)return;
        if(!CustomItem.Equals(playerColor.SelectedItem)){lastColorItem=playerColor.SelectedIndex;return;}
        int keep=SelectedColorAt(lastColorItem);
        using(var d=new ColorDialog{FullOpen=true,AnyColor=true,Color=custom!=null?custom.Swatch:TeamColors.ToColor(keep,colors)}) {
            if(d.ShowDialog(this)==DialogResult.OK){custom=null;keep=TeamColors.FromRgb(d.Color);colorsFor=null;}
            BeginInvoke((Action)(()=>{if(colorsFor==null)FillColors(keep);else{fillingColors=true;playerColor.SelectedIndex=lastColorItem;fillingColors=false;}}));
        }
    }
    int SelectedColorAt(int i){var c=i>=0&&i<playerColor.Items.Count?playerColor.Items[i] as TeamColor:null;return c==null?-1:c.Index;}

    void ToggleDetails() {
        showDetails=!showDetails;log.Visible=showDetails;details.Text=showDetails?"Hide details":"Show details";
        ClientSize=new Size(ClientSize.Width,showDetails?collapsedHeight+Theme.S(236):collapsedHeight);
        if(showDetails){log.SelectionStart=log.TextLength;log.ScrollToCaret();}
    }

    void SetStatus(string text,LaunchTone t) {
        tone=t;statusText.Text=text;statusDot.BackColor=Theme.Tone(t);
        // Errors open the details, where the full log is.
        if(t==LaunchTone.Error&&!showDetails)ToggleDetails();
    }

    void RefreshReady() {
        string text;ready=core.Ready(out text);
        prepared.Text=text;statusDotSetup.BackColor=ready?Theme.Good:File.Exists(core.Config)?Theme.Warn:Theme.Muted;
        prepare.Primary=!ready;prepare.Invalidate();
        start.Enabled=ready&&!core.Running;
    }

    void SetRunning(bool on) {
        stop.Enabled=on;prepare.Enabled=find.Enabled=browse.Enabled=reset.Enabled=campaign.Enabled=!on;
        // The two drop-downs stay enabled (a disabled one draws white); their values are only read at the next Start.
        address.Enabled=campaignDir.Enabled=playerName.Enabled=!on;
        foreach(var t in tiles)t.Enabled=!on;
        if(!on)RefreshReady();else start.Enabled=false;
    }

    async void OnStart(object sender,EventArgs e) {
        if(core.Running||!ready)return;
        core.Address=address.Text.Trim();core.Campaign=campaign.Checked;core.CampaignDir=campaignDir.Text.Trim();
        core.PlayerName=playerName.Text.Trim();core.PlayerColor=SelectedColor();
        core.SeparateUnits=SeparateOptions.UnitChoices[Math.Max(0,separateUnits.SelectedIndex)];core.SeparateMoney=SeparateOptions.MoneyChoices[Math.Max(0,separateMoney.SelectedIndex)];
        core.GameSpeed=Program.GameSpeeds[Math.Max(0,gameSpeed.SelectedIndex)];
        await core.Start(LauncherController.SteamTarget(friend.SelectedItem,friend.Text));
    }

    async void OnPrepare(object sender,EventArgs e) {
        using(var d=new FolderBrowserDialog{Description="Select your StarCraft II folder (it is only read, never changed).",SelectedPath=LauncherController.GuessInstall()??""}) {
            if(d.ShowDialog(this)!=DialogResult.OK)return;
            await core.Prepare(d.SelectedPath);
        }
    }

    async void OnFind(object sender,EventArgs e) {
        find.Enabled=false;
        try{var list=await core.FindFriends();friend.Items.Clear();foreach(var f in list)friend.Items.Add(f);if(list.Count>0)friend.SelectedIndex=0;}
        catch(Exception error){Program.Log("ERROR: "+error.GetBaseException().Message);}
        finally{find.Enabled=!core.Running;}
    }

    void OnSaveLogs(object sender,EventArgs e) {
        using(var d=new SaveFileDialog{Title="Save logs for a bug report",Filter="Zip file (*.zip)|*.zip",FileName="SC2Coop-logs-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip",
            InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}) {
            if(d.ShowDialog(this)!=DialogResult.OK)return;
            try {
                int n=core.SaveLogs(d.FileName,log.Text);
                Program.Log("Saved "+n+" log files to "+d.FileName+". Send that file with your bug report (it has player names and folder paths, nothing else from your PC).");
                try{System.Diagnostics.Process.Start("explorer.exe","/select,\""+d.FileName+"\"");}catch{}
            } catch(Exception error){Program.Log("ERROR: could not save the logs: "+error.GetBaseException().Message);}
        }
    }

    void OnBrowse(object sender,EventArgs e) {
        using(var d=new FolderBrowserDialog{Description="Select the campaign folder (Wings of Liberty: TStory01.SC2Map plus the mission maps).",SelectedPath=campaignDir.Text})
            if(d.ShowDialog(this)==DialogResult.OK)campaignDir.Text=d.SelectedPath;
    }
}
