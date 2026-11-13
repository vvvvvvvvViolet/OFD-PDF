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

        /// <summary>Everything we know about one file in the list.</summary>
        private sealed class Row
        {
            public string FilePath;
            public State State;
            public OfdInfo Info;            // null until the background scan finishes
            public ConvertOutcome Outcome;  // last conversion result
        }

        /// <summary>An entry of the "save to" drop-down.</summary>
        private sealed class Destination
        {
            public string Label;
            public string Folder;   // null = next to the source file
            public bool Browse;     // "choose a folder…"
            public override string ToString() { return Label; }
        }

        // Header
        private readonly ModernButton btnSettings = new ModernButton { Kind = ButtonKind.Subtle, IconGlyph = Theme.GlyphSettings };
        private readonly ModernButton btnHelp = new ModernButton { Kind = ButtonKind.Subtle, IconGlyph = Theme.GlyphHelp };
        // Files
        private readonly DropZone drop = new DropZone();
        private readonly Panel dropGap = new Panel();
        private readonly Card listCard = new Card();
        private readonly Label lblListTitle = new Label();
        private readonly ModernButton btnRemove = new ModernButton { Kind = ButtonKind.Subtle, Text = "ลบที่เลือก" };
        private readonly ModernButton btnClear = new ModernButton { Kind = ButtonKind.Subtle, Text = "ล้างรายการ" };
        private readonly FileList list = new FileList();
        // Preview
        private readonly Card previewCard = new Card();
        private readonly Label lblPreviewName = new Label();
        private readonly Label lblPreviewNote = new Label();
        private readonly PictureBox picture = new PictureBox();
        private readonly Label lblPreviewEmpty = new Label();
        private readonly ModernButton btnPrev = new ModernButton { Kind = ButtonKind.Subtle, IconGlyph = Theme.GlyphPrev };
        private readonly ModernButton btnNext = new ModernButton { Kind = ButtonKind.Subtle, IconGlyph = Theme.GlyphNext };
        private readonly Label lblPage = new Label();
        private readonly ModernButton btnOpenPdf = new ModernButton { Text = "เปิด PDF", IconGlyph = Theme.GlyphOpen };
        // Footer
        private readonly ComboBox cmbDest = new ComboBox();
        private readonly ComboBox cmbConflict = new ComboBox();
        private readonly ModernButton btnConvert = new ModernButton { Kind = ButtonKind.Accent };
        private readonly ThinProgress progress = new ThinProgress();
        private readonly Label statusLabel = new Label();
        // Menus
        private readonly ContextMenuStrip settingsMenu = MenuColors.NewMenu();
        private readonly ContextMenuStrip helpMenu = MenuColors.NewMenu();
        private readonly ContextMenuStrip rowMenu = MenuColors.NewMenu();
        private ToolStripMenuItem miOpenWhenDone, miSendTo;
        private TableLayoutPanel footerTable;

        private readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly AppSettings settings;
        private readonly bool autoStart;
        private readonly string snapshotPath;
        private readonly float k; // DPI scale
        private CancellationTokenSource cts;
        private bool busy, closeWhenDone, suppressDestEvents;
        private int destIndex;
        private Row previewRow;
        private int previewPage, previewPages, previewRequest;

        public MainForm(IEnumerable<string> initialFiles, bool autoStart, string snapshotPath)
        {
            this.autoStart = autoStart;
            this.snapshotPath = snapshotPath;
            // A CI snapshot must not depend on (or change) the user's saved settings.
            settings = snapshotPath == null ? AppSettings.Load() : new AppSettings();

            k = DeviceDpi / 96f;
            AutoScaleMode = AutoScaleMode.None; // fonts are in points already; pixel sizes use k
            Font = Theme.Body;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Text = "OFD → PDF Converter";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            MinimumSize = Px(900, 600);
            Size = settings.WindowWidth > 0
                ? new Size(Math.Max(settings.WindowWidth, MinimumSize.Width), Math.Max(settings.WindowHeight, MinimumSize.Height))
                : Px(1060, 700);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            SuspendLayout();
            BuildMenus();
            BuildLayout();
            ApplySettings();
            ResumeLayout(true);

            AddFiles(initialFiles ?? Enumerable.Empty<string>());
            UpdateUi();
            ShowPreviewPlaceholder();

            Shown += OnShown;
            FormClosing += OnFormClosing;
            DragEnter += (s, e) => e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null && !busy) AddFiles(f); };
        }

        private int Px(int v) { return (int)Math.Round(v * k); }
        private Size Px(int w, int h) { return new Size(Px(w), Px(h)); }

        // ---- layout ----------------------------------------------------------------------

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(Px(16), Px(8), Px(16), Px(16)),
                BackColor = Theme.Window,
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildBody(), 0, 1);
            var footer = BuildFooter();
            root.Controls.Add(footer, 0, 2);
            Controls.Add(root);
            // Size the footer row from its content (Panel.AutoSize is unreliable with docked children).
            root.RowStyles[2] = new RowStyle(SizeType.Absolute,
                footerTable.GetPreferredSize(new Size(Px(900), 0)).Height + footer.Padding.Vertical + footer.Margin.Vertical + Px(2));
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, Px(10)) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var logo = new PictureBox { Size = Px(36, 36), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, Px(10), 0) };
            try { logo.Image = new Icon(Icon, Px(48, 48)).ToBitmap(); } catch (Exception) { }

            var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
            titles.Controls.Add(new Label { Text = "OFD → PDF Converter", Font = Theme.Title, AutoSize = true, Margin = Padding.Empty });
            titles.Controls.Add(new Label { Text = "แปลงไฟล์ OFD เป็น PDF · เวอร์ชัน " + VersionText(), Font = Theme.Caption, ForeColor = Theme.TextSecondary, AutoSize = true, Margin = Padding.Empty });

            var tips = new ToolTip();
            tips.SetToolTip(btnSettings, "ตั้งค่าและเครื่องมือ");
            tips.SetToolTip(btnHelp, "วิธีใช้ (F1)");
            btnSettings.Anchor = btnHelp.Anchor = AnchorStyles.Right;
            btnSettings.Padding = btnHelp.Padding = new Padding(Px(8));
            btnSettings.Click += (s, e) => settingsMenu.Show(btnSettings, new Point(btnSettings.Width - settingsMenu.Width, btnSettings.Height));
            btnHelp.Click += (s, e) => helpMenu.Show(btnHelp, new Point(btnHelp.Width - helpMenu.Width, btnHelp.Height));

            header.Controls.Add(logo, 0, 0);
            header.Controls.Add(titles, 1, 0);
            header.Controls.Add(btnSettings, 2, 0);
            header.Controls.Add(btnHelp, 3, 0);
            return header;
        }

        private Control BuildBody()
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // Left: drop zone + file list.
            var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Px(6), 0) };
            drop.Dock = DockStyle.Fill;
            drop.CanDrop = () => !busy;
            drop.FilesDropped += files => AddFiles(files);
            drop.BtnFiles.Padding = drop.BtnFolder.Padding = new Padding(Px(14), Px(6), Px(14), Px(6));
            drop.BtnFiles.Click += (s, e) => AddFilesDialog();
            drop.BtnFolder.Click += (s, e) => AddFolderDialog();
            dropGap.Dock = DockStyle.Top;
            dropGap.Height = Px(10);

            listCard.Dock = DockStyle.Fill;
            listCard.Padding = new Padding(Px(6), Px(8), Px(6), Px(8));
            var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Theme.Card };
            listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            lblListTitle.Font = Theme.Subheading;
            lblListTitle.AutoSize = true;
            lblListTitle.Anchor = AnchorStyles.Left;
            lblListTitle.Margin = new Padding(Px(10), 0, 0, 0);
            btnRemove.Padding = btnClear.Padding = new Padding(Px(10), Px(4), Px(10), Px(4));
            btnRemove.Click += (s, e) => RemoveSelected();
            btnClear.Click += (s, e) => ClearList();
            list.Dock = DockStyle.Fill;
            list.Margin = new Padding(0, Px(4), 0, 0);
            list.Describe = o => Describe((Row)o);
            list.AllowDrop = true;
            list.DragEnter += (s, e) => e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            list.DragDrop += (s, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null && !busy) AddFiles(f); };
            list.SelectedIndexChanged += (s, e) => { UpdateUi(keepStatus: true); ShowPreview(); };
            list.DoubleClick += (s, e) => OpenSelectedPdf();
            list.KeyDown += OnListKeyDown;
            list.ContextMenuStrip = rowMenu;
            list.MoreClicked += (i, p) => rowMenu.Show(list, p);
            listLayout.Controls.Add(lblListTitle, 0, 0);
            listLayout.Controls.Add(btnRemove, 1, 0);
            listLayout.Controls.Add(btnClear, 2, 0);
            listLayout.Controls.Add(list, 0, 1);
            listLayout.SetColumnSpan(list, 3);
            listCard.Controls.Add(listLayout);

            // Docking: last added is laid out first.
            left.Controls.Add(listCard);
            left.Controls.Add(dropGap);
            left.Controls.Add(drop);

            // Right: preview.
            previewCard.Dock = DockStyle.Fill;
            previewCard.Margin = new Padding(Px(6), 0, 0, 0);
            previewCard.Padding = new Padding(Px(14), Px(12), Px(14), Px(12));
            var pv = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 4, BackColor = Theme.Card };
            pv.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pv.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pv.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pv.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pv.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pv.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pv.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            pv.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lblPreviewTitle = new Label { Text = "ตัวอย่าง PDF", Font = Theme.Subheading, AutoSize = true, Margin = Padding.Empty };
            lblPreviewName.AutoSize = false;
            lblPreviewName.Dock = DockStyle.Fill;
            lblPreviewName.Height = Px(22);
            lblPreviewName.AutoEllipsis = true;
            lblPreviewName.ForeColor = Theme.TextSecondary;
            lblPreviewName.Font = Theme.Caption;
            lblPreviewName.Margin = new Padding(0, Px(2), 0, 0);
            lblPreviewNote.AutoSize = false;
            lblPreviewNote.Dock = DockStyle.Fill;
            lblPreviewNote.Height = Px(20);
            lblPreviewNote.AutoEllipsis = true;
            lblPreviewNote.Font = Theme.Caption;
            lblPreviewNote.ForeColor = Theme.Warning;
            lblPreviewNote.Margin = Padding.Empty;

            var stage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Subtle, Margin = new Padding(0, Px(8), 0, Px(8)), Padding = new Padding(Px(8)) };
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.BackColor = Theme.Subtle;
            picture.Cursor = Cursors.Hand;
            picture.DoubleClick += (s, e) => OpenSelectedPdf();
            lblPreviewEmpty.Dock = DockStyle.Fill;
            lblPreviewEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblPreviewEmpty.ForeColor = Theme.TextSecondary;
            lblPreviewEmpty.BackColor = Theme.Subtle;
            stage.Controls.Add(picture);
            stage.Controls.Add(lblPreviewEmpty);

            btnPrev.Padding = btnNext.Padding = new Padding(Px(6));
            btnPrev.Click += (s, e) => { if (previewPage > 0) { previewPage--; ShowPreview(); } };
            btnNext.Click += (s, e) => { if (previewPage < previewPages - 1) { previewPage++; ShowPreview(); } };
            lblPage.AutoSize = true;
            lblPage.Anchor = AnchorStyles.Left;
            lblPage.ForeColor = Theme.TextSecondary;
            lblPage.Margin = new Padding(Px(4), 0, Px(4), 0);
            btnOpenPdf.Padding = new Padding(Px(12), Px(4), Px(12), Px(4));
            btnOpenPdf.Anchor = AnchorStyles.Right;
            btnOpenPdf.Click += (s, e) => OpenSelectedPdf();

            pv.Controls.Add(lblPreviewTitle, 0, 0);
            pv.SetColumnSpan(lblPreviewTitle, 5);
            pv.Controls.Add(lblPreviewName, 0, 1);
            pv.SetColumnSpan(lblPreviewName, 5);
            var notes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, Margin = Padding.Empty };
            notes.Controls.Add(lblPreviewNote, 0, 0);
            notes.Controls.Add(stage, 0, 1);
            notes.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            notes.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            notes.AutoSize = false;
            pv.Controls.Add(notes, 0, 2);
            pv.SetColumnSpan(notes, 5);
            pv.Controls.Add(btnPrev, 0, 3);
            pv.Controls.Add(lblPage, 1, 3);
            pv.Controls.Add(btnNext, 2, 3);
            pv.Controls.Add(btnOpenPdf, 4, 3);
            previewCard.Controls.Add(pv);

            body.Controls.Add(left, 0, 0);
            body.Controls.Add(previewCard, 1, 0);
            return body;
        }

        private Control BuildFooter()
        {
            var footer = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, Px(12), 0, 0), Padding = new Padding(Px(16), Px(12), Px(16), Px(12)) };
            var t = footerTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, RowCount = 3, BackColor = Theme.Card };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(10))); // progress row is always reserved
            for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lblDest = new Label { Text = "บันทึก PDF ที่", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, Px(6), 0) };
            cmbDest.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDest.FlatStyle = FlatStyle.System;
            cmbDest.Width = Px(260);
            cmbDest.Anchor = AnchorStyles.Left;
            cmbDest.Margin = new Padding(0, 0, Px(18), 0);
            cmbDest.SelectedIndexChanged += OnDestinationChanged;
            var lblConflict = new Label { Text = "ถ้ามีไฟล์ชื่อเดียวกัน", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, Px(6), 0) };
            cmbConflict.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbConflict.FlatStyle = FlatStyle.System;
            cmbConflict.Items.AddRange(new object[] { "สร้างชื่อใหม่ (file (1).pdf)", "เขียนทับไฟล์เดิม", "ข้ามไฟล์นั้น" });
            cmbConflict.Width = Px(200);
            cmbConflict.Anchor = AnchorStyles.Left;

            btnConvert.Font = new Font("Segoe UI Semibold", 11f);
            btnConvert.Padding = new Padding(Px(22), Px(8), Px(22), Px(8));
            btnConvert.MinimumSize = new Size(Px(170), 0);
            btnConvert.Anchor = AnchorStyles.Right;
            btnConvert.Click += (s, e) => OnConvertClicked();

            statusLabel.AutoSize = false;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Height = Px(22);
            statusLabel.AutoEllipsis = true;
            statusLabel.ForeColor = Theme.TextSecondary;
            statusLabel.Font = Theme.Caption;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Margin = new Padding(0, Px(8), 0, 0);
            progress.Dock = DockStyle.Fill;
            progress.Height = Px(4);
            progress.Margin = new Padding(0, Px(4), Px(18), 0);
            progress.Visible = false;

            t.Controls.Add(lblDest, 0, 0);
            t.Controls.Add(cmbDest, 1, 0);
            t.Controls.Add(lblConflict, 2, 0);
            t.Controls.Add(cmbConflict, 3, 0);
            t.Controls.Add(btnConvert, 5, 0);
            t.SetRowSpan(btnConvert, 3);
            t.Controls.Add(statusLabel, 0, 1);
            t.SetColumnSpan(statusLabel, 5);
            t.Controls.Add(progress, 0, 2);
            t.SetColumnSpan(progress, 5);
            footer.Controls.Add(t);
            return footer;
        }

        private void BuildMenus()
        {
            miOpenWhenDone = new ToolStripMenuItem("เปิดโฟลเดอร์ผลลัพธ์เมื่อแปลงเสร็จ") { CheckOnClick = true };
            miSendTo = new ToolStripMenuItem("เพิ่มในเมนู \"ส่งไปยัง\" (Send to) ของ Windows", null, (s, e) => ToggleSendTo());
            settingsMenu.Items.AddRange(new ToolStripItem[]
            {
                miOpenWhenDone,
                new ToolStripSeparator(),
                new ToolStripMenuItem("เปิดโฟลเดอร์ฟอนต์เพิ่มเติม", null, (s, e) => OpenFontsFolder()),
                new ToolStripMenuItem("เปิดบันทึกการทำงาน (log)", null, (s, e) => OpenLog()),
                new ToolStripSeparator(),
                miSendTo,
            });
            settingsMenu.Opening += (s, e) => miSendTo.Checked = File.Exists(SendToShortcut());

            helpMenu.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripMenuItem("วิธีใช้", null, (s, e) => ShowHelp()) { ShortcutKeyDisplayString = "F1" },
                new ToolStripMenuItem("ทางลัดแป้นพิมพ์", null, (s, e) => ShowShortcuts()),
                new ToolStripSeparator(),
                new ToolStripMenuItem("เกี่ยวกับโปรแกรม", null, (s, e) => ShowAbout()),
            });

            var open = new ToolStripMenuItem("เปิด PDF", null, (s, e) => OpenSelectedPdf()) { ShortcutKeyDisplayString = "Enter" };
            var reveal = new ToolStripMenuItem("แสดงในโฟลเดอร์", null, (s, e) => RevealSelected());
            var again = new ToolStripMenuItem("แปลงไฟล์ที่เลือก", null, (s, e) => StartConvert(SelectedRows()));
            var copy = new ToolStripMenuItem("คัดลอกรายละเอียด", null, (s, e) => CopySelectedDetails()) { ShortcutKeyDisplayString = "Ctrl+C" };
            var remove = new ToolStripMenuItem("ลบออกจากรายการ", null, (s, e) => RemoveSelected()) { ShortcutKeyDisplayString = "Del" };
            rowMenu.Items.AddRange(new ToolStripItem[] { open, reveal, new ToolStripSeparator(), again, copy, new ToolStripSeparator(), remove });
            rowMenu.Opening += (s, e) =>
            {
                var rows = SelectedRows();
                if (rows.Count == 0) { e.Cancel = true; return; }
                open.Enabled = rows.Count == 1 && HasPdf(rows[0]);
                reveal.Enabled = rows.Count == 1;
                again.Enabled = remove.Enabled = !busy;
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.O: AddFilesDialog(); return true;
                case Keys.Control | Keys.Shift | Keys.O: AddFolderDialog(); return true;
                case Keys.F5: OnConvertClicked(); return true;
                case Keys.F1: ShowHelp(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- settings --------------------------------------------------------------------

        private void ApplySettings()
        {
            FillDestinations(settings.OutputDir, settings.SaveNextToSource ? null : settings.OutputDir);
            cmbConflict.SelectedIndex = (int)settings.Conflict;
            miOpenWhenDone.Checked = settings.OpenFolderWhenDone;
        }

        private void FillDestinations(string folder, string select)
        {
            suppressDestEvents = true;
            cmbDest.Items.Clear();
            cmbDest.Items.Add(new Destination { Label = "โฟลเดอร์เดียวกับไฟล์ต้นฉบับ" });
            if (!string.IsNullOrEmpty(folder)) cmbDest.Items.Add(new Destination { Label = folder, Folder = folder });
            cmbDest.Items.Add(new Destination { Label = "เลือกโฟลเดอร์อื่น…", Browse = true });
            cmbDest.SelectedIndex = !string.IsNullOrEmpty(select) && !string.IsNullOrEmpty(folder) ? 1 : 0;
            destIndex = cmbDest.SelectedIndex;
            suppressDestEvents = false;
        }

        private void OnDestinationChanged(object sender, EventArgs e)
        {
            if (suppressDestEvents) return;
            var d = cmbDest.SelectedItem as Destination;
            if (d == null || !d.Browse)
            {
                destIndex = cmbDest.SelectedIndex;
                return;
            }
            using (var dlg = new FolderBrowserDialog { Description = "เลือกโฟลเดอร์สำหรับเก็บไฟล์ PDF" })
            {
                var current = CurrentFolder();
                if (current != null && Directory.Exists(current)) dlg.SelectedPath = current;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    FillDestinations(dlg.SelectedPath, dlg.SelectedPath);
                else
                {
                    suppressDestEvents = true;
                    cmbDest.SelectedIndex = destIndex;
                    suppressDestEvents = false;
                }
            }
        }

        /// <summary>The chosen output folder, or null for "next to the source file".</summary>
        private string CurrentFolder()
        {
            var d = cmbDest.SelectedItem as Destination;
            return d == null ? null : d.Folder;
        }

        private void SaveSettings()
        {
            if (snapshotPath != null) return;
            string folder = cmbDest.Items.OfType<Destination>().Select(d => d.Folder).FirstOrDefault(f => f != null);
            settings.SaveNextToSource = CurrentFolder() == null;
            settings.OutputDir = folder ?? "";
            settings.Conflict = (ConflictMode)Math.Max(0, cmbConflict.SelectedIndex);
            settings.OpenFolderWhenDone = miOpenWhenDone.Checked;
            if (WindowState == FormWindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
            settings.Save();
        }

        // ---- file list -------------------------------------------------------------------

        private void AddFiles(IEnumerable<string> paths)
        {
            var added = new List<Row>();
            int ignored = 0;
            list.BeginUpdate();
            foreach (string full in Program.ExpandInputs(paths))
            {
                if (!full.EndsWith(".ofd", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { ignored++; continue; }
                if (!known.Add(full)) continue;
                var row = new Row { FilePath = full };
                list.Items.Add(row);
                added.Add(row);
            }
            list.EndUpdate();
            UpdateUi();
            if (ignored > 0 && added.Count == 0)
                SetStatus("ไม่มีไฟล์ .ofd ในสิ่งที่เพิ่มเข้ามา");
            else if (added.Count > 0)
                SetStatus(string.Format("เพิ่ม {0} ไฟล์ — กด \"แปลง\" เมื่อพร้อม (F5)", added.Count));
            ScanAsync(added);
        }

        /// <summary>Reads page count / signatures / validity in the background so the list can warn early.</summary>
        private async void ScanAsync(List<Row> rows)
        {
            foreach (var row in rows)
            {
                string path = row.FilePath;
                var info = await Task.Run(() => OfdInfo.Inspect(path));
                if (row.Info == null) row.Info = info; // a finished conversion may already have set it
                RefreshRow(row);
            }
        }

        private void RefreshRow(Row row)
        {
            list.RefreshItem(row);
            if (row == previewRow) ShowPreview();
            UpdateUi(keepStatus: true);
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
            var rows = SelectedRows();
            if (rows.Count == 0) return;
            list.BeginUpdate();
            foreach (var row in rows)
            {
                known.Remove(row.FilePath);
                list.Items.Remove(row);
            }
            list.EndUpdate();
            UpdateUi();
            ShowPreview();
        }

        private void ClearList()
        {
            if (busy || list.Items.Count == 0) return;
            known.Clear();
            list.Items.Clear();
            UpdateUi();
            ShowPreview();
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { OpenSelectedPdf(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                list.BeginUpdate();
                for (int i = 0; i < list.Items.Count; i++) list.SetSelected(i, true);
                list.EndUpdate();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C) { CopySelectedDetails(); e.Handled = true; }
        }

        private List<Row> SelectedRows()
        {
            return list.SelectedItems.Cast<Row>().ToList();
        }

        private List<Row> AllRows()
        {
            return list.Items.Cast<Row>().ToList();
        }

        private static bool HasPdf(Row row)
        {
            return row.State == State.Done && row.Outcome != null && File.Exists(row.Outcome.OutputPath);
        }

        private List<Row> PendingRows()
        {
            return AllRows().Where(r => r.State != State.Done && r.State != State.Skipped).ToList();
        }

        // ---- conversion ------------------------------------------------------------------

        private void OnConvertClicked()
        {
            if (busy)
            {
                cts.Cancel();
                btnConvert.Enabled = false;
                SetStatus("กำลังหยุด… รอให้ไฟล์ปัจจุบันเสร็จก่อน");
                return;
            }
            if (list.Items.Count == 0) { AddFilesDialog(); return; }
            var todo = PendingRows();
            if (todo.Count == 0)
            {
                Notify("ทุกไฟล์ในรายการแปลงเสร็จแล้ว\nถ้าต้องการแปลงซ้ำ ให้คลิกขวาที่ไฟล์แล้วเลือก \"แปลงไฟล์ที่เลือก\"", MessageBoxIcon.Information);
                return;
            }
            StartConvert(todo);
        }

        private string ResolveOutputDir(out bool ok)
        {
            ok = true;
            string dir = CurrentFolder();
            if (dir == null) return null;
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

        private async void StartConvert(List<Row> rows)
        {
            if (busy || rows.Count == 0) return;
            bool ok;
            string outDir = ResolveOutputDir(out ok);
            if (!ok) return;
            SaveSettings();
            var mode = (ConflictMode)Math.Max(0, cmbConflict.SelectedIndex);

            cts = new CancellationTokenSource();
            var token = cts.Token;
            busy = true;
            progress.Maximum = rows.Count;
            progress.Value = 0;
            progress.Visible = true;
            UpdateUi();
            SetStatus("กำลังเตรียม…");

            await Task.Run(() => Converter.RefreshFonts()); // pick up fonts added since last time
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = new List<Row>();
            int index = 0;
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested || !list.Items.Contains(row)) continue;
                row.State = State.Converting;
                RefreshRow(row);
                int i = list.Items.IndexOf(row);
                if (i >= 0) list.TopIndex = Math.Min(list.TopIndex, i);
                index++;
                SetStatus(string.Format("กำลังแปลง {0}/{1}: {2}", index, rows.Count, Path.GetFileName(row.FilePath)));

                string path = row.FilePath;
                var r = await Task.Run(() => Converter.Convert(path, outDir, mode, claimed));
                row.Outcome = r;
                if (r.Info != null) row.Info = r.Info;
                row.State = r.Kind == OutcomeKind.Converted ? State.Done : r.Kind == OutcomeKind.Skipped ? State.Skipped : State.Failed;
                done.Add(row);
                progress.Value = done.Count;
                RefreshRow(row);
            }

            busy = false;
            progress.Visible = false;
            cts.Dispose();
            cts = null;
            bool cancelled = token.IsCancellationRequested;
            UpdateUi(keepStatus: true);

            // Show the first result straight away so the user can check it.
            if (list.SelectedItems.Count == 0)
            {
                var first = done.FirstOrDefault(r => r.State == State.Done);
                if (first != null) list.SetSelected(list.Items.IndexOf(first), true);
            }
            Summarize(done, cancelled, rows.Count);

            if (closeWhenDone) { Close(); return; }
            if (snapshotPath != null)
            {
                await ShowPreviewAsync();
                SaveSnapshotAndExit();
            }
        }

        private void Summarize(List<Row> rows, bool cancelled, int requested)
        {
            int converted = rows.Count(r => r.State == State.Done && !r.Outcome.Truncated);
            var partial = rows.Where(r => r.State == State.Done && r.Outcome.Truncated).ToList();
            int skipped = rows.Count(r => r.State == State.Skipped);
            var failed = rows.Where(r => r.State == State.Failed).ToList();

            var parts = new List<string> { "แปลงสำเร็จ " + converted + " ไฟล์" };
            if (partial.Count > 0) parts.Add("ได้ไม่ครบ " + partial.Count);
            if (skipped > 0) parts.Add("ข้าม " + skipped);
            if (failed.Count > 0) parts.Add("ล้มเหลว " + failed.Count);
            if (cancelled) parts.Add("หยุดกลางคัน (ยังไม่ได้แปลง " + (requested - rows.Count) + " ไฟล์)");
            SetStatus(string.Join(" · ", parts));

            var outcomes = rows.Where(r => r.Outcome != null).Select(r => r.Outcome).ToList();
            var unresolved = outcomes.SelectMany(o => o.MissingFonts).Distinct().ToList();
            int signed = rows.Count(r => r.State == State.Done && r.Info != null && r.Info.HasSignatures);
            var substituted = outcomes.SelectMany(o => o.SubstitutedFonts).Select(kv => kv.Key + " → " + kv.Value).Distinct().ToList();

            if (miOpenWhenDone.Checked && converted + partial.Count > 0 && snapshotPath == null)
                Reveal(rows.First(r => r.State == State.Done).Outcome.OutputPath);

            bool nothingToSay = failed.Count == 0 && unresolved.Count == 0 && partial.Count == 0 && signed == 0 && !cancelled;
            if (nothingToSay || snapshotPath != null) return;

            var sb = new StringBuilder(statusLabel.Text + "\n");
            if (failed.Count > 0)
            {
                sb.Append("\nไฟล์ที่ล้มเหลว:\n");
                foreach (var r in failed.Take(8))
                    sb.Append("  • " + Path.GetFileName(r.FilePath) + ": " + (r.Outcome == null ? "" : r.Outcome.Error) + "\n");
                if (failed.Count > 8) sb.Append("  … และอีก " + (failed.Count - 8) + " ไฟล์ (ดูในรายการ)\n");
            }
            if (partial.Count > 0)
            {
                sb.Append(string.Format("\n⚠ PDF ไม่ครบทุกหน้า — Free Spire.PDF แปลงไฟล์ OFD ได้เฉพาะ {0} หน้าแรก:\n", OfdInfo.FreePageLimit));
                foreach (var r in partial.Take(8))
                    sb.Append(string.Format("  • {0}: ได้ {1} จาก {2} หน้า\n", Path.GetFileName(r.FilePath), r.Outcome.PdfPages, r.Info.PageCount));
            }
            if (signed > 0)
                sb.Append("\nℹ " + signed + " ไฟล์มีลายเซ็นดิจิทัล — PDF ที่ได้เป็นสำเนาสำหรับอ่าน ลายเซ็นไม่ได้ถูกตรวจหรือคงไว้ ให้เก็บไฟล์ OFD ต้นฉบับไว้เป็นหลักฐาน\n");
            if (substituted.Count > 0)
                sb.Append("\nแทนฟอนต์ที่เครื่องไม่มีให้อัตโนมัติ: " + string.Join(", ", substituted) + " — ดูตัวอย่างทางขวาเพื่อตรวจตัวอักษร\n");
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
                failed.Count > 0 || partial.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // ---- row presentation --------------------------------------------------------------

        private RowView Describe(Row row)
        {
            var info = row.Info;
            var o = row.Outcome;
            var v = new RowView { Title = Path.GetFileName(row.FilePath), Tooltip = row.FilePath };
            var parts = new List<string>();
            if (info == null) parts.Add("กำลังตรวจไฟล์…");
            else if (info.IsValid) parts.Add(info.PageCount + " หน้า");

            switch (row.State)
            {
                case State.Waiting:
                    if (info != null && !info.IsValid)
                    {
                        Badge(v, Theme.GlyphCancel, "✕", Theme.Error, "ไฟล์เสีย", Theme.Error);
                        parts.Add(info.Error);
                        v.SubtitleColor = Theme.Error;
                    }
                    else if (info != null && info.OverPageLimit)
                    {
                        Badge(v, Theme.GlyphWarning, "!", Theme.WarningFill, "รอแปลง", Theme.TextSecondary);
                        parts.Add("รุ่นฟรีแปลงได้แค่ " + OfdInfo.FreePageLimit + " หน้าแรก");
                        v.SubtitleColor = Theme.Warning;
                    }
                    else
                    {
                        v.Hollow = true;
                        v.Fill = Theme.Neutral;
                        v.Status = "รอแปลง";
                    }
                    if (info != null && info.HasSignatures) parts.Add("มีลายเซ็นดิจิทัล");
                    break;
                case State.Converting:
                    Badge(v, Theme.GlyphSync, "…", Theme.Accent, "กำลังแปลง…", Theme.Accent);
                    break;
                case State.Done:
                    bool warn = o.Truncated || o.MissingFonts.Count > 0;
                    Badge(v, warn ? Theme.GlyphWarning : Theme.GlyphCheck, warn ? "!" : "✓", warn ? Theme.WarningFill : Theme.Success,
                        o.Truncated ? "ไม่ครบ" : "สำเร็จ", warn ? Theme.Warning : Theme.Success);
                    parts.Add("→ " + Path.GetFileName(o.OutputPath));
                    if (o.Truncated && info != null) parts.Add(string.Format("ได้ {0} จาก {1} หน้า", o.PdfPages, info.PageCount));
                    if (o.MissingFonts.Count > 0) parts.Add("ไม่มีฟอนต์ " + string.Join(", ", o.MissingFonts));
                    if (o.SubstitutedFonts.Count > 0) parts.Add("แทนฟอนต์ " + string.Join(", ", o.SubstitutedFonts.Select(kv => kv.Key + "→" + kv.Value)));
                    if (warn) v.SubtitleColor = Theme.Warning;
                    v.Tooltip += "\n→ " + o.OutputPath + (o.Warnings.Count > 0 ? "\n" + string.Join("\n", o.Warnings) : "") + "\nดับเบิลคลิกเพื่อเปิด PDF";
                    break;
                case State.Skipped:
                    Badge(v, Theme.GlyphSkip, "–", Theme.Neutral, "ข้าม", Theme.TextSecondary);
                    parts.Add("มี PDF อยู่แล้ว: " + Path.GetFileName(o.OutputPath));
                    break;
                case State.Failed:
                    Badge(v, Theme.GlyphCancel, "✕", Theme.Error, "ล้มเหลว", Theme.Error);
                    if (o != null) parts.Add(o.Error);
                    v.SubtitleColor = Theme.Error;
                    if (o != null) v.Tooltip += "\n" + o.Error;
                    break;
            }
            v.Subtitle = string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
            return v;
        }

        private static void Badge(RowView v, string glyph, string fallback, Color fill, string status, Color statusColor)
        {
            v.Glyph = Theme.Glyph(glyph, fallback);
            v.Fill = fill;
            v.Status = status;
            v.StatusColor = statusColor;
        }

        private void UpdateUi(bool keepStatus = false)
        {
            bool any = list.Items.Count > 0;
            drop.Compact = any;
            drop.Dock = any ? DockStyle.Top : DockStyle.Fill;
            drop.Height = Px(68);
            dropGap.Visible = listCard.Visible = any;
            drop.BtnFiles.Enabled = drop.BtnFolder.Enabled = !busy;

            var rows = AllRows();
            int pending = rows.Count(r => r.State != State.Done && r.State != State.Skipped);
            int okCount = rows.Count(r => r.State == State.Done);
            int failCount = rows.Count(r => r.State == State.Failed);
            var title = new StringBuilder("ไฟล์ (" + rows.Count + ")");
            if (okCount > 0) title.Append("  ·  แปลงแล้ว " + okCount);
            if (failCount > 0) title.Append("  ·  ล้มเหลว " + failCount);
            lblListTitle.Text = title.ToString();

            btnRemove.Enabled = !busy && list.SelectedItems.Count > 0;
            btnClear.Enabled = !busy && any;
            cmbDest.Enabled = cmbConflict.Enabled = !busy;

            if (busy)
            {
                btnConvert.Kind = ButtonKind.Danger;
                btnConvert.IconGlyph = Theme.GlyphStop;
                btnConvert.Text = "หยุด";
            }
            else
            {
                btnConvert.Kind = ButtonKind.Accent;
                btnConvert.IconGlyph = Theme.GlyphPlay;
                btnConvert.Text = pending > 0 ? "แปลง " + pending + " ไฟล์" : "แปลงเป็น PDF";
                btnConvert.Enabled = pending > 0;
            }
            btnConvert.Invalidate();

            if (!keepStatus && !busy)
                SetStatus(any ? rows.Count + " ไฟล์ในรายการ — ดับเบิลคลิกไฟล์ที่แปลงแล้วเพื่อเปิด, คลิกขวาเพื่อดูตัวเลือก"
                              : "พร้อมใช้งาน — เพิ่มไฟล์ .ofd เพื่อเริ่ม (Ctrl+O)");
        }

        private void SetStatus(string text)
        {
            statusLabel.Text = text;
        }

        // ---- preview ----------------------------------------------------------------------

        private void ShowPreview()
        {
            var task = ShowPreviewAsync();
        }

        private void ShowPreviewPlaceholder()
        {
            SetPreviewImage(null);
            lblPreviewEmpty.Text = "เลือกไฟล์ที่แปลงแล้ว\nเพื่อดูตัวอย่างหน้า PDF ที่นี่";
            lblPreviewName.Text = "";
            lblPreviewNote.Text = "";
            lblPage.Text = "";
            btnPrev.Enabled = btnNext.Enabled = btnOpenPdf.Enabled = false;
        }

        private async Task ShowPreviewAsync()
        {
            var sel = SelectedRows();
            var row = sel.Count == 1 ? sel[0] : null;
            if (row != previewRow) { previewRow = row; previewPage = 0; }
            int request = ++previewRequest;

            if (row == null)
            {
                ShowPreviewPlaceholder();
                if (sel.Count > 1) lblPreviewEmpty.Text = "เลือกไว้ " + sel.Count + " ไฟล์\nเลือกไฟล์เดียวเพื่อดูตัวอย่าง";
                return;
            }

            lblPreviewName.Text = Path.GetFileName(row.FilePath) + (HasPdf(row) ? "  →  " + Path.GetFileName(row.Outcome.OutputPath) : "");
            lblPreviewNote.Text = PreviewNote(row);
            if (!HasPdf(row))
            {
                SetPreviewImage(null);
                lblPage.Text = "";
                btnPrev.Enabled = btnNext.Enabled = btnOpenPdf.Enabled = false;
                lblPreviewEmpty.Text =
                    row.State == State.Failed ? "แปลงไม่สำเร็จ\n\n" + (row.Outcome == null ? "" : row.Outcome.Error) :
                    row.State == State.Converting ? "กำลังแปลง…" :
                    row.State == State.Skipped ? "ข้ามไฟล์นี้ เพราะมี PDF อยู่แล้ว" :
                    row.State == State.Done ? "ไม่พบไฟล์ PDF (อาจถูกย้ายหรือลบ)" :
                    row.Info != null && !row.Info.IsValid ? "ไฟล์นี้เปิดไม่ได้\n\n" + row.Info.Error :
                    "ยังไม่ได้แปลง\nกด \"แปลง\" แล้วตัวอย่างจะแสดงที่นี่";
                return;
            }

            if (picture.Image == null) lblPreviewEmpty.Text = "กำลังโหลดตัวอย่าง…";
            string pdf = row.Outcome.OutputPath;
            int page = previewPage;
            int dpi = (int)(96 * k);
            int count = 0;
            var image = await Task.Run(() =>
            {
                int c;
                var img = Converter.RenderPdfPage(pdf, page, dpi, out c);
                count = c;
                return img;
            });
            if (request != previewRequest || IsDisposed)
            {
                if (image != null) image.Dispose();
                return;
            }
            previewPages = count;
            SetPreviewImage(image);
            if (image == null) lblPreviewEmpty.Text = "แสดงตัวอย่างไม่ได้ — กด \"เปิด PDF\" เพื่อดูในโปรแกรมอ่าน PDF";
            lblPage.Text = count > 0 ? string.Format("หน้า {0} / {1}", page + 1, count) : "";
            btnPrev.Enabled = page > 0;
            btnNext.Enabled = page < count - 1;
            btnOpenPdf.Enabled = true;
        }

        private static string PreviewNote(Row row)
        {
            var o = row.Outcome;
            if (row.State != State.Done || o == null) return "";
            if (o.Truncated && row.Info != null)
                return string.Format("⚠ ได้ {0} จาก {1} หน้า — รุ่นฟรีแปลงได้เฉพาะ {2} หน้าแรก", o.PdfPages, row.Info.PageCount, OfdInfo.FreePageLimit);
            if (o.MissingFonts.Count > 0) return "⚠ ไม่มีฟอนต์ " + string.Join(", ", o.MissingFonts) + " — ตัวอักษรอาจเพี้ยน";
            if (o.SubstitutedFonts.Count > 0) return "แทนฟอนต์ " + string.Join(", ", o.SubstitutedFonts.Select(kv => kv.Key + "→" + kv.Value)) + " — ตรวจตัวอักษรได้จากภาพนี้";
            return "";
        }

        private void SetPreviewImage(Image image)
        {
            var old = picture.Image;
            picture.Image = image;
            if (old != null) old.Dispose();
            picture.Visible = image != null;
            lblPreviewEmpty.Visible = image == null;
        }

        // ---- actions ---------------------------------------------------------------------

        private void OpenSelectedPdf()
        {
            var sel = SelectedRows();
            if (sel.Count == 1 && HasPdf(sel[0])) Shell(sel[0].Outcome.OutputPath);
        }

        private void RevealSelected()
        {
            var sel = SelectedRows();
            if (sel.Count != 1) return;
            Reveal(HasPdf(sel[0]) ? sel[0].Outcome.OutputPath : sel[0].FilePath);
        }

        private void CopySelectedDetails()
        {
            var lines = SelectedRows().Select(r =>
            {
                var v = Describe(r);
                return v.Title + "\t" + v.Status + "\t" + v.Subtitle + "\t" + r.FilePath;
            }).ToList();
            if (lines.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                SetStatus("คัดลอกรายละเอียด " + lines.Count + " รายการแล้ว");
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
                "1. เพิ่มไฟล์ .ofd: ลากมาวาง หรือกด \"เลือกไฟล์\" / \"เลือกโฟลเดอร์\"\n" +
                "2. เลือกที่บันทึก PDF และวิธีจัดการไฟล์ชื่อซ้ำที่แถบด้านล่าง\n" +
                "3. กด \"แปลง\" (F5)\n" +
                "4. คลิกไฟล์ที่แปลงแล้วเพื่อดูตัวอย่างทางขวา, ดับเบิลคลิกเพื่อเปิด PDF, คลิกขวาหรือ ⋯ เพื่อดูตัวเลือกเพิ่ม\n\n" +
                "ทางลัด: ⚙ → เพิ่มในเมนู \"ส่งไปยัง\" แล้วคลิกขวาที่ไฟล์ .ofd ใน Explorer ได้เลย\n\n" +
                "ตัวอักษรเพี้ยน: โปรแกรมแทนฟอนต์จีนที่เครื่องไม่มีให้อัตโนมัติ ตรวจได้จากภาพตัวอย่าง ถ้าต้องการหน้าตาตรงต้นฉบับ " +
                "ให้ติดตั้ง \"Chinese (Simplified) Supplemental Fonts\" หรือคัดลอกไฟล์ฟอนต์ไปไว้ใน ⚙ → เปิดโฟลเดอร์ฟอนต์เพิ่มเติม\n\n" +
                "ข้อจำกัด: รุ่นฟรีแปลงไฟล์ OFD ได้เฉพาะ " + OfdInfo.FreePageLimit + " หน้าแรก (โปรแกรมจะแจ้งเมื่อได้ไม่ครบ)",
                MessageBoxIcon.Information);
        }

        private void ShowShortcuts()
        {
            Notify(
                "Ctrl+O\tเพิ่มไฟล์\n" +
                "Ctrl+Shift+O\tเพิ่มโฟลเดอร์\n" +
                "F5\tแปลง / หยุด\n" +
                "Enter\tเปิด PDF ของไฟล์ที่เลือก\n" +
                "Delete\tลบไฟล์ที่เลือกออกจากรายการ\n" +
                "Ctrl+A\tเลือกทั้งหมด\n" +
                "Ctrl+C\tคัดลอกรายละเอียด\n" +
                "F1\tวิธีใช้",
                MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            Notify(
                "OFD → PDF Converter " + VersionText() + "\n\n" +
                "เครื่องมือแปลง: Free Spire.PDF (แปลงไฟล์ OFD ได้ " + OfdInfo.FreePageLimit + " หน้าแรก)\n\n" +
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
                SetStatus("กำลังหยุด… โปรแกรมจะปิดเมื่อไฟล์ปัจจุบันเสร็จ");
                return;
            }
            SaveSettings();
            SetPreviewImage(null);
        }

        /// <summary>CI only: render the window to a PNG so the layout can be checked without a screen.</summary>
        private void SaveSnapshotAndExit()
        {
            try
            {
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
