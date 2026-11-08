using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OfdToPdf
{
    public sealed class MainForm : Form
    {
        private enum State { Waiting, Converting, Done, Skipped, Failed }

        /// <summary>Everything we know about one file in the list (kept in ListViewItem.Tag).</summary>
        private sealed class Row
        {
            public string FilePath;
            public State State;
            public OfdInfo Info;            // null until the background scan finishes
            public ConvertOutcome Outcome;  // last conversion result
        }

        private static readonly Color Blue = Color.FromArgb(0, 95, 184);
        private static readonly Color BlueHover = Color.FromArgb(0, 78, 152);
        private static readonly Color Green = Color.FromArgb(16, 124, 16);
        private static readonly Color Red = Color.FromArgb(196, 43, 28);
        private static readonly Color Amber = Color.FromArgb(157, 93, 0);

        private readonly MenuStrip menu = new MenuStrip();
        private readonly ListView list = new ListView();
        private readonly Label emptyState = new Label();
        private readonly Button btnAdd = new Button();
        private readonly Button btnAddFolder = new Button();
        private readonly Button btnRemove = new Button();
        private readonly Button btnClear = new Button();
        private readonly Button btnConvert = new Button();
        private readonly RadioButton rbSame = new RadioButton();
        private readonly RadioButton rbFolder = new RadioButton();
        private readonly TextBox txtOut = new TextBox();
        private readonly Button btnBrowse = new Button();
        private readonly ComboBox cmbConflict = new ComboBox();
        private readonly CheckBox chkOpenWhenDone = new CheckBox();
        private readonly StatusStrip statusStrip = new StatusStrip();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        private readonly ToolStripProgressBar progress = new ToolStripProgressBar();
        private readonly ContextMenuStrip rowMenu = new ContextMenuStrip();
        private ToolStripMenuItem miConvertMenu, miSendTo;

        private readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly AppSettings settings;
        private readonly bool autoStart;
        private readonly string snapshotPath;
        private readonly float k; // DPI scale
        private CancellationTokenSource cts;
        private bool busy, closeWhenDone;

        public MainForm(IEnumerable<string> initialFiles, bool autoStart, string snapshotPath)
        {
            this.autoStart = autoStart;
            this.snapshotPath = snapshotPath;
            // A CI snapshot must not depend on (or change) the user's saved settings.
            settings = snapshotPath == null ? AppSettings.Load() : new AppSettings();

            k = DeviceDpi / 96f;
            AutoScaleMode = AutoScaleMode.None; // fonts are in points already; pixel sizes use k
            Font = new Font("Segoe UI", 9f);
            Text = "OFD → PDF Converter " + VersionText();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            MinimumSize = Px(720, 480);
            Size = settings.WindowWidth > 0
                ? new Size(Math.Max(settings.WindowWidth, MinimumSize.Width), Math.Max(settings.WindowHeight, MinimumSize.Height))
                : Px(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            SuspendLayout();
            BuildMenu();
            BuildUi();
            ApplySettings();
            ResumeLayout(true);

            AddFiles(initialFiles ?? Enumerable.Empty<string>());
            UpdateUi();

            Shown += OnShown;
            FormClosing += OnFormClosing;
        }

        // ---- layout ----------------------------------------------------------------------

        private void BuildMenu()
        {
            var file = new ToolStripMenuItem("ไฟล์");
            file.DropDownItems.Add(Item("เพิ่มไฟล์…", Keys.Control | Keys.O, (s, e) => AddFilesDialog()));
            file.DropDownItems.Add(Item("เพิ่มโฟลเดอร์…", Keys.Control | Keys.Shift | Keys.O, (s, e) => AddFolderDialog()));
            file.DropDownItems.Add(new ToolStripSeparator());
            miConvertMenu = Item("แปลงเป็น PDF", Keys.F5, (s, e) => OnConvertClicked());
            file.DropDownItems.Add(miConvertMenu);
            file.DropDownItems.Add(new ToolStripSeparator());
            var exit = Item("ออก", Keys.None, (s, e) => Close());
            exit.ShortcutKeyDisplayString = "Alt+F4";
            file.DropDownItems.Add(exit);

            var tools = new ToolStripMenuItem("เครื่องมือ");
            tools.DropDownItems.Add(Item("เปิดโฟลเดอร์ฟอนต์เพิ่มเติม", Keys.None, (s, e) => OpenFontsFolder()));
            tools.DropDownItems.Add(Item("เปิดไฟล์บันทึกการทำงาน (log)", Keys.None, (s, e) => OpenLog()));
            tools.DropDownItems.Add(new ToolStripSeparator());
            miSendTo = Item("เพิ่มในเมนู \"ส่งไปยัง\" (Send to) ของ Windows", Keys.None, (s, e) => ToggleSendTo());
            tools.DropDownItems.Add(miSendTo);
            tools.DropDownOpening += (s, e) => miSendTo.Checked = File.Exists(SendToShortcut());

            var help = new ToolStripMenuItem("ช่วยเหลือ");
            help.DropDownItems.Add(Item("วิธีใช้", Keys.F1, (s, e) => ShowHelp()));
            help.DropDownItems.Add(Item("เกี่ยวกับโปรแกรม", Keys.None, (s, e) => ShowAbout()));

            menu.Items.AddRange(new ToolStripItem[] { file, tools, help });
            MainMenuStrip = menu;
        }

        private static ToolStripMenuItem Item(string text, Keys keys, EventHandler click)
        {
            var mi = new ToolStripMenuItem(text, null, click);
            if (keys != Keys.None) mi.ShortcutKeys = keys;
            return mi;
        }

        private void BuildUi()
        {
            // Toolbar: list actions on the left, the one primary action on the right.
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(Px(8), Px(8), Px(8), Px(6)),
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            SetupButton(btnAdd, "＋  เพิ่มไฟล์…", (s, e) => AddFilesDialog());
            SetupButton(btnAddFolder, "＋  เพิ่มโฟลเดอร์…", (s, e) => AddFolderDialog());
            SetupButton(btnRemove, "ลบที่เลือก", (s, e) => RemoveSelected());
            SetupButton(btnClear, "ล้างรายการ", (s, e) => ClearList());
            left.Controls.AddRange(new Control[] { btnAdd, btnAddFolder, btnRemove, btnClear });

            SetupButton(btnConvert, "▶  แปลงเป็น PDF", (s, e) => OnConvertClicked());
            btnConvert.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            btnConvert.FlatStyle = FlatStyle.Flat;
            btnConvert.FlatAppearance.BorderSize = 0;
            btnConvert.BackColor = Blue;
            btnConvert.FlatAppearance.MouseOverBackColor = BlueHover;
            btnConvert.ForeColor = Color.White;
            btnConvert.Padding = new Padding(Px(14), Px(4), Px(14), Px(4));
            btnConvert.Anchor = AnchorStyles.Right;
            toolbar.Controls.Add(left, 0, 0);
            toolbar.Controls.Add(btnConvert, 1, 0);

            // File list, with an empty state that doubles as a drop zone.
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(8), 0, Px(8), Px(4)) };
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.ShowItemToolTips = true;
            list.AllowDrop = true;
            list.Columns.Add("ไฟล์", Px(250));
            list.Columns.Add("หน้า", Px(55), HorizontalAlignment.Right);
            list.Columns.Add("สถานะ", Px(110));
            list.Columns.Add("รายละเอียด", Px(400));
            list.DragEnter += OnDragEnter;
            list.DragDrop += OnDragDrop;
            list.KeyDown += OnListKeyDown;
            list.DoubleClick += (s, e) => OpenSelectedPdf();
            list.SelectedIndexChanged += (s, e) => UpdateUi(keepStatus: true);
            list.Resize += (s, e) => FitLastColumn();
            list.ContextMenuStrip = rowMenu;
            BuildRowMenu();

            emptyState.Dock = DockStyle.Fill;
            emptyState.TextAlign = ContentAlignment.MiddleCenter;
            emptyState.Font = new Font(Font.FontFamily, 11f);
            emptyState.ForeColor = SystemColors.GrayText;
            emptyState.BackColor = SystemColors.Window;
            emptyState.BorderStyle = BorderStyle.FixedSingle;
            emptyState.Text = "ลากไฟล์ .ofd หรือโฟลเดอร์มาวางที่นี่\n\nหรือคลิกเพื่อเลือกไฟล์";
            emptyState.Cursor = Cursors.Hand;
            emptyState.AllowDrop = true;
            emptyState.Click += (s, e) => AddFilesDialog();
            emptyState.DragEnter += OnDragEnter;
            emptyState.DragDrop += OnDragDrop;
            listHost.Controls.Add(list);
            listHost.Controls.Add(emptyState);
            emptyState.BringToFront();

            // Where to save.
            var options = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(Px(8), Px(4), Px(8), Px(8)),
            };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var title = new Label { Text = "บันทึก PDF", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 3) };
            rbSame.Text = "ไว้โฟลเดอร์เดียวกับไฟล์ต้นฉบับ";
            rbSame.AutoSize = true;
            rbFolder.Text = "ไว้ในโฟลเดอร์:";
            rbFolder.AutoSize = true;
            rbSame.CheckedChanged += (s, e) => UpdateUi(keepStatus: true);
            txtOut.Dock = DockStyle.Fill;
            txtOut.Margin = new Padding(3, Px(4), 3, 3);
            SetupButton(btnBrowse, "เลือก…", (s, e) => BrowseOutput());
            var lblConflict = new Label { Text = "ถ้ามี PDF ชื่อเดียวกันอยู่แล้ว:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, Px(6), 3, 3) };
            cmbConflict.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbConflict.Items.AddRange(new object[] { "สร้างชื่อใหม่ เช่น file (1).pdf", "เขียนทับไฟล์เดิม", "ข้ามไฟล์นั้น" });
            cmbConflict.Width = Px(260);
            cmbConflict.Anchor = AnchorStyles.Left;
            chkOpenWhenDone.Text = "เปิดโฟลเดอร์ผลลัพธ์เมื่อแปลงเสร็จ";
            chkOpenWhenDone.AutoSize = true;

            options.Controls.Add(title, 0, 0);
            options.SetColumnSpan(title, 3);
            options.Controls.Add(rbSame, 0, 1);
            options.SetColumnSpan(rbSame, 3);
            options.Controls.Add(rbFolder, 0, 2);
            options.Controls.Add(txtOut, 1, 2);
            options.Controls.Add(btnBrowse, 2, 2);
            options.Controls.Add(lblConflict, 0, 3);
            options.Controls.Add(cmbConflict, 1, 3);
            options.SetColumnSpan(cmbConflict, 2);
            options.Controls.Add(chkOpenWhenDone, 0, 4);
            options.SetColumnSpan(chkOpenWhenDone, 3);

            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            progress.Size = Px(220, 16);
            progress.Visible = false;
            statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, progress });
            statusStrip.SizingGrip = true;

            // Docking: the last control added is laid out first (outermost).
            Controls.Add(listHost);
            Controls.Add(options);
            Controls.Add(statusStrip);
            Controls.Add(toolbar);
            Controls.Add(menu);

            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
        }

        private void BuildRowMenu()
        {
            var open = new ToolStripMenuItem("เปิด PDF", null, (s, e) => OpenSelectedPdf()) { ShortcutKeyDisplayString = "Enter" };
            var reveal = new ToolStripMenuItem("แสดงในโฟลเดอร์", null, (s, e) => RevealSelected());
            var again = new ToolStripMenuItem("แปลงไฟล์ที่เลือก", null, (s, e) => StartConvert(SelectedItems()));
            var copy = new ToolStripMenuItem("คัดลอกรายละเอียด", null, (s, e) => CopySelectedDetails()) { ShortcutKeyDisplayString = "Ctrl+C" };
            var remove = new ToolStripMenuItem("ลบออกจากรายการ", null, (s, e) => RemoveSelected()) { ShortcutKeyDisplayString = "Del" };
            rowMenu.Items.AddRange(new ToolStripItem[] { open, reveal, new ToolStripSeparator(), again, copy, new ToolStripSeparator(), remove });
            rowMenu.Opening += (s, e) =>
            {
                var rows = SelectedItems().Select(RowOf).ToList();
                if (rows.Count == 0) { e.Cancel = true; return; }
                open.Enabled = rows.Count == 1 && HasPdf(rows[0]);
                reveal.Enabled = rows.Count == 1;
                again.Enabled = remove.Enabled = !busy;
            };
        }

        private void SetupButton(Button b, string text, EventHandler click)
        {
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(Px(6), Px(2), Px(6), Px(2));
            b.Click += click;
        }

        private void FitLastColumn()
        {
            if (list.Columns.Count < 4) return;
            int used = list.Columns[0].Width + list.Columns[1].Width + list.Columns[2].Width;
            int w = list.ClientSize.Width - used - 2;
            if (w > Px(120)) list.Columns[3].Width = w;
        }

        private int Px(int v) { return (int)Math.Round(v * k); }
        private Size Px(int w, int h) { return new Size(Px(w), Px(h)); }

        // ---- settings --------------------------------------------------------------------

        private void ApplySettings()
        {
            rbSame.Checked = settings.SaveNextToSource;
            rbFolder.Checked = !settings.SaveNextToSource;
            txtOut.Text = settings.OutputDir;
            cmbConflict.SelectedIndex = (int)settings.Conflict;
            chkOpenWhenDone.Checked = settings.OpenFolderWhenDone;
        }

        private void CaptureSettings()
        {
            settings.SaveNextToSource = rbSame.Checked;
            settings.OutputDir = txtOut.Text.Trim();
            settings.Conflict = (ConflictMode)Math.Max(0, cmbConflict.SelectedIndex);
            settings.OpenFolderWhenDone = chkOpenWhenDone.Checked;
            if (WindowState == FormWindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
        }

        private void SaveSettings()
        {
            if (snapshotPath != null) return;
            CaptureSettings();
            settings.Save();
        }

        // ---- file list -------------------------------------------------------------------

        private void AddFiles(IEnumerable<string> paths)
        {
            var added = new List<ListViewItem>();
            int ignored = 0;
            list.BeginUpdate();
            foreach (string full in Program.ExpandInputs(paths))
            {
                if (!full.EndsWith(".ofd", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { ignored++; continue; }
                if (!known.Add(full)) continue;
                var it = new ListViewItem(new[] { "", "", "", "" }) { Tag = new Row { FilePath = full }, UseItemStyleForSubItems = false };
                list.Items.Add(it);
                Render(it);
                added.Add(it);
            }
            list.EndUpdate();
            UpdateUi();
            if (ignored > 0 && added.Count == 0)
                statusLabel.Text = "ไม่มีไฟล์ .ofd ในสิ่งที่เพิ่มเข้ามา";
            else if (added.Count > 0)
                statusLabel.Text = string.Format("เพิ่ม {0} ไฟล์ — กด \"แปลงเป็น PDF\" เมื่อพร้อม", added.Count);
            ScanAsync(added);
        }

        /// <summary>Reads page count / signatures / validity in the background so the list can warn early.</summary>
        private async void ScanAsync(List<ListViewItem> items)
        {
            foreach (var it in items)
            {
                var row = RowOf(it);
                string path = row.FilePath;
                var info = await Task.Run(() => OfdInfo.Inspect(path));
                if (row.Info == null) row.Info = info; // a finished conversion may already have set it
                if (it.ListView != null) Render(it);
            }
        }

        private void AddFilesDialog()
        {
            if (busy) return;
            using (var d = new OpenFileDialog { Filter = "ไฟล์ OFD (*.ofd)|*.ofd", Multiselect = true, CheckFileExists = true, Title = "เลือกไฟล์ OFD" })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }

        private void AddFolderDialog()
        {
            if (busy) return;
            using (var d = new FolderBrowserDialog { Description = "เลือกโฟลเดอร์ที่มีไฟล์ .ofd (รวมโฟลเดอร์ย่อย)" })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(new[] { d.SelectedPath });
        }

        private void RemoveSelected()
        {
            if (busy) return;
            list.BeginUpdate();
            foreach (ListViewItem it in SelectedItems())
            {
                known.Remove(RowOf(it).FilePath);
                list.Items.Remove(it);
            }
            list.EndUpdate();
            UpdateUi();
        }

        private void ClearList()
        {
            if (busy || list.Items.Count == 0) return;
            known.Clear();
            list.Items.Clear();
            UpdateUi();
        }

        private void BrowseOutput()
        {
            using (var d = new FolderBrowserDialog { Description = "เลือกโฟลเดอร์สำหรับเก็บไฟล์ PDF" })
            {
                if (Directory.Exists(txtOut.Text)) d.SelectedPath = txtOut.Text;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    txtOut.Text = d.SelectedPath;
                    rbFolder.Checked = true;
                }
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (busy) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddFiles(files);
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { OpenSelectedPdf(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                list.BeginUpdate();
                foreach (ListViewItem it in list.Items) it.Selected = true;
                list.EndUpdate();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C) { CopySelectedDetails(); e.Handled = true; }
        }

        private List<ListViewItem> SelectedItems()
        {
            return list.SelectedItems.Cast<ListViewItem>().ToList();
        }

        private static Row RowOf(ListViewItem it) { return (Row)it.Tag; }

        private static bool HasPdf(Row row)
        {
            return row.State == State.Done && row.Outcome != null && File.Exists(row.Outcome.OutputPath);
        }

        // ---- conversion ------------------------------------------------------------------

        private void OnConvertClicked()
        {
            if (busy)
            {
                cts.Cancel();
                btnConvert.Enabled = false;
                statusLabel.Text = "กำลังยกเลิก… รอให้ไฟล์ปัจจุบันเสร็จก่อน";
                return;
            }
            // Files already converted or skipped are not redone unless chosen via "แปลงไฟล์ที่เลือก".
            var todo = list.Items.Cast<ListViewItem>()
                .Where(i => RowOf(i).State != State.Done && RowOf(i).State != State.Skipped).ToList();
            if (todo.Count == 0)
            {
                if (list.Items.Count == 0) { AddFilesDialog(); return; }
                Notify("ทุกไฟล์ในรายการแปลงเสร็จแล้ว\nถ้าต้องการแปลงซ้ำ ให้คลิกขวาที่ไฟล์แล้วเลือก \"แปลงไฟล์ที่เลือก\"", MessageBoxIcon.Information);
                return;
            }
            StartConvert(todo);
        }

        private string ResolveOutputDir(out bool ok)
        {
            ok = true;
            if (rbSame.Checked) return null;
            string dir = txtOut.Text.Trim();
            if (dir.Length == 0)
            {
                Notify("กรุณาเลือกโฟลเดอร์สำหรับเก็บไฟล์ PDF", MessageBoxIcon.Warning);
                txtOut.Focus();
                ok = false;
                return null;
            }
            if (!Directory.Exists(dir))
            {
                if (snapshotPath == null && MessageBox.Show(this, "ไม่มีโฟลเดอร์นี้:\n" + dir + "\n\nสร้างใหม่ไหม?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    ok = false;
                    return null;
                }
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    Notify("สร้างโฟลเดอร์ไม่ได้: " + ex.Message, MessageBoxIcon.Error);
                    ok = false;
                    return null;
                }
            }
            return dir;
        }

        private async void StartConvert(List<ListViewItem> items)
        {
            if (busy || items.Count == 0) return;
            bool ok;
            string outDir = ResolveOutputDir(out ok);
            if (!ok) return;
            SaveSettings();
            var mode = (ConflictMode)Math.Max(0, cmbConflict.SelectedIndex);

            cts = new CancellationTokenSource();
            var token = cts.Token;
            busy = true;
            progress.Maximum = items.Count;
            progress.Value = 0;
            progress.Visible = true;
            UpdateUi();
            statusLabel.Text = "กำลังเตรียม…";

            await Task.Run(() => Converter.RefreshFonts()); // pick up fonts added since last time
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = new List<Row>();
            int index = 0;
            foreach (var it in items)
            {
                if (token.IsCancellationRequested || it.ListView == null) continue;
                var row = RowOf(it);
                row.State = State.Converting;
                Render(it);
                it.EnsureVisible();
                index++;
                statusLabel.Text = string.Format("กำลังแปลง {0}/{1}: {2}", index, items.Count, Path.GetFileName(row.FilePath));

                string path = row.FilePath;
                var r = await Task.Run(() => Converter.Convert(path, outDir, mode, claimed));
                row.Outcome = r;
                if (r.Info != null) row.Info = r.Info;
                row.State = r.Kind == OutcomeKind.Converted ? State.Done : r.Kind == OutcomeKind.Skipped ? State.Skipped : State.Failed;
                Render(it);
                done.Add(row);
                progress.Value = Math.Min(progress.Maximum, progress.Value + 1);
            }

            busy = false;
            progress.Visible = false;
            cts.Dispose();
            cts = null;
            bool cancelled = token.IsCancellationRequested;
            UpdateUi(keepStatus: true);
            Summarize(done, cancelled, items.Count);

            if (closeWhenDone) { Close(); return; }
            if (snapshotPath != null) SaveSnapshotAndExit();
        }

        private void Summarize(List<Row> rows, bool cancelled, int requested)
        {
            int converted = rows.Count(r => r.State == State.Done);
            int skipped = rows.Count(r => r.State == State.Skipped);
            var failed = rows.Where(r => r.State == State.Failed).ToList();

            var parts = new List<string> { "แปลงสำเร็จ " + converted + " ไฟล์" };
            if (skipped > 0) parts.Add("ข้าม " + skipped);
            if (failed.Count > 0) parts.Add("ล้มเหลว " + failed.Count);
            if (cancelled) parts.Add("หยุดกลางคัน (ยังไม่ได้แปลง " + (requested - rows.Count) + " ไฟล์)");
            statusLabel.Text = string.Join(" · ", parts);

            var outcomes = rows.Where(r => r.Outcome != null).Select(r => r.Outcome).ToList();
            var unresolved = outcomes.SelectMany(o => o.MissingFonts).Distinct().ToList();
            var overLimit = rows.Where(r => r.State == State.Done && r.Info != null && r.Info.OverPageLimit).ToList();
            int signed = rows.Count(r => r.State == State.Done && r.Info != null && r.Info.HasSignatures);
            var substituted = outcomes.SelectMany(o => o.SubstitutedFonts).Select(kv => kv.Key + " → " + kv.Value).Distinct().ToList();

            if (chkOpenWhenDone.Checked && converted > 0 && snapshotPath == null)
                Reveal(rows.First(r => r.State == State.Done).Outcome.OutputPath);

            bool nothingToSay = failed.Count == 0 && unresolved.Count == 0 && overLimit.Count == 0 && signed == 0 && !cancelled;
            if (nothingToSay || snapshotPath != null) return;

            var sb = new StringBuilder(statusLabel.Text + "\n");
            if (failed.Count > 0)
            {
                sb.Append("\nไฟล์ที่ล้มเหลว:\n");
                foreach (var r in failed.Take(8))
                    sb.Append("  • " + Path.GetFileName(r.FilePath) + ": " + (r.Outcome == null ? "" : r.Outcome.Error) + "\n");
                if (failed.Count > 8) sb.Append("  … และอีก " + (failed.Count - 8) + " ไฟล์ (ดูในรายการ)\n");
            }
            if (overLimit.Count > 0)
                sb.Append(string.Format("\n⚠ {0} ไฟล์มีมากกว่า {1} หน้า — Free Spire.PDF รองรับสูงสุด {1} หน้า โปรดตรวจว่า PDF ครบ\n",
                    overLimit.Count, OfdInfo.FreePageLimit));
            if (signed > 0)
                sb.Append("\nℹ " + signed + " ไฟล์มีลายเซ็นดิจิทัล — PDF ที่ได้เป็นสำเนาสำหรับอ่าน ลายเซ็นไม่ได้ถูกตรวจหรือคงไว้ ให้เก็บไฟล์ OFD ต้นฉบับไว้เป็นหลักฐาน\n");
            if (substituted.Count > 0)
                sb.Append("\nแทนฟอนต์ที่เครื่องไม่มีให้อัตโนมัติ: " + string.Join(", ", substituted) + "\n");
            if (unresolved.Count > 0)
            {
                sb.Append("\n⚠ ไม่มีฟอนต์และหาฟอนต์แทนไม่ได้: " + string.Join(", ", unresolved) +
                          "\nตัวอักษรส่วนนี้อาจเพี้ยน วิธีแก้: ติดตั้ง \"Chinese (Simplified) Supplemental Fonts\" " +
                          "(Settings → Apps → Optional features) หรือคัดลอกไฟล์ฟอนต์ไปไว้ในโฟลเดอร์ฟอนต์ แล้วแปลงใหม่\n" +
                          "\nเปิดโฟลเดอร์ฟอนต์เลยไหม?");
                if (MessageBox.Show(this, sb.ToString(), "ผลการแปลง", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    OpenFontsFolder();
                return;
            }
            MessageBox.Show(this, sb.ToString(), "ผลการแปลง", MessageBoxButtons.OK,
                failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // ---- row rendering ---------------------------------------------------------------

        private void Render(ListViewItem it)
        {
            var row = RowOf(it);
            var info = row.Info;
            var o = row.Outcome;

            it.Text = Path.GetFileName(row.FilePath);
            var pages = it.SubItems[1];
            var status = it.SubItems[2];
            var detail = it.SubItems[3];

            pages.Text = info == null ? "…" : !info.IsValid ? "–" : info.PageCount + (info.OverPageLimit ? " ⚠" : "");
            pages.ForeColor = info != null && info.OverPageLimit ? Amber : SystemColors.WindowText;

            string tip = row.FilePath;
            switch (row.State)
            {
                case State.Waiting:
                    status.Text = "○ รอแปลง";
                    status.ForeColor = SystemColors.GrayText;
                    if (info != null && !info.IsValid) { detail.Text = "⚠ " + info.Error; detail.ForeColor = Red; }
                    else
                    {
                        var hints = new List<string>();
                        if (info != null && info.OverPageLimit) hints.Add("⚠ เกิน " + OfdInfo.FreePageLimit + " หน้า อาจแปลงไม่ครบ");
                        if (info != null && info.HasSignatures) hints.Add("มีลายเซ็นดิจิทัล");
                        detail.Text = string.Join(" · ", hints);
                        detail.ForeColor = info != null && info.OverPageLimit ? Amber : SystemColors.GrayText;
                    }
                    break;
                case State.Converting:
                    status.Text = "◔ กำลังแปลง…";
                    status.ForeColor = Blue;
                    detail.Text = "";
                    break;
                case State.Done:
                    status.Text = "✓ สำเร็จ";
                    status.ForeColor = Green;
                    var bits = new List<string> { "→ " + Path.GetFileName(o.OutputPath) };
                    if (o.SubstitutedFonts.Count > 0)
                        bits.Add("แทนฟอนต์ " + string.Join(", ", o.SubstitutedFonts.Select(kv => kv.Key + "→" + kv.Value)));
                    if (o.MissingFonts.Count > 0) bits.Add("⚠ ไม่มีฟอนต์ " + string.Join(", ", o.MissingFonts));
                    if (info != null && info.OverPageLimit) bits.Add("⚠ ตรวจจำนวนหน้า");
                    if (info != null && info.HasSignatures) bits.Add("ลายเซ็นไม่ถูกคงไว้ใน PDF");
                    detail.Text = string.Join(" · ", bits);
                    detail.ForeColor = o.MissingFonts.Count > 0 || (info != null && info.OverPageLimit) ? Amber : SystemColors.WindowText;
                    tip += "\n→ " + o.OutputPath + (o.Warnings.Count > 0 ? "\n" + string.Join("\n", o.Warnings) : "") + "\n(ดับเบิลคลิกเพื่อเปิด)";
                    break;
                case State.Skipped:
                    status.Text = "↷ ข้าม";
                    status.ForeColor = SystemColors.GrayText;
                    detail.Text = "มี PDF อยู่แล้ว: " + Path.GetFileName(o.OutputPath);
                    detail.ForeColor = SystemColors.GrayText;
                    break;
                case State.Failed:
                    status.Text = "✗ ล้มเหลว";
                    status.ForeColor = Red;
                    detail.Text = o == null ? "" : o.Error;
                    detail.ForeColor = Red;
                    tip += "\n" + detail.Text;
                    break;
            }
            it.ToolTipText = tip;
        }

        private void UpdateUi(bool keepStatus = false)
        {
            bool any = list.Items.Count > 0;
            emptyState.Visible = !any;
            txtOut.Enabled = btnBrowse.Enabled = rbFolder.Checked && !busy;
            btnAdd.Enabled = btnAddFolder.Enabled = !busy;
            btnRemove.Enabled = !busy && list.SelectedItems.Count > 0;
            btnClear.Enabled = !busy && any;
            rbSame.Enabled = rbFolder.Enabled = cmbConflict.Enabled = chkOpenWhenDone.Enabled = !busy;
            btnConvert.Text = busy ? "■  หยุด" : "▶  แปลงเป็น PDF";
            btnConvert.BackColor = busy ? Red : Blue;
            btnConvert.FlatAppearance.MouseOverBackColor = busy ? Color.FromArgb(164, 38, 24) : BlueHover;
            btnConvert.Enabled = busy || any;
            if (!btnConvert.Enabled) btnConvert.BackColor = SystemColors.ControlDark;
            miConvertMenu.Text = busy ? "หยุด" : "แปลงเป็น PDF";
            if (!keepStatus && !busy)
                statusLabel.Text = any
                    ? list.Items.Count + " ไฟล์ในรายการ — กด \"แปลงเป็น PDF\" (F5)"
                    : "พร้อม — เพิ่มไฟล์ .ofd เพื่อเริ่ม";
        }

        // ---- actions ---------------------------------------------------------------------

        private void OpenSelectedPdf()
        {
            var sel = SelectedItems();
            if (sel.Count != 1) return;
            var row = RowOf(sel[0]);
            if (HasPdf(row)) Shell(row.Outcome.OutputPath);
        }

        private void RevealSelected()
        {
            var sel = SelectedItems();
            if (sel.Count != 1) return;
            var row = RowOf(sel[0]);
            Reveal(HasPdf(row) ? row.Outcome.OutputPath : row.FilePath);
        }

        private void CopySelectedDetails()
        {
            var lines = SelectedItems().Select(it =>
                it.Text + "\t" + it.SubItems[2].Text + "\t" + it.SubItems[3].Text + "\t" + RowOf(it).FilePath).ToList();
            if (lines.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                statusLabel.Text = "คัดลอกรายละเอียด " + lines.Count + " รายการแล้ว";
            }
            catch (ExternalException) { /* clipboard busy */ }
        }

        private void OpenFontsFolder()
        {
            try { Shell(AppPaths.FontDirForUser()); }
            catch (Exception ex) { Notify("เปิดโฟลเดอร์ฟอนต์ไม่ได้: " + ex.Message, MessageBoxIcon.Error); }
        }

        private void OpenLog()
        {
            if (File.Exists(AppPaths.LogFile)) Shell(AppPaths.LogFile);
            else Notify("ยังไม่มีบันทึกการทำงาน", MessageBoxIcon.Information);
        }

        private static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
            }
            catch (Exception ex) { Log.Write("reveal failed: " + ex.Message); }
        }

        private void Shell(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Notify("เปิดไม่ได้: " + ex.Message, MessageBoxIcon.Error); }
        }

        private void Notify(string message, MessageBoxIcon icon)
        {
            if (snapshotPath != null) { Log.Write("dialog suppressed: " + message); return; }
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
        }

        // ---- "Send to" integration ---------------------------------------------------------

        private static string SendToShortcut()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "แปลง OFD เป็น PDF.lnk");
        }

        private void ToggleSendTo()
        {
            string lnk = SendToShortcut();
            try
            {
                if (File.Exists(lnk))
                {
                    File.Delete(lnk);
                    Notify("ลบออกจากเมนู \"ส่งไปยัง\" แล้ว", MessageBoxIcon.Information);
                    return;
                }
                CreateShortcut(lnk, Application.ExecutablePath);
                Notify("เพิ่มแล้ว: คลิกขวาที่ไฟล์ .ofd (หรือโฟลเดอร์) → ส่งไปยัง (Send to) → \"แปลง OFD เป็น PDF\"\n\n" +
                       "ถ้าย้ายไฟล์โปรแกรมไปที่อื่น ให้เพิ่มเมนูนี้ใหม่อีกครั้ง", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Notify("ทำรายการไม่สำเร็จ: " + ex.Message, MessageBoxIcon.Error);
            }
        }

        private static void CreateShortcut(string lnkPath, string target)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object lnk = null;
            try
            {
                lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "แปลงไฟล์ OFD เป็น PDF" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                if (lnk != null) Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
            }
        }

        // ---- help ------------------------------------------------------------------------

        private void ShowHelp()
        {
            Notify(
                "1. เพิ่มไฟล์ .ofd: ลากมาวาง, กด \"เพิ่มไฟล์\" (Ctrl+O) หรือ \"เพิ่มโฟลเดอร์\" (Ctrl+Shift+O)\n" +
                "2. เลือกที่เก็บ PDF และวิธีจัดการไฟล์ชื่อซ้ำที่ด้านล่าง\n" +
                "3. กด \"แปลงเป็น PDF\" (F5)\n" +
                "4. ดับเบิลคลิกแถวที่สำเร็จเพื่อเปิด PDF, คลิกขวาเพื่อดูตัวเลือกเพิ่ม\n\n" +
                "ทางลัด: เครื่องมือ → เพิ่มในเมนู \"ส่งไปยัง\" แล้วคลิกขวาที่ไฟล์ .ofd ใน Explorer ได้เลย\n\n" +
                "ตัวอักษรเพี้ยน: โปรแกรมแทนฟอนต์จีนที่เครื่องไม่มีให้อัตโนมัติ ถ้าต้องการหน้าตาตรงต้นฉบับ " +
                "ให้ติดตั้ง \"Chinese (Simplified) Supplemental Fonts\" หรือคัดลอกไฟล์ฟอนต์ไปไว้ใน เครื่องมือ → เปิดโฟลเดอร์ฟอนต์เพิ่มเติม\n\n" +
                "ข้อจำกัด: แปลงได้ไม่เกิน " + OfdInfo.FreePageLimit + " หน้าต่อไฟล์ (Free Spire.PDF)",
                MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            Notify(
                "OFD → PDF Converter " + VersionText() + "\n\n" +
                "เครื่องมือแปลง: Free Spire.PDF (สูงสุด " + OfdInfo.FreePageLimit + " หน้าต่อไฟล์)\n\n" +
                "การตั้งค่า ฟอนต์เพิ่มเติม และบันทึกการทำงาน เก็บไว้ในเครื่องที่:\n" + AppPaths.DataDir,
                MessageBoxIcon.Information);
        }

        private static string VersionText()
        {
            var v = typeof(MainForm).Assembly.GetName().Version;
            return v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : "");
        }

        // ---- lifecycle -------------------------------------------------------------------

        private void OnShown(object sender, EventArgs e)
        {
            FitLastColumn();
            if (autoStart && list.Items.Count > 0) OnConvertClicked();
            else if (snapshotPath != null) SaveSnapshotAndExit();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (busy)
            {
                if (snapshotPath == null && MessageBox.Show(this, "กำลังแปลงไฟล์อยู่ ต้องการหยุดและปิดโปรแกรมไหม?\n(ไฟล์ที่กำลังแปลงจะเสร็จก่อนแล้วจึงปิด)",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                closeWhenDone = true;
                cts.Cancel();
                e.Cancel = true;
                statusLabel.Text = "กำลังหยุด… โปรแกรมจะปิดเมื่อไฟล์ปัจจุบันเสร็จ";
                return;
            }
            SaveSettings();
        }

        /// <summary>CI only: render the window to a PNG so the layout can be checked without a screen.</summary>
        private void SaveSnapshotAndExit()
        {
            try
            {
                FitLastColumn();
                Refresh();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
                    bmp.Save(snapshotPath, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Log.Write("snapshot failed: " + ex);
            }
            BeginInvoke(new Action(Close));
        }
    }
}
