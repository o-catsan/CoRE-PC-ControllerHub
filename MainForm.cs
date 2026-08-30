using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Windows.Gaming.Input;

namespace ControllerHubNative;

public enum InputMode { Controller, KeyboardMouse }

public sealed class InputConfig
{
    public InputMode Mode { get; set; } = InputMode.Controller;
    public string LeftUp { get; set; } = "W";
    public string LeftDown { get; set; } = "S";
    public string LeftLeft { get; set; } = "A";
    public string LeftRight { get; set; } = "D";
    public string A { get; set; } = "Space";
    public string B { get; set; } = "LControlKey";
    public string X { get; set; } = "E";
    public string Y { get; set; } = "Q";
    public string L1 { get; set; } = "LShiftKey";
    public string R1 { get; set; } = "RMouse";
    public string L3 { get; set; } = "C";
    public string R3 { get; set; } = "MMouse";
    public string DPadUp { get; set; } = "Up";
    public string DPadDown { get; set; } = "Down";
    public string DPadLeft { get; set; } = "Left";
    public string DPadRight { get; set; } = "Right";
    public string Share { get; set; } = "Tab";
    public string Options { get; set; } = "Escape";
    public string LT { get; set; } = "LShiftKey";
    public string RT { get; set; } = "LMouse";
    public double MouseSensitivity { get; set; } = 1.0;
    public bool InvertY { get; set; } = false;
    public string MouseCaptureKey { get; set; } = "F8";

    public static InputConfig Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<InputConfig>(File.ReadAllText(path)) ?? new InputConfig();
        }
        catch { }
        return new InputConfig();
    }

    public void Save()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class MainForm : Form
{
    private readonly Label status = new();
    private readonly Label device = new();
    private readonly Label packetLabel = new();
    private readonly Label targetLabel = new();
    private readonly Label[] buttonLabels = new Label[16];
    private readonly StickView leftStick = new();
    private readonly StickView rightStick = new();
    private readonly Label leftCoord = new();
    private readonly Label rightCoord = new();
    private readonly Label leftInfoCoord = new();
    private readonly Label rightInfoCoord = new();
    private readonly TextBox ipBox = new() { Text = "192.168.6.20" };
    private readonly NumericUpDown portBox = new() { Minimum = 1, Maximum = 65535, Value = 12345 };
    private readonly NumericUpDown rateBox = new() { Minimum = 5, Maximum = 200, Value = 20 };
    private readonly Button connectButton = new();
    private readonly Button disconnectButton = new();
    private readonly Button settingsButton = new();
    private readonly Label ltValue = new(), rtValue = new();
    private Panel ltFill = new(), rtFill = new();
    private readonly RichTextBox connectionLog = new();
    private readonly RichTextBox txLog = new();

    private readonly System.Windows.Forms.Timer timer = new();
    private Gamepad? gamepad;
    private TcpClient? tcp;
    private NetworkStream? stream;
    private long tx;
    private DateTime lastSecond = DateTime.UtcNow;
    private int txInSecond;

    private InputConfig config = InputConfig.Load();
    private readonly HashSet<Keys> keysDown = new();
    private bool mouseCaptured;
    private double mouseDx;
    private double mouseDy;
    private Point lastMouseScreen;
    private bool haveMousePosition;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private static readonly Color Bg = Color.FromArgb(7, 10, 15);
    private static readonly Color Panel = Color.FromArgb(16, 21, 29);
    private static readonly Color Panel2 = Color.FromArgb(10, 16, 23);
    private static readonly Color Line = Color.FromArgb(27, 40, 55);
    private static readonly Color TextColor = Color.FromArgb(225, 233, 243);
    private static readonly Color Muted = Color.FromArgb(125, 143, 166);
    private static readonly Color Blue = Color.FromArgb(67, 165, 255);
    private static readonly Color Green = Color.FromArgb(67, 229, 138);

    public MainForm(InputMode startupMode)
    {
        config.Mode = startupMode;
        config.Save();
        base.Text = "Controller Hub";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 650);
        ClientSize = new Size(1280, 720);
        WindowState = FormWindowState.Maximized;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;

        BuildUi();

        timer.Interval = 10;
        timer.Tick += (_, _) => Poll();
        timer.Start();

        KeyDown += MainForm_KeyDown;
        KeyUp += MainForm_KeyUp;
        MouseMove += MainForm_MouseMove;
        MouseDown += MainForm_MouseDown;
        MouseUp += MainForm_MouseUp;

        Gamepad.GamepadAdded += (_, g) =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(() => SelectGamepad(g));
            }
            catch { }
        };
        Gamepad.GamepadRemoved += (_, g) =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (ReferenceEquals(gamepad, g))
                    {
                        gamepad = null;
                        if (config.Mode == InputMode.Controller) SetStatus("● CONTROLLER DISCONNECTED", false);
                        AppendLog(connectionLog, "Controller disconnected", Color.FromArgb(255, 150, 150));
                    }
                });
            }
            catch { }
        };

        // GAMEPAD初期取得：Gamepad APIが利用できなくてもKeyboard + Mouseモードは起動可能にする
        try
        {
            SelectGamepad(Gamepad.Gamepads.FirstOrDefault());
        }
        catch (Exception ex)
        {
            gamepad = null;
            AppendLog(connectionLog, "Gamepad API unavailable: " + ex.Message, Color.FromArgb(255, 150, 150));
        }
        ApplyModeUi();
    }

    private static Label TextLabel(string text, float size = 9, FontStyle style = FontStyle.Regular)
        => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font("Segoe UI", size, style),
            BackColor = Color.Transparent
        };

    private static void StyleInput(Control c)
    {
        c.BackColor = Color.FromArgb(8, 14, 21);
        c.ForeColor = TextColor;
        c.Font = new Font("Segoe UI", 10F);
    }

    private Panel Card(string title, string badge = "")
    {
        var p = new Panel { Dock = DockStyle.None, BackColor = Panel, Margin = new Padding(4), Padding = Padding.Empty, BorderStyle = BorderStyle.FixedSingle };
        var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var t = TextLabel(title, 11, FontStyle.Bold); t.ForeColor = TextColor; t.Location = new Point(2, 5); header.Controls.Add(t);
        if (!string.IsNullOrEmpty(badge))
        {
            var b = TextLabel(badge, 8); b.ForeColor = Muted; b.Anchor = AnchorStyles.Top | AnchorStyles.Right; b.Location = new Point(Math.Max(0, header.ClientSize.Width - 50), 7); header.Controls.Add(b);
            header.Resize += (_, _) => b.Left = Math.Max(0, header.ClientSize.Width - b.Width);
        }
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        p.Controls.Add(content); p.Controls.Add(header); p.Tag = content;
        return p;
    }

    private static Panel ContentOf(Panel card) => (Panel)card.Tag!;

    private void BuildUi()
    {
        SuspendLayout();
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(10) }; Controls.Add(root);

        // HEADER：タイトル・入力モード・接続状態
        var header = new Panel { BackColor = Bg }; root.Controls.Add(header);
        var logo = new Label { Name = "headerLogo", Text = "🎮", AutoSize = true, Font = new Font("Segoe UI Emoji", 22F), ForeColor = Color.White };
        var title = TextLabel("CONTROLLER HUB", 18, FontStyle.Bold); title.Name = "headerTitle"; title.ForeColor = Color.White;
        settingsButton.Text = "SETTINGS"; StyleButton(settingsButton, Color.FromArgb(22, 36, 52), TextColor); settingsButton.Click += (_, _) => OpenSettings();
        header.Controls.Add(logo); header.Controls.Add(title); header.Controls.Add(settingsButton); header.Controls.Add(status);
        status.AutoSize = true; status.ForeColor = Green;

        // MAIN：左 = INPUT MONITOR / 右 = SIDEBAR
        var main = new Panel { BackColor = Bg }; root.Controls.Add(main);
        var monitor = new Panel { BackColor = Panel, BorderStyle = BorderStyle.FixedSingle };
        var sidebar = new Panel { BackColor = Bg }; main.Controls.Add(monitor); main.Controls.Add(sidebar);

        var monitorHeader = new Panel { BackColor = Panel };
        var monitorTitle = TextLabel("INPUT MONITOR", 11, FontStyle.Bold); monitorTitle.ForeColor = TextColor;
        var analog = TextLabel("LIVE · 1:1 ANALOG", 9);
        monitorHeader.Controls.Add(monitorTitle); monitorHeader.Controls.Add(analog); monitor.Controls.Add(monitorHeader);

        var sticks = new Panel { BackColor = Panel };
        sticks.Controls.Add(leftStick); sticks.Controls.Add(rightStick); sticks.Controls.Add(leftCoord); sticks.Controls.Add(rightCoord);
        var ln = TextLabel("L STICK", 10, FontStyle.Bold); ln.ForeColor = Blue; var rn = TextLabel("R STICK", 10, FontStyle.Bold); rn.ForeColor = Blue;
        sticks.Controls.Add(ln); sticks.Controls.Add(rn); monitor.Controls.Add(sticks);
        leftCoord.Font = new Font("Consolas", 9F); leftCoord.ForeColor = TextColor; leftCoord.AutoSize = true;
        rightCoord.Font = new Font("Consolas", 9F); rightCoord.ForeColor = TextColor; rightCoord.AutoSize = true;

        var triggers = new Panel { BackColor = Panel };
        ltFill = TriggerPanel("L TRIGGER (LT)", ltValue); rtFill = TriggerPanel("R TRIGGER (RT)", rtValue);
        triggers.Controls.Add(ltFill); triggers.Controls.Add(rtFill); monitor.Controls.Add(triggers);

        var buttonGrid = new TableLayoutPanel { ColumnCount = 4, RowCount = 4, BackColor = Panel, Padding = Padding.Empty };
        for (int c = 0; c < 4; c++) buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (int r = 0; r < 4; r++) buttonGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        string[] names = { "A / CROSS", "B / CIRCLE", "X / SQUARE", "Y / TRIANGLE", "L1", "L3", "R1", "R3", "←", "↑", "↓", "→", "SHARE", "OPTIONS (OP)", "", "" };
        for (int i = 0; i < 16; i++)
        {
            var lab = new Label { Text = names[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(15, 23, 33), ForeColor = Color.FromArgb(205, 216, 230), Margin = new Padding(3), Font = new Font("Segoe UI", 9F), BorderStyle = BorderStyle.FixedSingle };
            buttonLabels[i] = lab; buttonGrid.Controls.Add(lab, i % 4, i / 4);
        }
        monitor.Controls.Add(buttonGrid);

        // SIDEBAR：CONTROLLER INFO / 接続ログ / 送信値ログ / NETWORK - TCP
        var infoCard = Card("CONTROLLER INFO", "LIVE");
        var info = new Panel { BackColor = Color.Transparent };
        device.ForeColor = TextColor; device.Font = new Font("Segoe UI", 9F); device.AutoSize = true;
        var state = TextLabel("● CONNECTED", 9); state.ForeColor = Green; var mode = TextLabel("LIVE POLLING", 9); mode.ForeColor = TextColor;
        leftInfoCoord.Font = new Font("Segoe UI", 9F); leftInfoCoord.ForeColor = TextColor; leftInfoCoord.AutoSize = true;
        rightInfoCoord.Font = new Font("Segoe UI", 9F); rightInfoCoord.ForeColor = TextColor; rightInfoCoord.AutoSize = true;
        info.Controls.Add(device); info.Controls.Add(state); info.Controls.Add(mode); info.Controls.Add(leftInfoCoord); info.Controls.Add(rightInfoCoord); AddInfoLabels(info, "DEVICE", "STATUS", "INPUT MODE", "LEFT STICK", "RIGHT STICK");
        ContentOf(infoCard).Controls.Add(info); sidebar.Controls.Add(infoCard);

        var connCard = Card("接続ログ"); ConfigureLog(connectionLog); connectionLog.Text = "[22:00:01] Controller connected\n[22:00:02] Input polling started"; ContentOf(connCard).Controls.Add(connectionLog); sidebar.Controls.Add(connCard);
        var txCard = Card("送信値ログ"); ConfigureLog(txLog); txLog.Text = "Waiting for TCP connection..."; ContentOf(txCard).Controls.Add(txLog); sidebar.Controls.Add(txCard);

        var netCard = Card("NETWORK - TCP", "M5STACK");
        var net = new Panel { BackColor = Color.Transparent };
        net.Controls.Add(ipBox); net.Controls.Add(portBox); net.Controls.Add(rateBox); net.Controls.Add(connectButton); net.Controls.Add(disconnectButton); net.Controls.Add(targetLabel);
        var ipL = TextLabel("IP ADDRESS", 7); var portL = TextLabel("PORT", 7); var rateL = TextLabel("INTERVAL (ms)", 7);
        net.Controls.Add(ipL); net.Controls.Add(portL); net.Controls.Add(rateL);
        connectButton.Dock = DockStyle.None; disconnectButton.Dock = DockStyle.None; ipBox.Dock = DockStyle.None; portBox.Dock = DockStyle.None; rateBox.Dock = DockStyle.None; targetLabel.Dock = DockStyle.None;
        connectButton.Text = "CONNECT"; StyleButton(connectButton, Color.FromArgb(42,137,208), Color.White); connectButton.Click += (_,_) => ConnectTcp();
        disconnectButton.Text = "DISCONNECT"; StyleButton(disconnectButton, Color.FromArgb(45,19,26), Color.FromArgb(255,190,198)); disconnectButton.Click += (_,_) => DisconnectTcp();
        targetLabel.Text = "● TCP DISCONNECTED"; targetLabel.ForeColor = TextColor; targetLabel.Font = new Font("Segoe UI", 9F);
        ContentOf(netCard).Controls.Add(net); sidebar.Controls.Add(netCard);

        // FOOTER：状態表示
        var footer = new Panel { BackColor = Color.FromArgb(11,17,25), BorderStyle = BorderStyle.FixedSingle };
        var ready = TextLabel("● READY", 9, FontStyle.Bold); ready.ForeColor = Green; var txRate = TextLabel("TX RATE: 0 Hz", 9); var packets = TextLabel("PACKETS: 0", 9); var errors = TextLabel("ERRORS: 0", 9); var ver = TextLabel("UI v1.0", 9);
        footer.Controls.AddRange(new Control[]{ready,txRate,packets,errors,ver}); root.Controls.Add(footer);

        root.Resize += (_,_) => LayoutUi(root, header, main, monitor, monitorHeader, monitorTitle, analog, sticks, ln, rn, triggers, buttonGrid, sidebar, infoCard, info, state, mode, connCard, txCard, netCard, net, ipL, portL, rateL, footer, ready, txRate, packets, errors, ver);
        LayoutUi(root, header, main, monitor, monitorHeader, monitorTitle, analog, sticks, ln, rn, triggers, buttonGrid, sidebar, infoCard, info, state, mode, connCard, txCard, netCard, net, ipL, portL, rateL, footer, ready, txRate, packets, errors, ver);
        ResumeLayout(true);
    }

    private void AddInfoLabels(Panel p, params string[] names)
    { for (int i=0;i<names.Length;i++) { var l=TextLabel(names[i],8); l.Name="infoLabel"+i; p.Controls.Add(l); } }

    private void LayoutUi(Panel root, Panel header, Panel main, Panel monitor, Panel monitorHeader, Label monitorTitle, Label analog, Panel sticks, Label ln, Label rn, Panel triggers, TableLayoutPanel buttonGrid, Panel sidebar, Panel infoCard, Panel info, Label state, Label mode, Panel connCard, Panel txCard, Panel netCard, Panel net, Label ipL, Label portL, Label rateL, Panel footer, Label ready, Label txRate, Label packets, Label errors, Label ver)
    {
        int w = Math.Max(1, root.ClientSize.Width), h = Math.Max(1, root.ClientSize.Height);
        const int headerH = 58, footerH = 28, gap = 8;
        header.SetBounds(0,0,w,headerH); main.SetBounds(0,headerH+gap,w,Math.Max(1,h-headerH-footerH-gap*2)); footer.SetBounds(0,h-footerH,w,footerH);

        var headerLogo=header.Controls["headerLogo"]; var headerTitle=header.Controls["headerTitle"];
        if(headerLogo!=null) headerLogo.Location=new Point(10,(headerH-headerLogo.Height)/2);
        if(headerTitle!=null) headerTitle.Location=new Point((headerLogo?.Right??52)+12,(headerH-headerTitle.Height)/2);
        settingsButton.SetBounds(Math.Max(0,header.ClientSize.Width-settingsButton.Width-status.Width-32),Math.Max(0,(header.ClientSize.Height-settingsButton.Height)/2),settingsButton.Width,settingsButton.Height);
        status.Top=Math.Max(2,(header.ClientSize.Height-status.Height)/2); status.Left=Math.Max(0,header.ClientSize.Width-status.Width-12);

        int sideW=Math.Clamp((int)(w*.31),390,520), leftW=Math.Max(500,w-sideW-gap);
        if(leftW+sideW+gap>w){sideW=Math.Max(340,w-500-gap);leftW=Math.Max(1,w-sideW-gap);}
        monitor.SetBounds(0,0,leftW,main.ClientSize.Height); sidebar.SetBounds(leftW+gap,0,sideW,main.ClientSize.Height);

        const int monitorHeaderH=34, triggerH=68, buttonH=124;
        monitorHeader.SetBounds(0,0,monitor.ClientSize.Width,monitorHeaderH); monitorTitle.Location=new Point(12,8); analog.Location=new Point(Math.Max(0,monitorHeader.ClientSize.Width-analog.Width-12),8);
        int available=monitor.ClientSize.Height-monitorHeaderH-triggerH-buttonH, contentH=Math.Max(1,available); sticks.SetBounds(0,monitorHeaderH,monitor.ClientSize.Width,contentH);
        int half=Math.Max(1,sticks.ClientSize.Width/2);
        int maxStick=Math.Min(300,Math.Min(Math.Max(1,half-50),Math.Max(1,contentH-70))), stickSize=Math.Max(1,maxStick);
        if(contentH>=140) stickSize=Math.Max(120,stickSize); if(stickSize>half-20) stickSize=Math.Max(1,half-20); if(stickSize>contentH-48) stickSize=Math.Max(1,contentH-48);
        int stickTop=Math.Max(40,(contentH-stickSize-42)/2);
        leftStick.SetBounds(Math.Max(0,(half-stickSize)/2),stickTop,stickSize,stickSize); rightStick.SetBounds(half+Math.Max(0,(half-stickSize)/2),stickTop,stickSize,stickSize);
        ln.Location=new Point(Math.Max(0,(half-ln.Width)/2),8); rn.Location=new Point(half+Math.Max(0,(half-rn.Width)/2),8);
        int coordY=Math.Min(Math.Max(0,contentH-leftCoord.Height),stickTop+stickSize+8); leftCoord.Location=new Point(Math.Max(0,(half-leftCoord.Width)/2),coordY); rightCoord.Location=new Point(half+Math.Max(0,(half-rightCoord.Width)/2),coordY);
        int triggerY=monitorHeaderH+contentH; triggers.SetBounds(0,triggerY,monitor.ClientSize.Width,triggerH); ltFill.SetBounds(10,8,Math.Max(1,triggers.ClientSize.Width/2-15),triggerH-16); rtFill.SetBounds(triggers.ClientSize.Width/2+5,8,Math.Max(1,triggers.ClientSize.Width/2-15),triggerH-16); buttonGrid.SetBounds(0,triggerY+triggerH,monitor.ClientSize.Width,buttonH);

        int sw=sidebar.ClientSize.Width, sh=sidebar.ClientSize.Height; const int infoH=160, netH=176, sideGap=6;
        int logArea=Math.Max(0,sh-infoH-netH-sideGap*3), logH=Math.Max(40,logArea/2); if(infoH+netH+logH*2+sideGap*3>sh) logH=Math.Max(1,(sh-infoH-netH-sideGap*3)/2);
        infoCard.SetBounds(0,0,sw,infoH); connCard.SetBounds(0,infoH+sideGap,sw,logH); txCard.SetBounds(0,infoH+sideGap*2+logH,sw,logH); netCard.SetBounds(0,Math.Max(0,sh-netH),sw,netH);

        var infoContent=ContentOf(infoCard); infoContent.Padding=new Padding(10,4,10,4); info.Dock=DockStyle.Fill; info.Margin=Padding.Empty; if(!infoContent.Controls.Contains(info)) infoContent.Controls.Add(info);
        var labels=info.Controls.Cast<Control>().Where(c=>c.Name.StartsWith("infoLabel")).OrderBy(c=>c.Name).ToArray(); Control[] values={device,state,mode,leftInfoCoord,rightInfoCoord}; int rowH=Math.Max(1,(info.ClientSize.Height-4)/5);
        for(int i=0;i<5;i++){int y=i*rowH;labels[i].SetBounds(0,y,105,rowH);values[i].SetBounds(120,y,Math.Max(1,info.ClientSize.Width-120),rowH);}
        ContentOf(connCard).Padding=new Padding(8,4,8,8); ContentOf(txCard).Padding=new Padding(8,4,8,8); connectionLog.Dock=DockStyle.Fill; txLog.Dock=DockStyle.Fill;

        var netContent=ContentOf(netCard); netContent.Padding=new Padding(10,4,10,6); net.Dock=DockStyle.Fill; net.Margin=Padding.Empty; if(!netContent.Controls.Contains(net)) netContent.Controls.Add(net);
        int nw=Math.Max(1,sw-22), nh=Math.Max(1,netH-46); int buttonW=Math.Clamp(nw/4,118,140), colGap=8, leftWNet=Math.Max(1,nw-buttonW-colGap), rightX=leftWNet+colGap; int rowLabelH=13, fieldH=Math.Clamp((nh-58)/2,18,24);
        ipL.SetBounds(0,0,leftWNet,rowLabelH); ipBox.SetBounds(0,rowLabelH+1,leftWNet,fieldH); portL.SetBounds(rightX,0,buttonW,rowLabelH); portBox.SetBounds(rightX,rowLabelH+1,buttonW,fieldH);
        int row2Y=rowLabelH+1+fieldH+4; rateL.SetBounds(0,row2Y,leftWNet,rowLabelH); rateBox.SetBounds(0,row2Y+rowLabelH+1,leftWNet,fieldH);
        int row3Y=row2Y+rowLabelH+1+fieldH+4; int bottomH=Math.Max(22,nh-row3Y); connectButton.SetBounds(rightX,row2Y,buttonW,Math.Max(22,row3Y-row2Y)); disconnectButton.SetBounds(0,row3Y,leftWNet,bottomH); targetLabel.SetBounds(rightX,row3Y,buttonW,bottomH);

        ready.Location=new Point(10,Math.Max(0,(footer.ClientSize.Height-ready.Height)/2)); txRate.Location=new Point(90,Math.Max(0,(footer.ClientSize.Height-txRate.Height)/2)); packets.Location=new Point(210,Math.Max(0,(footer.ClientSize.Height-packets.Height)/2)); errors.Location=new Point(325,Math.Max(0,(footer.ClientSize.Height-errors.Height)/2)); ver.Location=new Point(Math.Max(0,footer.ClientSize.Width-ver.Width-10),Math.Max(0,(footer.ClientSize.Height-ver.Height)/2));
    }

    private static void ConfigureLog(RichTextBox box){box.Dock=DockStyle.Fill;box.BackColor=Color.FromArgb(5,10,16);box.ForeColor=Color.FromArgb(102,255,168);box.BorderStyle=BorderStyle.FixedSingle;box.Font=new Font("Consolas",9F);box.ReadOnly=true;box.WordWrap=false;box.ScrollBars=RichTextBoxScrollBars.Vertical;box.Margin=Padding.Empty;box.Padding=new Padding(8);}
    private static void StyleButton(Button b,Color back,Color fore){b.Dock=DockStyle.None;b.BackColor=back;b.ForeColor=fore;b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(45,75,100);b.FlatAppearance.BorderSize=1;b.Font=new Font("Segoe UI",9F,FontStyle.Bold);b.Margin=new Padding(3);}
    private Panel TriggerPanel(string name,Label value){var p=new Panel{Dock=DockStyle.None,BackColor=Panel2,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(3),Padding=new Padding(12,10,12,10)};var l=TextLabel(name,9,FontStyle.Bold);l.ForeColor=TextColor;l.Dock=DockStyle.Top;l.Height=22;value.Text="0%";value.TextAlign=ContentAlignment.TopRight;value.Dock=DockStyle.Top;value.Height=22;value.ForeColor=TextColor;value.Font=new Font("Segoe UI",9F,FontStyle.Bold);var bar=new Panel{Dock=DockStyle.Bottom,Height=10,BackColor=Color.FromArgb(28,45,63),Padding=Padding.Empty};var fill=new Panel{Dock=DockStyle.Left,Width=0,BackColor=Blue};bar.Controls.Add(fill);p.Controls.Add(bar);p.Controls.Add(value);p.Controls.Add(l);p.Tag=fill;return p;}

    private void SelectGamepad(Gamepad? g){if(g==null)return;gamepad=g;device.Text="XInput Gamepad";if(config.Mode==InputMode.Controller)SetStatus("● CONTROLLER CONNECTED",true);}
    private void SetStatus(string text,bool ok){status.Text=text;status.ForeColor=ok?Green:Color.FromArgb(126,139,157);if(status.Parent!=null)status.Left=Math.Max(0,status.Parent.ClientSize.Width-status.Width-12);}

    private void ApplyModeUi()
    {
        string modeText=config.Mode==InputMode.Controller?"CONTROLLER":"KEYBOARD + MOUSE";
        device.Text=config.Mode==InputMode.Controller?(gamepad!=null?"XInput Gamepad":"Waiting for controller"):"Keyboard + Mouse";
        if(config.Mode!=InputMode.KeyboardMouse && mouseCaptured)
        {
            mouseCaptured=false;
            Cursor.Show();
        }
        haveMousePosition=false;
        mouseDx=0;
        mouseDy=0;
        SetStatus(config.Mode==InputMode.Controller ? (gamepad!=null?"● CONTROLLER CONNECTED":"● WAITING FOR CONTROLLER") : "● KEYBOARD + MOUSE", config.Mode==InputMode.KeyboardMouse || gamepad!=null);
        AppendLog(connectionLog,$"Input mode: {modeText}",Color.FromArgb(140,180,220));
    }

    private void MainForm_KeyDown(object? sender,KeyEventArgs e)
    {
        keysDown.Add(e.KeyCode);
        if (config.Mode==InputMode.KeyboardMouse && KeyName(e.KeyCode)==config.MouseCaptureKey && !e.Handled){ToggleMouseCapture();e.Handled=true;}
    }
    private void MainForm_KeyUp(object? sender,KeyEventArgs e){keysDown.Remove(e.KeyCode);}
    private void MainForm_MouseDown(object? sender,MouseEventArgs e){if(config.Mode==InputMode.KeyboardMouse)keysDownMouse.Add(e.Button);}
    private void MainForm_MouseUp(object? sender,MouseEventArgs e){keysDownMouse.Remove(e.Button);}
    private readonly HashSet<MouseButtons> keysDownMouse=new();
    private void MainForm_MouseMove(object? sender,MouseEventArgs e)
    {
        // マウス移動は PollKeyboardMouse() 側で画面座標を直接取得する。
        // 子コントロール上を移動しても取りこぼさないため、ここでは何もしない。
    }

    private void ToggleMouseCapture()
    {
        mouseCaptured=!mouseCaptured;
        haveMousePosition=false;
        if(mouseCaptured)
        {
            Cursor.Hide();
            Point center=PointToScreen(new Point(ClientSize.Width/2,ClientSize.Height/2));
            SetCursorPos(center.X,center.Y);
            lastMouseScreen=center;
            haveMousePosition=true;
            SetStatus("● KEYBOARD + MOUSE · CAPTURE",true);
        }
        else
        {
            Cursor.Show();
            SetStatus("● KEYBOARD + MOUSE",true);
        }
    }

    private void Poll()
    {
        if(config.Mode==InputMode.Controller) PollController(); else PollKeyboardMouse();
    }

    private void PollController()
    {
        var pads=Gamepad.Gamepads; if(gamepad==null||!pads.Contains(gamepad))gamepad=pads.FirstOrDefault();
        if(gamepad==null){SetStatus("● WAITING FOR CONTROLLER",false);device.Text="Waiting for controller";return;}
        var r=gamepad.GetCurrentReading();var b=r.Buttons;
        bool[] pressed={b.HasFlag(GamepadButtons.A),b.HasFlag(GamepadButtons.B),b.HasFlag(GamepadButtons.X),b.HasFlag(GamepadButtons.Y),b.HasFlag(GamepadButtons.LeftShoulder),b.HasFlag(GamepadButtons.RightShoulder),r.LeftTrigger>.05,r.RightTrigger>.05,b.HasFlag(GamepadButtons.View),b.HasFlag(GamepadButtons.Menu),b.HasFlag(GamepadButtons.LeftThumbstick),b.HasFlag(GamepadButtons.RightThumbstick),b.HasFlag(GamepadButtons.DPadUp),b.HasFlag(GamepadButtons.DPadDown),b.HasFlag(GamepadButtons.DPadLeft),b.HasFlag(GamepadButtons.DPadRight)};
        UpdateVisuals(r.LeftThumbstickX,r.LeftThumbstickY,r.RightThumbstickX,r.RightThumbstickY,r.LeftTrigger,r.RightTrigger,pressed);
        if(stream!=null&&stream.CanWrite)SendPacket(r,pressed);
    }

    private void PollKeyboardMouse()
    {
        // 左スティック相当：通常 0.8、Shift で 1.0、Ctrl で 0.5。
        // 同時押しの場合は Shift を優先する。
        double leftMagnitude = IsShiftDown() ? 1.0 : IsControlDown() ? 0.5 : 0.8;
        double lx=IsKey(config.LeftRight)?leftMagnitude:0; if(IsKey(config.LeftLeft))lx=-leftMagnitude;
        double ly=IsKey(config.LeftUp)?leftMagnitude:0; if(IsKey(config.LeftDown))ly=-leftMagnitude;

        bool[] pressed={IsKey(config.A),IsKey(config.B),IsKey(config.X),IsKey(config.Y),IsKey(config.L1)||IsKey(config.LT),IsMouse(config.R1),IsKey(config.LT),IsMouse(config.RT),IsKey(config.Share),IsKey(config.Options),IsKey(config.L3),IsMouse(config.R3),IsKey(config.DPadUp),IsKey(config.DPadDown),IsKey(config.DPadLeft),IsKey(config.DPadRight)};

        // マウス入力はイベントではなく画面座標から差分を取る。
        // これにより StickView / TextBox / Panel 上でも確実に右スティックへ入る。
        if(GetCursorPos(out Point currentMouseScreen))
        {
            if(!haveMousePosition)
            {
                lastMouseScreen=currentMouseScreen;
                haveMousePosition=true;
            }
            else if(mouseCaptured)
            {
                Point center=PointToScreen(new Point(ClientSize.Width/2,ClientSize.Height/2));
                mouseDx += currentMouseScreen.X-center.X;
                mouseDy += currentMouseScreen.Y-center.Y;
                if(currentMouseScreen.X!=center.X || currentMouseScreen.Y!=center.Y)
                    SetCursorPos(center.X,center.Y);
                lastMouseScreen=center;
            }
            else
            {
                mouseDx += currentMouseScreen.X-lastMouseScreen.X;
                mouseDy += currentMouseScreen.Y-lastMouseScreen.Y;
                lastMouseScreen=currentMouseScreen;
            }
        }

        double rx=Math.Clamp(mouseDx*config.MouseSensitivity/10.0,-1,1);
        double ry=Math.Clamp(mouseDy*config.MouseSensitivity/10.0,-1,1);
        if(config.InvertY)ry=-ry;
        double lt=IsKey(config.LT)?1:(IsMouse(config.LT)?1:0), rt=IsMouse(config.RT)?1:(IsKey(config.RT)?1:0);
        UpdateVisuals(lx,ly,rx,ry,lt,rt,pressed);
        SendKeyboardPacket(lx,ly,rx,ry,pressed);
        mouseDx=0;mouseDy=0;
    }

    private bool IsShiftDown()=>keysDown.Contains(Keys.LShiftKey)||keysDown.Contains(Keys.RShiftKey)||keysDown.Contains(Keys.ShiftKey);
    private bool IsControlDown()=>keysDown.Contains(Keys.LControlKey)||keysDown.Contains(Keys.RControlKey)||keysDown.Contains(Keys.ControlKey);

    private bool IsKey(string name){if(TryParseKey(name,out var k))return keysDown.Contains(k);return false;}
    private bool IsMouse(string name)=>name.Equals("LMouse",StringComparison.OrdinalIgnoreCase)?keysDownMouse.Contains(MouseButtons.Left):name.Equals("RMouse",StringComparison.OrdinalIgnoreCase)?keysDownMouse.Contains(MouseButtons.Right):name.Equals("MMouse",StringComparison.OrdinalIgnoreCase)&&keysDownMouse.Contains(MouseButtons.Middle);
    private static bool TryParseKey(string name,out Keys key)=>Enum.TryParse(name,true,out key);
    private static string KeyName(Keys key)=>key.ToString();

    private void UpdateVisuals(double lx,double ly,double rx,double ry,double lt,double rt,bool[] pressed)
    {
        for(int i=0;i<16;i++){buttonLabels[i].BackColor=pressed[i]?Color.FromArgb(29,99,157):Color.FromArgb(15,23,33);buttonLabels[i].ForeColor=pressed[i]?Color.White:Color.FromArgb(205,216,230);}
        leftStick.SetPosition(lx,ly);rightStick.SetPosition(rx,ry);leftCoord.Text=$"X: {lx:0.000}   Y: {ly:0.000}";rightCoord.Text=$"X: {rx:0.000}   Y: {ry:0.000}";leftInfoCoord.Text=$"X: {lx:0.000} / Y: {ly:0.000}";rightInfoCoord.Text=$"X: {rx:0.000} / Y: {ry:0.000}";
        int ltp=(int)Math.Round(lt*100),rtp=(int)Math.Round(rt*100);ltValue.Text=$"{ltp}%";rtValue.Text=$"{rtp}%";var ltf=ltFill.Tag as Panel;var rtf=rtFill.Tag as Panel;if(ltf!=null)ltf.Width=(int)(Math.Max(0,ltf.Parent?.ClientSize.Width??0)*ltp/100.0);if(rtf!=null)rtf.Width=(int)(Math.Max(0,rtf.Parent?.ClientSize.Width??0)*rtp/100.0);
    }

    private void SendKeyboardPacket(double lx,double ly,double rx,double ry,bool[] p)
    {
        if(stream==null||!stream.CanWrite)return;
        int b0=0;if(p[0])b0|=1;if(p[1])b0|=2;if(p[2])b0|=4;if(p[3])b0|=8;if(p[4])b0|=0x10;if(p[5])b0|=0x20;if(p[6])b0|=0x40;if(p[7])b0|=0x80;
        int b1=0;if(p[8])b1|=1;if(p[9])b1|=2;if(p[10])b1|=0x10;if(p[11])b1|=0x20;
        int d=0;if(p[12]&&p[15])d=2;else if(p[15]&&p[13])d=4;else if(p[13]&&p[14])d=6;else if(p[14]&&p[12])d=8;else if(p[12])d=1;else if(p[15])d=3;else if(p[13])d=5;else if(p[14])d=7;
        static byte Axis(double v)=>(byte)Math.Clamp((int)Math.Round((v+1)*127.5),0,255);
        string line=$"{b0:X2},{b1:X2},{d:X2},{Axis(lx):X2},{Axis(ly):X2},{Axis(rx):X2},{Axis(ry):X2}\r\n";
        try{var data=Encoding.ASCII.GetBytes(line);stream.Write(data,0,data.Length);tx++;txInSecond++;packetLabel.Text=$"TX {tx}";txLog.Text=line.TrimEnd();if((DateTime.UtcNow-lastSecond).TotalSeconds>=1){lastSecond=DateTime.UtcNow;txInSecond=0;}}catch{DisconnectTcp();}
    }

    private void SendPacket(GamepadReading r,bool[] p){SendKeyboardPacket(r.LeftThumbstickX,r.LeftThumbstickY,r.RightThumbstickX,r.RightThumbstickY,p);}

    private async void ConnectTcp(){try{DisconnectTcp();tcp=new TcpClient();await tcp.ConnectAsync(ipBox.Text.Trim(),(int)portBox.Value);tcp.NoDelay=true;stream=tcp.GetStream();targetLabel.Text="● TCP CONNECTED";targetLabel.ForeColor=Green;AppendLog(txLog,$"TCP connected → {ipBox.Text.Trim()}:{portBox.Value}",Green);}catch(Exception ex){targetLabel.Text="● TCP ERROR";targetLabel.ForeColor=Color.FromArgb(255,150,150);AppendLog(txLog,ex.Message,Color.FromArgb(255,150,150));DisconnectTcp();}}
    private void DisconnectTcp(){try{stream?.Dispose();}catch{}try{tcp?.Dispose();}catch{}stream=null;tcp=null;targetLabel.Text="● TCP DISCONNECTED";targetLabel.ForeColor=Muted;}
    private void AppendLog(RichTextBox box,string text,Color color){if(box.InvokeRequired){box.BeginInvoke(()=>AppendLog(box,text,color));return;}box.SelectionStart=box.TextLength;box.SelectionLength=0;box.SelectionColor=color;box.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");box.SelectionColor=box.ForeColor;box.ScrollToCaret();}

    private void OpenSettings()
    {
        using var dlg=new SettingsForm(config);
        if(dlg.ShowDialog(this)==DialogResult.OK){config=dlg.Result;config.Save();ApplyModeUi();}
    }

    protected override void OnFormClosed(FormClosedEventArgs e){timer.Stop();if(mouseCaptured){mouseCaptured=false;Cursor.Show();}DisconnectTcp();base.OnFormClosed(e);}
}

public sealed class SettingsForm : Form
{
    private readonly InputConfig cfg;
    private readonly Dictionary<string,TextBox> fields=new();
    private readonly ComboBox modeBox=new();
    private readonly NumericUpDown sensitivity=new(){Minimum=.1m,Maximum=10m,Increment=.1m,DecimalPlaces=1,Value=1};
    private readonly CheckBox invertY=new(){Text="INVERT Y AXIS",AutoSize=true};
    private readonly Button ok=new(){Text="APPLY"},cancel=new(){Text="CANCEL"},defaults=new(){Text="DEFAULT"};
    private string? waitingField;

    public InputConfig Result { get; private set; }
    public SettingsForm(InputConfig source)
    {
        cfg=JsonSerializer.Deserialize<InputConfig>(JsonSerializer.Serialize(source))??new InputConfig(); Result=cfg;
        Text="INPUT SETTINGS"; StartPosition=FormStartPosition.CenterParent; ClientSize=new Size(650,650); MinimumSize=new Size(650,650); BackColor=Color.FromArgb(7,10,15); ForeColor=Color.FromArgb(225,233,243); Font=new Font("Segoe UI",9F); KeyPreview=true;
        Build(); KeyDown+=SettingsForm_KeyDown; MouseDown+=SettingsForm_MouseDown;
    }
    private void Build()
    {
        var panel=new Panel{Dock=DockStyle.Fill,Padding=new Padding(18),AutoScroll=true};Controls.Add(panel);
        var title=new Label{Text="INPUT CONFIGURATION",AutoSize=true,Font=new Font("Segoe UI",15,FontStyle.Bold),ForeColor=Color.White,Location=new Point(18,18)};panel.Controls.Add(title);
        AddMode(panel,60); int y=110;
        AddSection(panel,"LEFT STICK",ref y,new[]{("UP",nameof(cfg.LeftUp)),("DOWN",nameof(cfg.LeftDown)),("LEFT",nameof(cfg.LeftLeft)),("RIGHT",nameof(cfg.LeftRight))});
        AddSection(panel,"BUTTONS",ref y,new[]{("A / CROSS",nameof(cfg.A)),("B / CIRCLE",nameof(cfg.B)),("X / SQUARE",nameof(cfg.X)),("Y / TRIANGLE",nameof(cfg.Y)),("L1",nameof(cfg.L1)),("R1",nameof(cfg.R1)),("L3",nameof(cfg.L3)),("R3",nameof(cfg.R3)),("D-PAD UP",nameof(cfg.DPadUp)),("D-PAD DOWN",nameof(cfg.DPadDown)),("D-PAD LEFT",nameof(cfg.DPadLeft)),("D-PAD RIGHT",nameof(cfg.DPadRight)),("SHARE",nameof(cfg.Share)),("OPTIONS",nameof(cfg.Options)),("LT",nameof(cfg.LT)),("RT",nameof(cfg.RT))});
        var mouseTitle=new Label{Text="MOUSE",AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold),ForeColor=Color.FromArgb(225,233,243),Location=new Point(18,y)};panel.Controls.Add(mouseTitle);y+=30;
        panel.Controls.Add(new Label{Text="Sensitivity",AutoSize=true,Location=new Point(28,y+5)});sensitivity.Value=(decimal)Math.Clamp(cfg.MouseSensitivity,.1,10);sensitivity.Location=new Point(145,y);sensitivity.Width=80;panel.Controls.Add(sensitivity);invertY.Checked=cfg.InvertY;invertY.Location=new Point(245,y+4);panel.Controls.Add(invertY);y+=38;
        AddSingle(panel,"Mouse Capture",nameof(cfg.MouseCaptureKey),ref y);
        var note=new Label{Text="RIGHT STICK = MOUSE X/Y  (fixed)\nKeyboard assignments: click CHANGE, then press a key.\nFor R1/R3/RT, left/right/middle mouse buttons can also be captured.",AutoSize=true,ForeColor=Color.FromArgb(125,143,166),Location=new Point(18,y+8)};panel.Controls.Add(note);y+=70;
        defaults.Location=new Point(18,y);defaults.Size=new Size(100,32);defaults.Click+=(_,_)=>{Result=new InputConfig();CloseWith(DialogResult.OK);};panel.Controls.Add(defaults);
        cancel.Location=new Point(415,y);cancel.Size=new Size(90,32);cancel.Click+=(_,_)=>CloseWith(DialogResult.Cancel);panel.Controls.Add(cancel);
        ok.Location=new Point(515,y);ok.Size=new Size(100,32);ok.BackColor=Color.FromArgb(42,137,208);ok.ForeColor=Color.White;ok.FlatStyle=FlatStyle.Flat;ok.Click+=(_,_)=>Apply();panel.Controls.Add(ok);
    }
    private void AddMode(Panel p,int y){p.Controls.Add(new Label{Text="INPUT MODE",AutoSize=true,Location=new Point(18,y+4),ForeColor=Color.FromArgb(125,143,166)});modeBox.DropDownStyle=ComboBoxStyle.DropDownList;modeBox.Items.AddRange(new object[]{"CONTROLLER","KEYBOARD + MOUSE"});modeBox.SelectedIndex=cfg.Mode==InputMode.Controller?0:1;modeBox.Location=new Point(145,y);modeBox.Width=200;p.Controls.Add(modeBox);}
    private void AddSection(Panel p,string heading,ref int y,(string label,string prop)[] items){var h=new Label{Text=heading,AutoSize=true,Font=new Font("Segoe UI",10,FontStyle.Bold),Location=new Point(18,y),ForeColor=Color.FromArgb(225,233,243)};p.Controls.Add(h);y+=28;foreach(var item in items){AddSingle(p,item.label,item.prop,ref y);}y+=10;}
    private void AddSingle(Panel p,string label,string prop,ref int y){p.Controls.Add(new Label{Text=label,AutoSize=true,Location=new Point(28,y+6),ForeColor=Color.FromArgb(125,143,166)});var box=new TextBox{ReadOnly=true,Text=Get(prop),Location=new Point(145,y),Width=190,BackColor=Color.FromArgb(8,14,21),ForeColor=Color.FromArgb(225,233,243)};var b=new Button{Text="CHANGE",Location=new Point(345,y),Width=80,Height=25};b.Click+=(_,_)=>{waitingField=prop;box.Text="PRESS KEY / MOUSE...";Focus();};fields[prop]=box;p.Controls.Add(box);p.Controls.Add(b);y+=31;}
    private string Get(string prop)=>prop switch{nameof(InputConfig.LeftUp)=>cfg.LeftUp,nameof(InputConfig.LeftDown)=>cfg.LeftDown,nameof(InputConfig.LeftLeft)=>cfg.LeftLeft,nameof(InputConfig.LeftRight)=>cfg.LeftRight,nameof(InputConfig.A)=>cfg.A,nameof(InputConfig.B)=>cfg.B,nameof(InputConfig.X)=>cfg.X,nameof(InputConfig.Y)=>cfg.Y,nameof(InputConfig.L1)=>cfg.L1,nameof(InputConfig.R1)=>cfg.R1,nameof(InputConfig.L3)=>cfg.L3,nameof(InputConfig.R3)=>cfg.R3,nameof(InputConfig.DPadUp)=>cfg.DPadUp,nameof(InputConfig.DPadDown)=>cfg.DPadDown,nameof(InputConfig.DPadLeft)=>cfg.DPadLeft,nameof(InputConfig.DPadRight)=>cfg.DPadRight,nameof(InputConfig.Share)=>cfg.Share,nameof(InputConfig.Options)=>cfg.Options,nameof(InputConfig.LT)=>cfg.LT,nameof(InputConfig.RT)=>cfg.RT,nameof(InputConfig.MouseCaptureKey)=>cfg.MouseCaptureKey,_=>""};
    private void SettingsForm_KeyDown(object? s,KeyEventArgs e){if(waitingField==null)return;string value=e.KeyCode.ToString();Set(waitingField,value);e.SuppressKeyPress=true;waitingField=null;}
    private void SettingsForm_MouseDown(object? s,MouseEventArgs e){if(waitingField==null)return;if(e.Button==MouseButtons.Left||e.Button==MouseButtons.Right||e.Button==MouseButtons.Middle){string value=e.Button==MouseButtons.Left?"LMouse":e.Button==MouseButtons.Right?"RMouse":"MMouse";Set(waitingField,value);waitingField=null;}}
    private void Set(string prop,string value){switch(prop){case nameof(InputConfig.LeftUp):cfg.LeftUp=value;break;case nameof(InputConfig.LeftDown):cfg.LeftDown=value;break;case nameof(InputConfig.LeftLeft):cfg.LeftLeft=value;break;case nameof(InputConfig.LeftRight):cfg.LeftRight=value;break;case nameof(InputConfig.A):cfg.A=value;break;case nameof(InputConfig.B):cfg.B=value;break;case nameof(InputConfig.X):cfg.X=value;break;case nameof(InputConfig.Y):cfg.Y=value;break;case nameof(InputConfig.L1):cfg.L1=value;break;case nameof(InputConfig.R1):cfg.R1=value;break;case nameof(InputConfig.L3):cfg.L3=value;break;case nameof(InputConfig.R3):cfg.R3=value;break;case nameof(InputConfig.DPadUp):cfg.DPadUp=value;break;case nameof(InputConfig.DPadDown):cfg.DPadDown=value;break;case nameof(InputConfig.DPadLeft):cfg.DPadLeft=value;break;case nameof(InputConfig.DPadRight):cfg.DPadRight=value;break;case nameof(InputConfig.Share):cfg.Share=value;break;case nameof(InputConfig.Options):cfg.Options=value;break;case nameof(InputConfig.LT):cfg.LT=value;break;case nameof(InputConfig.RT):cfg.RT=value;break;case nameof(InputConfig.MouseCaptureKey):cfg.MouseCaptureKey=value;break;}if(fields.TryGetValue(prop,out var box))box.Text=value;}
    private void Apply(){cfg.Mode=modeBox.SelectedIndex==0?InputMode.Controller:InputMode.KeyboardMouse;cfg.MouseSensitivity=(double)sensitivity.Value;cfg.InvertY=invertY.Checked;Result=cfg;CloseWith(DialogResult.OK);}
    private void CloseWith(DialogResult r){DialogResult=r;Close();}
}

public sealed class StickView : Control
{
    private double x,y;
    public StickView(){DoubleBuffered=true;BackColor=Color.FromArgb(7,13,20);MinimumSize=new Size(1,1);}
    public void SetPosition(double x,double y){this.x=x;this.y=y;Invalidate();}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);int s=Math.Max(0,Math.Min(ClientSize.Width,ClientSize.Height)-2);int l=(ClientSize.Width-s)/2,t=(ClientSize.Height-s)/2;using var p=new Pen(Color.FromArgb(48,64,82));e.Graphics.DrawRectangle(p,l,t,s,s);e.Graphics.DrawLine(p,l+s/2,t,l+s/2,t+s);e.Graphics.DrawLine(p,l,t+s/2,l+s,t+s/2);float px=l+(float)((x+1)*.5*s),py=t+(float)((-y+1)*.5*s);using var glow=new SolidBrush(Color.FromArgb(55,67,165,255));e.Graphics.FillEllipse(glow,px-12,py-12,24,24);using var b=new SolidBrush(Color.FromArgb(67,165,255));e.Graphics.FillEllipse(b,px-8,py-8,16,16);}
}
