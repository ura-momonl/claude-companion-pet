// First-run guide: three steps in ONE window (pick a look, connect Claude Code, done). Also reachable from the right-click menu.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ClaudeCompanionPet
{
    class WizardForm : Form
    {
        readonly PetForm pet;
        readonly Panel[] pages = new Panel[3];
        readonly Button back = new Button(), next = new Button(), connect = new Button();
        readonly Label step = new Label();
        readonly TextBox result = new TextBox();          // read-only text box: the message can be selected and copied
        readonly LinkLabel logLink = new LinkLabel();
        readonly ListBox list = new ListBox();
        readonly PictureBox preview = new PictureBox();
        readonly CheckBox autostart = new CheckBox();
        int cur;
        bool connected;

        public WizardForm(PetForm pet)
        {
            this.pet = pet;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            Text = "claude-companion";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual; TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(520, 600); BackColor = Color.FromArgb(0xfb, 0xf5, 0xe6);

            step.SetBounds(20, 14, 480, 20); step.ForeColor = Color.FromArgb(0x7a, 0x6f, 0x60); Controls.Add(step);
            for (int i = 0; i < 3; i++) { pages[i] = new Panel(); pages[i].SetBounds(20, 40, 480, 500); Controls.Add(pages[i]); }

            // ---- 1: welcome + skin ----
            AddText(pages[0], Lang.T("小人陪你写代码", "A little friend for your coding"), 0, 0, 480, 30, true);
            Label intro = AddText(pages[0], Lang.T("Claude Code 需要你的时候，桌面角落的小人会招手，旁边冒出气泡，写明是哪个项目、要你做什么。不弹窗，不抢你的焦点。",
                "When Claude Code needs you, the little character in the corner of your desktop waves and a speech bubble says which project and why. No pop-ups, and it never steals your focus."), 0, 36, 480, 20, false);
            Label pick = AddText(pages[0], Lang.T("选一个喜欢的样子（以后可以在右键菜单里换）：", "Pick a look (you can change it later from the right-click menu):"), 0, intro.Bottom + 10, 480, 20, false);
            list.SetBounds(0, pick.Bottom + 6, 200, 130); list.IntegralHeight = false; pages[0].Controls.Add(list);
            foreach (Skin s in pet.Skins) list.Items.Add(s.Title);
            preview.SetBounds(220, list.Top, 200, 130); preview.SizeMode = PictureBoxSizeMode.Zoom; preview.BackColor = Color.White; pages[0].Controls.Add(preview);
            list.SelectedIndexChanged += delegate { ShowPreview(); };
            int at = pet.Skins.FindIndex(s => s.Id == pet.SkinId); list.SelectedIndex = at >= 0 ? at : (list.Items.Count > 0 ? 0 : -1);

            // ---- 2: connect ----
            AddText(pages[1], Lang.T("连接 Claude Code", "Connect Claude Code"), 0, 0, 480, 30, true);
            Label how = AddText(pages[1], Lang.T("点下面的按钮，我会在 Claude Code 的设置里加上两样东西：\r\n  · 状态记录（hooks）：让小人知道 Claude 在忙、做完了、还是在等你；\r\n  · 状态栏：让面板里的会话显示名称、所在项目和上下文窗口大小。\r\n\r\n改之前会先把你原来的 settings.json 备份在同一个文件夹里；你自己的状态栏会保留。以后在右键菜单点“断开 Claude Code 连接”就能一键还原。",
                "The button below adds two things to Claude Code's settings:\r\n  · hooks: so the pet knows Claude is busy, done, or waiting for you;\r\n  · a status line: lets the panel show each session's name, project and context window size.\r\n\r\nYour current settings.json is backed up in the same folder first, and your own status line is kept. \"Disconnect\" in the right-click menu restores everything in one click."), 0, 36, 480, 20, false);
            Label will = AddText(pages[1], Lang.T("将修改：", "Will change: ") + Connector.SettingsPath(), 0, how.Bottom + 6, 480, 72, false);
            connect.Text = Lang.T("连接 Claude Code", "Connect Claude Code"); connect.SetBounds(0, will.Bottom + 8, 300, 38); connect.BackColor = Color.FromArgb(0xd9, 0x77, 0x57); connect.ForeColor = Color.White; connect.FlatStyle = FlatStyle.Flat; connect.FlatAppearance.BorderSize = 0;
            connect.Font = new Font(Font, FontStyle.Bold); connect.Click += delegate { DoConnect(); }; pages[1].Controls.Add(connect);
            result.SetBounds(0, connect.Bottom + 8, 480, 64); result.Multiline = true; result.ReadOnly = true; result.BorderStyle = BorderStyle.None; result.BackColor = BackColor; result.ScrollBars = ScrollBars.Vertical; result.TabStop = false;
            result.Text = Lang.T("还没有连接。点上面的按钮。", "Not connected yet. Click the button above."); result.ForeColor = Color.FromArgb(0x7a, 0x6f, 0x60); pages[1].Controls.Add(result);

            // ---- 3: done ----
            AddText(pages[2], Lang.T("好了", "All set"), 0, 0, 480, 30, true);
            Label done = AddText(pages[2], Lang.T("新开一个 Claude Code 会话就行；已经开着的会话要重新打开一次，才会带上新设置。\r\n\r\n小人平时安静待在右下角，可以拖到别处；右键它（或托盘图标）有更多选项。",
                "Start a new Claude Code session. Sessions that are already open need to be reopened once to pick up the new settings.\r\n\r\nThe pet stays quietly in the bottom-right corner (drag it anywhere); right-click it or the tray icon for more options."), 0, 36, 480, 20, false);
            autostart.Text = Lang.T("开机时自动启动小人", "Start the pet when I sign in to Windows"); autostart.SetBounds(0, done.Bottom + 10, 460, 24); autostart.Checked = false; pages[2].Controls.Add(autostart);

            back.Text = Lang.T("上一步", "Back"); back.SetBounds(300, 554, 90, 30); back.Click += delegate { Go(cur - 1); };
            next.Text = Lang.T("下一步", "Next"); next.SetBounds(400, 554, 100, 30); next.Click += delegate { if (cur == 2) Finish(); else Go(cur + 1); };
            Controls.Add(back); Controls.Add(next);
            logLink.Text = Lang.T("打开日志文件夹", "Open the log folder"); logLink.SetBounds(20, 560, 270, 20); logLink.AutoSize = false;
            logLink.LinkClicked += delegate { OpenLog(); }; Controls.Add(logLink);
            FormClosed += delegate { if (!pet.SetupDone) { pet.SetupDone = true; pet.SaveSettings(); } };
            connected = Connector.IsConnected();
            if (connected) ShowOk(Lang.T("✓ 已经连接。新开一个 Claude Code 会话就会生效。", "✓ Already connected. A new Claude Code session will use it."));
            PlaceOnPetScreen(); Shown += delegate { Activate(); };
            Go(0);
        }

        // body text gets the height its wrapped text really needs (fonts and DPI differ per machine); the caller stacks the next control under the returned label
        Label AddText(Panel p, string text, int x, int y, int w, int h, bool title)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(x, y, w, h); l.AutoSize = false;
            if (title) { l.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold); l.ForeColor = Color.FromArgb(0xd9, 0x77, 0x57); }
            else
            {
                l.ForeColor = Color.FromArgb(0x2b, 0x2d, 0x33);
                p.Controls.Add(l); // the font is inherited from the form only once the label has a parent
                Size need = TextRenderer.MeasureText(text, l.Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                l.Height = Math.Max(h, need.Height + 8);
            }
            if (l.Parent == null) p.Controls.Add(l);
            return l;
        }

        void ShowPreview()
        {
            if (list.SelectedIndex < 0) return;
            Skin s = pet.Skins[list.SelectedIndex];
            Bitmap[] fr = s.Frames("approval");
            if (fr.Length == 0) fr = s.Frames("idle");
            preview.Image = fr.Length > 0 ? fr[0] : null;
        }

        void Go(int n)
        {
            if (cur == 0 && n > 0 && list.SelectedIndex >= 0) { pet.SelectSkin(pet.Skins[list.SelectedIndex].Id); }
            cur = Math.Max(0, Math.Min(2, n));
            for (int i = 0; i < 3; i++) pages[i].Visible = i == cur;
            step.Text = Lang.T("第 " + (cur + 1) + " 步，共 3 步", "Step " + (cur + 1) + " of 3");
            back.Visible = cur > 0;
            next.Text = cur == 2 ? Lang.T("完成", "Finish") : cur == 1 && !connected ? Lang.T("先跳过", "Skip for now") : Lang.T("下一步", "Next");
        }

        void ShowOk(string text) { result.ForeColor = Color.FromArgb(0x2e, 0x8b, 0x57); result.Text = text; connect.Text = Lang.T("已连接 ✓（点这里可重新连接）", "Connected ✓ (click again = reconnect)"); }

        void OpenLog()
        {
            try { string f = Diag.Path_(); System.Diagnostics.Process.Start("explorer.exe", string.IsNullOrEmpty(f) ? "\"" + Env.DataDir() + "\"" : "/select,\"" + f + "\""); }
            catch (Exception e) { Diag.Error("could not open the log folder", e); }
        }

        // the guide opens on the screen where the pet is (never on a monitor that is off or out of sight)
        void PlaceOnPetScreen()
        {
            try
            {
                Rectangle wa = pet.PetArea();
                Location = new Point(Math.Max(wa.Left, wa.Left + (wa.Width - Width) / 2), Math.Max(wa.Top, wa.Top + (wa.Height - Height) / 2));
                pet.Log("guide window at " + Left + "," + Top + " " + Width + "x" + Height + " on screen " + wa);
            }
            catch (Exception e) { Diag.Error("could not place the guide window", e); }
        }

        void DoConnect()
        {
            connect.Enabled = false; result.ForeColor = Color.FromArgb(0x7a, 0x6f, 0x60); result.Text = Lang.T("正在连接…", "Connecting…"); result.Refresh();
            string err;
            try { err = Connector.Connect(); }
            catch (Exception e) { Diag.Error("connect crashed", e); err = Lang.T("连接时出现意外错误：", "Unexpected error while connecting: ") + e.GetType().Name + ": " + e.Message + "\r\n" + Lang.T("日志：", "Log: ") + Diag.Path_(); }
            connect.Enabled = true;
            pet.Log("connect " + (err == null ? "ok" : "FAILED: " + err.Replace("\r\n", " | ")));
            if (err != null)
            {
                result.ForeColor = Color.FromArgb(0xd2, 0x4a, 0x4a);
                result.Text = Lang.T("✗ 连接失败，Claude Code 的设置没有被改动。\r\n", "✗ Connecting failed; Claude Code's settings were not changed.\r\n") + err;
                return;
            }
            connected = true; pet.RefreshLinked();
            string b = Connector.LastBackup();
            ShowOk(Lang.T("✓ 已连接。新开一个 Claude Code 会话就会生效。\r\n", "✓ Connected. A new Claude Code session will use it.\r\n") + (b != null ? Lang.T("原设置已备份为：", "Original settings backed up as: ") + Path.GetFileName(b) : Lang.T("（原来没有设置文件，已新建。）", "(There was no settings file; one was created.)")));
            Go(1);
        }

        void Finish()
        {
            if (autostart.Checked) pet.SetAutostart(true);
            pet.SetupDone = true; pet.SaveSettings();
            Close();
            pet.HelloBubble();
        }
    }
}
