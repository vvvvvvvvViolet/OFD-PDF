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
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0) };
            Setup(btnAdd, "เพิ่มไฟล์…", (s, e) => AddFilesDialog());
            Setup(btnAddFolder, "เพิ่มโฟลเดอร์…", (s, e) => AddFolderDialog());
            Setup(btnRemove, "ลบที่เลือก", (s, e) => RemoveSelected());
            Setup(btnClear, "ล้างทั้งหมด", (s, e) => { known.Clear(); list.Items.Clear(); UpdateUi(); });
            top.Controls.AddRange(new Control[] { btnAdd, btnAddFolder, btnRemove, btnClear });

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

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(8) };

            chkSame.Text = "บันทึก PDF ไว้โฟลเดอร์เดียวกับไฟล์ต้นฉบับ";
            chkSame.Checked = true;
            chkSame.AutoSize = true;
            chkSame.Location = new Point(8, 8);
            chkSame.CheckedChanged += (s, e) => UpdateUi(keepStatus: true);

            txtOut.Location = new Point(8, 34);
            txtOut.Width = 560;
            txtOut.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            btnBrowse.Text = "เลือกโฟลเดอร์…";
            btnBrowse.Width = 120;
            btnBrowse.Location = new Point(576, 32);
            btnBrowse.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnBrowse.Click += (s, e) => BrowseOutput();

            chkOverwrite.Text = "เขียนทับไฟล์ PDF เดิม (ถ้าไม่ติ๊ก จะสร้างชื่อใหม่ เช่น file (1).pdf)";
            chkOverwrite.AutoSize = true;
            chkOverwrite.Location = new Point(8, 62);

            progress.Location = new Point(8, 92);
            progress.Size = new Size(560, 24);
            progress.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            btnConvert.Text = "แปลงเป็น PDF";
            btnConvert.Font = new Font(Font, FontStyle.Bold);
            btnConvert.Size = new Size(120, 28);
            btnConvert.Location = new Point(576, 90);
            btnConvert.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnConvert.Click += (s, e) => { if (busy) cts.Cancel(); else StartConvert(); };

            btnOpen.Text = "เปิดโฟลเดอร์ผลลัพธ์";
            btnOpen.AutoSize = true;
            btnOpen.Location = new Point(8, 120);
            btnOpen.Visible = false;
            btnOpen.Click += (s, e) => OpenOutput();

            status.AutoSize = true;
            status.Location = new Point(160, 125);

            bottom.Controls.AddRange(new Control[] { chkSame, txtOut, btnBrowse, chkOverwrite, progress, btnConvert, btnOpen, status });

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
            foreach (var it in todo)
            {
                if (token.IsCancellationRequested) break;
                SetState(it, State.Converting, null);
                string path = (string)it.Tag;
                var r = await Task.Run(() => Converter.Convert(path, outDir, overwrite));
                if (r.Success) { ok++; SetState(it, State.Done, Path.GetFileName(r.OutputPath)); }
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
            btnConvert.Text = busy ? "ยกเลิก" : "แปลงเป็น PDF";
            btnConvert.Enabled = busy || list.Items.Count > 0;
            if (!keepStatus) status.Text = list.Items.Count + " ไฟล์ในรายการ (ลากไฟล์ .ofd มาวางได้)";
        }
    }
}
