using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace OfdToPdf
{
    /// <summary>Windows 11-style palette, fonts and drawing helpers.</summary>
    internal static class Theme
    {
        public static readonly Color Window = Color.FromArgb(243, 243, 243);
        public static readonly Color Card = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(229, 229, 229);
        public static readonly Color Text = Color.FromArgb(27, 27, 27);
        public static readonly Color TextSecondary = Color.FromArgb(96, 96, 96);
        public static readonly Color TextDisabled = Color.FromArgb(160, 160, 160);
        public static readonly Color Accent = Color.FromArgb(0, 95, 184);
        public static readonly Color AccentHover = Color.FromArgb(25, 110, 191);
        public static readonly Color AccentPressed = Color.FromArgb(49, 125, 198);
        public static readonly Color AccentLight = Color.FromArgb(230, 240, 250);
        public static readonly Color Hover = Color.FromArgb(245, 245, 245);
        public static readonly Color Pressed = Color.FromArgb(235, 235, 235);
        public static readonly Color Subtle = Color.FromArgb(250, 250, 250);
        public static readonly Color Success = Color.FromArgb(15, 123, 15);
        public static readonly Color Warning = Color.FromArgb(157, 93, 0);
        public static readonly Color WarningFill = Color.FromArgb(226, 148, 0);
        public static readonly Color Error = Color.FromArgb(196, 43, 28);
        public static readonly Color Neutral = Color.FromArgb(138, 138, 138);

        public const string UiFamily = "Segoe UI";
        public static readonly Font Body = new Font(UiFamily, 10f);
        public static readonly Font BodyStrong = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Caption = new Font(UiFamily, 9f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Subheading = new Font("Segoe UI Semibold", 11f);

        // Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) glyphs.
        public const string GlyphAdd = "", GlyphFolder = "", GlyphDownload = "",
            GlyphCheck = "", GlyphWarning = "", GlyphCancel = "", GlyphSync = "",
            GlyphMore = "", GlyphSettings = "", GlyphHelp = "", GlyphPlay = "",
            GlyphStop = "", GlyphPrev = "", GlyphNext = "", GlyphOpen = "",
            GlyphSkip = "", GlyphDocument = "";

        private static string iconFamily;

        /// <summary>Installed icon font, or null if neither is available (then text fallbacks are used).</summary>
        public static string IconFamily
        {
            get
            {
                if (iconFamily == null)
                {
                    using (var fonts = new InstalledFontCollection())
                    {
                        var names = fonts.Families.Select(f => f.Name).ToList();
                        iconFamily = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                            : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "";
                    }
                }
                return iconFamily.Length == 0 ? null : iconFamily;
            }
        }

        public static Font IconFont(float size)
        {
            return new Font(IconFamily ?? UiFamily, size);
        }

        /// <summary>The icon glyph if an icon font exists, otherwise a plain-text stand-in.</summary>
        public static string Glyph(string glyph, string fallback)
        {
            return IconFamily != null ? glyph : fallback;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static float Scale(Control c) { return c.DeviceDpi / 96f; }

        public static Color ParentBack(Control c)
        {
            return c.Parent != null ? c.Parent.BackColor : Window;
        }
    }

    internal enum ButtonKind { Standard, Accent, Subtle, Danger }

    /// <summary>Rounded, flat button in the Windows 11 style, with an optional icon glyph.</summary>
    internal sealed class ModernButton : Button
    {
        private bool hover, pressed;

        public ButtonKind Kind { get; set; }
        public string IconGlyph { get; set; }

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.Body;
            Cursor = Cursors.Hand;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private int IconSize { get { return (int)Math.Round(Font.Size * 1.25f * DeviceDpi / 72f); } }

        public override Size GetPreferredSize(Size proposedSize)
        {
            float k = Theme.Scale(this);
            var text = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix);
            int w = text.Width + Padding.Horizontal;
            if (!string.IsNullOrEmpty(IconGlyph)) w += IconSize + (text.Width > 0 ? (int)(8 * k) : 0);
            int h = Math.Max(text.Height, IconSize) + Padding.Vertical;
            return new Size(Math.Max(w, h), Math.Max(h, (int)(32 * k)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale(this);
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill, fore, border = Color.Empty;
            switch (Kind)
            {
                case ButtonKind.Accent:
                case ButtonKind.Danger:
                    Color baseColor = Kind == ButtonKind.Accent ? Theme.Accent : Theme.Error;
                    fill = !Enabled ? Color.FromArgb(200, 200, 200)
                        : pressed ? ControlPaint.Light(baseColor, 0.3f) : hover ? ControlPaint.Light(baseColor, 0.12f) : baseColor;
                    fore = Enabled ? Color.White : Color.FromArgb(245, 245, 245);
                    break;
                case ButtonKind.Subtle:
                    fill = !Enabled ? Theme.ParentBack(this) : pressed ? Theme.Pressed : hover ? Theme.Hover : Theme.ParentBack(this);
                    fore = Enabled ? Theme.Text : Theme.TextDisabled;
                    break;
                default:
                    fill = !Enabled ? Theme.Subtle : pressed ? Theme.Pressed : hover ? Theme.Hover : Color.White;
                    fore = Enabled ? Theme.Text : Theme.TextDisabled;
                    border = Theme.CardBorder;
                    break;
            }

            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(rect, 4 * k))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
                if (!border.IsEmpty) using (var pen = new Pen(border)) g.DrawPath(pen, path);
                if (Focused && ShowFocusCues)
                    using (var focus = Theme.Round(RectangleF.Inflate(rect, -2.5f * k, -2.5f * k), 3 * k))
                    using (var pen = new Pen(Kind == ButtonKind.Accent || Kind == ButtonKind.Danger ? Color.White : Theme.Text, 1.5f))
                        g.DrawPath(pen, focus);
            }

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            var textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            bool icon = !string.IsNullOrEmpty(IconGlyph);
            int gap = icon && textSize.Width > 0 ? (int)(8 * k) : 0;
            int contentW = textSize.Width + (icon ? IconSize + gap : 0);
            int x = (Width - contentW) / 2;
            if (icon)
            {
                using (var f = Theme.IconFont(Font.Size * 1.05f))
                    TextRenderer.DrawText(g, IconGlyph, f, new Rectangle(x, 0, IconSize, Height), fore,
                        flags | TextFormatFlags.HorizontalCenter);
                x += IconSize + gap;
            }
            if (textSize.Width > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), fore, flags);
        }
    }

    /// <summary>White rounded panel with a hairline border.</summary>
    internal class Card : Panel
    {
        public Card()
        {
            BackColor = Theme.Card;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale(this);
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * k))
            using (var brush = new SolidBrush(BackColor))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>
    /// Dashed drop target with "choose files / folder" buttons. Large when the list is empty,
    /// a slim strip once files have been added.
    /// </summary>
    internal sealed class DropZone : Control
    {
        public readonly ModernButton BtnFiles = new ModernButton { Text = "เลือกไฟล์", IconGlyph = Theme.GlyphAdd };
        public readonly ModernButton BtnFolder = new ModernButton { Text = "เลือกโฟลเดอร์", IconGlyph = Theme.GlyphFolder };
        public event Action<string[]> FilesDropped;
        public Func<bool> CanDrop = () => true;
        private bool compact, dragOver;

        public bool Compact
        {
            get { return compact; }
            set { if (compact != value) { compact = value; PerformLayout(); Invalidate(); } }
        }

        public DropZone()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            AllowDrop = true;
            BackColor = Theme.Window;
            Font = Theme.Body;
            BtnFiles.Kind = ButtonKind.Accent;
            Controls.Add(BtnFiles);
            Controls.Add(BtnFolder);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            bool ok = CanDrop() && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            dragOver = ok;
            Invalidate();
            base.OnDragEnter(e);
        }

        protected override void OnDragLeave(EventArgs e) { dragOver = false; Invalidate(); base.OnDragLeave(e); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            dragOver = false;
            Invalidate();
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && CanDrop() && FilesDropped != null) FilesDropped(files);
            base.OnDragDrop(e);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            float k = Theme.Scale(this);
            var a = BtnFiles.PreferredSize;
            var b = BtnFolder.PreferredSize;
            BtnFiles.Size = a;
            BtnFolder.Size = b;
            int gap = (int)(8 * k);
            if (compact)
            {
                int right = Width - (int)(14 * k);
                BtnFolder.Location = new Point(right - b.Width, (Height - b.Height) / 2);
                BtnFiles.Location = new Point(BtnFolder.Left - gap - a.Width, (Height - a.Height) / 2);
            }
            else
            {
                int total = a.Width + gap + b.Width;
                int y = Height / 2 + (int)(28 * k);
                BtnFiles.Location = new Point((Width - total) / 2, y);
                BtnFolder.Location = new Point(BtnFiles.Right + gap, y);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale(this);
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(rect, 8 * k))
            using (var brush = new SolidBrush(dragOver ? Theme.AccentLight : Theme.Subtle))
            using (var pen = new Pen(dragOver ? Theme.Accent : Color.FromArgb(190, 190, 190), Math.Max(1f, 1.2f * k)) { DashStyle = DashStyle.Dash })
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Color iconColor = dragOver ? Theme.Accent : Theme.TextSecondary;
            if (compact)
            {
                int x = (int)(18 * k);
                using (var icon = Theme.IconFont(14f))
                    TextRenderer.DrawText(g, Theme.Glyph(Theme.GlyphDownload, "↓"), icon,
                        new Rectangle(x, 0, (int)(28 * k), Height), iconColor, flags | TextFormatFlags.VerticalCenter);
                int textRight = BtnFiles.Left - (int)(12 * k);
                TextRenderer.DrawText(g, dragOver ? "ปล่อยเพื่อเพิ่มไฟล์" : "ลากไฟล์ .ofd หรือโฟลเดอร์มาวางเพื่อเพิ่ม", Font,
                    new Rectangle(x + (int)(36 * k), 0, Math.Max(0, textRight - x - (int)(36 * k)), Height), Theme.TextSecondary,
                    flags | TextFormatFlags.VerticalCenter);
                return;
            }

            int mid = Height / 2;
            using (var icon = Theme.IconFont(30f))
                TextRenderer.DrawText(g, Theme.Glyph(Theme.GlyphDownload, "↓"), icon,
                    new Rectangle(0, mid - (int)(110 * k), Width, (int)(56 * k)), iconColor, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, dragOver ? "ปล่อยเพื่อเพิ่มไฟล์" : "ลากไฟล์ .ofd หรือโฟลเดอร์มาวางที่นี่", Theme.Subheading,
                new Rectangle(0, mid - (int)(50 * k), Width, (int)(30 * k)), Theme.Text, flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "แปลงเป็น PDF ได้ครั้งละหลายไฟล์ · โฟลเดอร์จะรวมไฟล์ในโฟลเดอร์ย่อยด้วย", Theme.Caption,
                new Rectangle(0, mid - (int)(18 * k), Width, (int)(24 * k)), Theme.TextSecondary, flags | TextFormatFlags.HorizontalCenter);
        }
    }

    /// <summary>What one row of <see cref="FileList"/> shows.</summary>
    internal sealed class RowView
    {
        public string Glyph = "", Title = "", Subtitle = "", Status = "", Tooltip = "";
        public Color Fill = Theme.Neutral, SubtitleColor = Theme.TextSecondary, StatusColor = Theme.TextSecondary;
        public bool Hollow;
    }

    /// <summary>Owner-drawn list of files: status badge, name, details, status word and a "more" button.</summary>
    internal sealed class FileList : ListBox
    {
        public Func<object, RowView> Describe = o => new RowView { Title = Convert.ToString(o) };
        public event Action<int, Point> MoreClicked;
        private readonly ToolTip tip = new ToolTip { InitialDelay = 600, ReshowDelay = 200 };
        private int hover = -1;
        private bool hoverMore;

        public FileList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            SelectionMode = SelectionMode.MultiExtended;
            BackColor = Theme.Card;
            Font = Theme.Body;
            SetStyle(ControlStyles.ResizeRedraw, true);
            ItemHeight = Math.Min(255, (int)Math.Round(60 * Theme.Scale(this)));
        }

        private float K { get { return Theme.Scale(this); } }

        private Rectangle MoreRect(Rectangle item)
        {
            int w = (int)(36 * K);
            return new Rectangle(item.Right - w - (int)(8 * K), item.Top + (item.Height - w) / 2, w, w);
        }

        public void RefreshItem(object item)
        {
            int i = Items.IndexOf(item);
            if (i >= 0) Invalidate(GetItemRectangle(i));
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;

            float k = K;
            var v = Describe(Items[e.Index]);
            bool selected = (e.State & DrawItemState.Selected) != 0;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var inner = Rectangle.Inflate(e.Bounds, -(int)(6 * k), -(int)(3 * k));
            if (selected || e.Index == hover)
                using (var path = Theme.Round(inner, 6 * k))
                using (var b = new SolidBrush(selected ? Theme.AccentLight : Theme.Hover))
                    g.FillPath(b, path);
            if (selected)
                using (var bar = Theme.Round(new RectangleF(inner.Left, inner.Top + inner.Height * 0.3f, 3 * k, inner.Height * 0.4f), 1.5f * k))
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, bar);

            // Status badge.
            int d = (int)(28 * k);
            var badge = new Rectangle(inner.Left + (int)(14 * k), inner.Top + (inner.Height - d) / 2, d, d);
            if (v.Hollow)
                using (var pen = new Pen(v.Fill, Math.Max(1.5f, 1.5f * k))) g.DrawEllipse(pen, RectangleF.Inflate(badge, -1, -1));
            else
                using (var b = new SolidBrush(v.Fill)) g.FillEllipse(b, badge);
            if (!string.IsNullOrEmpty(v.Glyph))
                using (var icon = Theme.IconFont(9f))
                    TextRenderer.DrawText(g, v.Glyph, icon, badge, v.Hollow ? v.Fill : Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            var more = MoreRect(e.Bounds);
            int statusW = (int)(110 * k);
            int textLeft = badge.Right + (int)(14 * k);
            int textRight = more.Left - statusW - (int)(8 * k);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int lineH = (int)(22 * k);
            int top = inner.Top + (inner.Height - 2 * lineH) / 2;
            TextRenderer.DrawText(g, v.Title, Theme.BodyStrong, new Rectangle(textLeft, top, textRight - textLeft, lineH), Theme.Text, flags | TextFormatFlags.Bottom);
            TextRenderer.DrawText(g, v.Subtitle, Theme.Caption, new Rectangle(textLeft, top + lineH, textRight - textLeft, lineH), v.SubtitleColor, flags | TextFormatFlags.Top);

            TextRenderer.DrawText(g, v.Status, Theme.BodyStrong, new Rectangle(more.Left - statusW - (int)(4 * k), inner.Top, statusW, inner.Height),
                v.StatusColor, flags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            if (e.Index == hover && hoverMore)
                using (var path = Theme.Round(more, 4 * k))
                using (var b = new SolidBrush(Theme.Pressed))
                    g.FillPath(b, path);
            using (var icon = Theme.IconFont(10f))
                TextRenderer.DrawText(g, Theme.Glyph(Theme.GlyphMore, "⋯"), icon, more, Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (!selected && e.Index != hover && e.Index < Items.Count - 1)
                using (var pen = new Pen(Color.FromArgb(240, 240, 240)))
                    g.DrawLine(pen, textLeft, e.Bounds.Bottom - 1, e.Bounds.Right - (int)(14 * k), e.Bounds.Bottom - 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexFromPoint(e.Location);
            if (i >= Items.Count) i = -1;
            bool more = i >= 0 && MoreRect(GetItemRectangle(i)).Contains(e.Location);
            if (i == hover && more == hoverMore) return;
            int old = hover;
            hover = i;
            hoverMore = more;
            if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
            if (i >= 0)
            {
                Invalidate(GetItemRectangle(i));
                if (old != i) tip.SetToolTip(this, Describe(Items[i]).Tooltip);
            }
            else tip.SetToolTip(this, "");
            Cursor = more ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            int old = hover;
            hover = -1;
            hoverMore = false;
            if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexFromPoint(e.Location);
            bool onItem = i >= 0 && i < Items.Count && GetItemRectangle(i).Contains(e.Location);
            if (onItem && (e.Button == MouseButtons.Right || MoreRect(GetItemRectangle(i)).Contains(e.Location)) && !GetSelected(i))
            {
                ClearSelected();
                SetSelected(i, true);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = IndexFromPoint(e.Location);
            if (i >= 0 && i < Items.Count && e.Button == MouseButtons.Left && MoreRect(GetItemRectangle(i)).Contains(e.Location) && MoreClicked != null)
                MoreClicked(i, e.Location);
        }
    }

    /// <summary>Slim progress bar.</summary>
    internal sealed class ThinProgress : Control
    {
        private int value, maximum = 100;
        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(value, maximum)); Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = Math.Max(1, value); Invalidate(); } }

        public ThinProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Height / 2f;
            using (var track = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), r))
            using (var b = new SolidBrush(Color.FromArgb(225, 225, 225)))
                g.FillPath(b, track);
            float w = (Width - 1) * (float)value / maximum;
            if (w >= Height)
                using (var fill = Theme.Round(new RectangleF(0, 0, w, Height - 1), r))
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, fill);
        }
    }

    /// <summary>Light, flat menus to match the rest of the window.</summary>
    internal sealed class MenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return Theme.CardBorder; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color SeparatorDark { get { return Theme.CardBorder; } }
        public override Color SeparatorLight { get { return Color.White; } }
        public override Color CheckBackground { get { return Theme.AccentLight; } }
        public override Color CheckSelectedBackground { get { return Theme.AccentLight; } }
        public override Color CheckPressedBackground { get { return Theme.AccentLight; } }

        public static ContextMenuStrip NewMenu()
        {
            return new ContextMenuStrip
            {
                Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false },
                Font = Theme.Body,
                ShowImageMargin = true,
            };
        }
    }
}
