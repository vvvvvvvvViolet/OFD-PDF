using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OfdToPdf
{
    public sealed class MainForm : Form
    {
        private enum State { Waiting, Converting, Done, Failed }

        private readonly ListView list = new ListView();
        private readonly Button btnAdd = new Button();
        private readonly Button btnAddFolder = new Button();
        private readonly Button btnRemove = new Button();
        private readonly Button btnClear = new Button();
        private readonly Button btnConvert = new Button();
        private readonly Button btnBrowse = new Button();
        private readonly Button btnOpen = new Button();
        private readonly TextBox txtOut = new TextBox();
        private readonly CheckBox chkSame = new CheckBox();
        private readonly CheckBox chkOverwrite = new CheckBox();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label status = new Label();

        private readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource cts;
        private bool busy;

        public MainForm()
        {
            Text = "OFD → PDF Converter";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            MinimumSize = new Size(680, 440);
            Size = new Size(780, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            BuildUi();
            UpdateUi();
        }

        private void BuildUi()
        {
            // Toolbar: file actions on the left, the main "Convert" action always visible here.
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(8, 8, 8, 4),
            };
            Setup(btnAdd, "เพิ่มไฟล์…", (s, e) => AddFilesDialog());
            Setup(btnAddFolder, "เพิ่มโฟลเดอร์…", (s, e) => AddFolderDialog());
            Setup(btnRemove, "ลบที่เลือก", (s, e) => RemoveSelected());
            Setup(btnClear, "ล้างทั้งหมด", (s, e) => { known.Clear(); list.Items.Clear(); UpdateUi(); });
            Setup(btnConvert, "▶ แปลงเป็น PDF", (s, e) => { if (busy) cts.Cancel(); else StartConvert(); });
            btnConvert.Font = new Font(Font, FontStyle.Bold);
            btnConvert.BackColor = Color.FromArgb(0, 120, 215);
            btnConvert.ForeColor = Color.White;
            btnConvert.FlatStyle = FlatStyle.Flat;
            btnConvert.Padding = new Padding(10, 2, 10, 2);
            btnConvert.Margin = new Padding(16, 3, 3, 3);
            Setup(btnOpen, "เปิดโฟลเดอร์ผลลัพธ์", (s, e) => OpenOutput());
            btnOpen.Visible = false;
            top.Controls.AddRange(new Control[] { btnAdd, btnAddFolder, btnRemove, btnClear, btnConvert, btnOpen });

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
            list.AllowDrop = true;
            list.Columns.Add("ไฟล์ OFD", 440);
            list.Columns.Add("สถานะ", 260);
            list.DragEnter += OnDragEnter;
            list.DragDrop += OnDragDrop;
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

            // Options + progress. TableLayoutPanel sizes itself, so nothing gets pushed off-screen.
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(8, 4, 8, 8),
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            chkSame.Text = "บันทึก PDF ไว้โฟลเดอร์เดียวกับไฟล์ต้นฉบับ";
            chkSame.Checked = true;
            chkSame.AutoSize = true;
            chkSame.CheckedChanged += (s, e) => UpdateUi(keepStatus: true);

            txtOut.Dock = DockStyle.Fill;
            Setup(btnBrowse, "เลือกโฟลเดอร์…", (s, e) => BrowseOutput());

            chkOverwrite.Text = "เขียนทับไฟล์ PDF เดิม (ถ้าไม่ติ๊ก จะสร้างชื่อใหม่ เช่น file (1).pdf)";
            chkOverwrite.AutoSize = true;

            progress.Dock = DockStyle.Fill;
            progress.Height = 22;

            status.AutoSize = true;
            status.Anchor = AnchorStyles.Left;

            bottom.Controls.Add(chkSame, 0, 0);
            bottom.SetColumnSpan(chkSame, 2);
            bottom.Controls.Add(txtOut, 0, 1);
            bottom.Controls.Add(btnBrowse, 1, 1);
            bottom.Controls.Add(chkOverwrite, 0, 2);
            bottom.SetColumnSpan(chkOverwrite, 2);
            bottom.Controls.Add(progress, 0, 3);
            bottom.SetColumnSpan(progress, 2);
            bottom.Controls.Add(status, 0, 4);
            bottom.SetColumnSpan(status, 2);

            // Add Fill first so docked Top/Bottom panels claim their space correctly.
            Controls.Add(list);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private static void Setup(Button b, string text, EventHandler click)
        {
            b.Text = text;
            b.AutoSize = true;
            b.Click += click;
        }

        // ---- file list -------------------------------------------------------------------

        private void AddFiles(IEnumerable<string> paths)
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    AddFiles(Directory.EnumerateFiles(p, "*.ofd", SearchOption.AllDirectories));
                    continue;
                }
                if (!File.Exists(p) || !p.EndsWith(".ofd", StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.GetFullPath(p);
                if (!known.Add(full)) continue;

                var it = new ListViewItem(full) { Tag = full, UseItemStyleForSubItems = false };
                it.SubItems.Add("");
                list.Items.Add(it);
                SetState(it, State.Waiting, null);
            }
            UpdateUi();
        }

        private void AddFilesDialog()
        {
            using (var d = new OpenFileDialog { Filter = "OFD (*.ofd)|*.ofd", Multiselect = true, CheckFileExists = true })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }

        private void AddFolderDialog()
        {
            using (var d = new FolderBrowserDialog { Description = "เลือกโฟลเดอร์ที่มีไฟล์ .ofd (รวมโฟลเดอร์ย่อย)" })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(new[] { d.SelectedPath });
        }

        private void RemoveSelected()
        {
            if (busy) return;
            foreach (ListViewItem it in list.SelectedItems.Cast<ListViewItem>().ToList())
            {
                known.Remove((string)it.Tag);
                list.Items.Remove(it);
            }
            UpdateUi();
        }

        private void BrowseOutput()
        {
            using (var d = new FolderBrowserDialog())
                if (d.ShowDialog(this) == DialogResult.OK) txtOut.Text = d.SelectedPath;
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (busy) return;
            AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        // ---- conversion ------------------------------------------------------------------

        private async void StartConvert()
        {
            string outDir = chkSame.Checked ? null : txtOut.Text.Trim();
            if (!chkSame.Checked && (outDir.Length == 0 || !Directory.Exists(outDir)))
            {
                MessageBox.Show(this, "กรุณาเลือกโฟลเดอร์ปลายทางที่มีอยู่จริง", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var todo = list.Items.Cast<ListViewItem>().Where(i => (State)i.SubItems[1].Tag != State.Done).ToList();
            if (todo.Count == 0)
            {
                MessageBox.Show(this, "ไม่มีไฟล์ที่รอแปลง", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool overwrite = chkOverwrite.Checked;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            busy = true;
            progress.Maximum = todo.Count;
            progress.Value = 0;
            UpdateUi();

            int ok = 0, fail = 0;
            var missingFonts = new SortedSet<string>();
            foreach (var it in todo)
            {
                if (token.IsCancellationRequested) break;
                SetState(it, State.Converting, null);
                string path = (string)it.Tag;
                var r = await Task.Run(() => Converter.Convert(path, outDir, overwrite));
                if (r.Success)
                {
                    ok++;
                    string detail = Path.GetFileName(r.OutputPath);
                    if (r.MissingFonts.Count > 0)
                    {
                        detail += "  ⚠ ไม่มีฟอนต์: " + string.Join(", ", r.MissingFonts);
                        foreach (string font in r.MissingFonts) missingFonts.Add(font);
                    }
                    SetState(it, State.Done, detail);
                }
                else { fail++; SetState(it, State.Failed, r.Error); }
                progress.Value++;
            }

            // Anything left in "Converting" or untouched after a cancel goes back to waiting.
            foreach (var it in todo)
                if ((State)it.SubItems[1].Tag == State.Converting) SetState(it, State.Waiting, null);

            busy = false;
            status.Text = string.Format("เสร็จสิ้น: สำเร็จ {0}, ล้มเหลว {1}{2}", ok, fail,
                token.IsCancellationRequested ? " (ยกเลิกกลางคัน)" : "");
            btnOpen.Visible = ok > 0;
            UpdateUi(keepStatus: true);

            if (missingFonts.Count > 0) ShowMissingFontsHelp(missingFonts);
        }

        private void ShowMissingFontsHelp(IEnumerable<string> fonts)
        {
            string msg =
                "ไฟล์ OFD ใช้ฟอนต์ที่ไม่ได้ฝังมาในไฟล์ และเครื่องนี้ไม่มีฟอนต์เหล่านี้:\n\n    " +
                string.Join(", ", fonts) +
                "\n\nตัวอักษรใน PDF อาจเพี้ยนหรือเป็นกล่องสี่เหลี่ยม วิธีแก้ (เลือกอย่างใดอย่างหนึ่ง):\n\n" +
                "1) ติดตั้งฟอนต์จีนของ Windows:\n" +
                "    Settings → Apps → Optional features → Add a feature →\n" +
                "    \"Chinese (Simplified) Supplemental Fonts\" (มี 楷体 KaiTi, 仿宋 FangSong ฯลฯ)\n\n" +
                "2) คัดลอกไฟล์ฟอนต์ (.ttf/.ttc/.otf) ไปไว้ในโฟลเดอร์:\n    " + FontCheck.CustomFontDir +
                "\n\nจากนั้นปิดโปรแกรมแล้วเปิดใหม่ และแปลงไฟล์อีกครั้ง (ติ๊ก \"เขียนทับ\" เพื่อแทนที่ PDF เดิม)\n\n" +
                "ต้องการเปิดโฟลเดอร์ fonts เลยไหม?";
            if (MessageBox.Show(this, msg, "ฟอนต์ไม่ครบ", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                Directory.CreateDirectory(FontCheck.CustomFontDir);
                Process.Start("explorer.exe", "\"" + FontCheck.CustomFontDir + "\"");
            }
        }

        private void OpenOutput()
        {
            string dir = chkSame.Checked ? null : txtOut.Text.Trim();
            if (dir == null)
            {
                var first = list.Items.Cast<ListViewItem>().FirstOrDefault();
                if (first == null) return;
                dir = Path.GetDirectoryName((string)first.Tag);
            }
            if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        private void SetState(ListViewItem it, State s, string detail)
        {
            var sub = it.SubItems[1];
            sub.Tag = s;
            switch (s)
            {
                case State.Waiting: sub.Text = "รอแปลง"; sub.ForeColor = Color.Black; break;
                case State.Converting: sub.Text = "กำลังแปลง…"; sub.ForeColor = Color.CadetBlue; break;
                case State.Done: sub.Text = "สำเร็จ → " + detail; sub.ForeColor = Color.SeaGreen; break;
                case State.Failed: sub.Text = "ล้มเหลว: " + detail; sub.ForeColor = Color.IndianRed; break;
            }
            it.EnsureVisible();
        }

        private void UpdateUi(bool keepStatus = false)
        {
            txtOut.Enabled = btnBrowse.Enabled = !chkSame.Checked && !busy;
            btnAdd.Enabled = btnAddFolder.Enabled = btnRemove.Enabled = btnClear.Enabled = !busy;
            chkSame.Enabled = chkOverwrite.Enabled = !busy;
            btnConvert.Text = busy ? "■ ยกเลิก" : "▶ แปลงเป็น PDF";
            btnConvert.Enabled = busy || list.Items.Count > 0;
            if (!keepStatus) status.Text = list.Items.Count + " ไฟล์ในรายการ — ลากไฟล์ .ofd มาวาง แล้วกด \"▶ แปลงเป็น PDF\" ด้านบน";
        }
    }
}
