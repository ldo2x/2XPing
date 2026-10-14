using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PingBoost
{
    // The dark owner-drawn lists of the main window (servers, relays): rows, hover, header and RTL reading order.
    // The ListView itself stays left-to-right; for Arabic the column order is reversed and the text right-aligned.
    static class ListStyle
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        static ListViewItem hotRow; // the row under the mouse

        public static Color RowBack(ListViewItem item, int index)
        {
            if (item.Selected) return Theme.RowSel;
            if (item == hotRow) return Theme.RowHover;
            return index % 2 == 0 ? Theme.Panel1 : Theme.RowAlt;
        }

        // Text alignment inside owner-drawn lists (right-aligned, RTL reading for Arabic).
        public static TextFormatFlags Align
        {
            get { return Lang.Ar ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left; }
        }

        // Bounds of the first column (ListView reports the whole row for column 0, and it may not be displayed first).
        public static Rectangle FirstCell(ListView lv, ListViewItem item, Rectangle row)
        {
            int x = row.X;
            for (int i = 1; i < lv.Columns.Count; i++)
                if (lv.Columns[i].DisplayIndex < lv.Columns[0].DisplayIndex) x += lv.Columns[i].Width;
            return new Rectangle(x, row.Y, lv.Columns[0].Width, row.Height);
        }

        // The pick tick box inside a first-column cell (on the reading side).
        public static Rectangle CheckBox(Rectangle cell)
        {
            int x = Lang.Ar ? cell.Right - 8 - 18 : cell.X + 8;
            return new Rectangle(x, cell.Y + (cell.Height - 18) / 2, 18, 18);
        }

        // font: the window's font (all text is drawn with it). drawnElsewhere: cells another handler draws.
        // dotColumn: a column whose "ms" values get a dot in their colour (-1 = none).
        public static ListView Make(string[] cols, int[] widths, Func<Font> font,
                                    Func<DrawListViewSubItemEventArgs, bool> drawnElsewhere, int dotColumn)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Theme.Panel1, ForeColor = Theme.TextC,
                OwnerDraw = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                // with an embedded (private) font WinForms stops owner-drawing the header, so the control keeps a
                // system font; all text is still drawn with the form's font (Readex Pro) in the Draw* handlers below
                Font = new Font("Segoe UI", 10f),
                // Kept left-to-right internally; for Arabic the column order is reversed and text is right-aligned.
                RightToLeft = RightToLeft.No, RightToLeftLayout = false,
                SmallImageList = new ImageList { ImageSize = new Size(1, 30) }
            };
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(lv, true, null);
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            if (Lang.Ar)
                for (int i = 0; i < cols.Length; i++) lv.Columns[i].DisplayIndex = cols.Length - 1 - i;

            // hidden columns (width 0 in the simple layout) cannot be dragged open
            lv.ColumnWidthChanging += (s, e) =>
            {
                if (widths[e.ColumnIndex] == 0) { e.Cancel = true; e.NewWidth = 0; }
            };
            // the first column (server name) takes the remaining width
            lv.Resize += delegate
            {
                int used = 0;
                for (int i = 1; i < lv.Columns.Count; i++) used += lv.Columns[i].Width;
                // room for the vertical scrollbar even before it shows, so the list never scrolls sideways
                lv.Columns[0].Width = Math.Max(widths[0], lv.ClientSize.Width - used - (lv.ClientSize.Width < lv.Width ? 0 : SystemInformation.VerticalScrollBarWidth));
            };
            // dark scrollbars on the dark list (Windows 10 1809 and later; older ones keep the light ones)
            lv.HandleCreated += delegate { try { SetWindowTheme(lv.Handle, "DarkMode_Explorer", null); } catch { } };

            lv.DrawColumnHeader += (s, e) =>
            {
                if (e.Bounds.Width <= 0) return;
                using (var b = new SolidBrush(Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, font(), Rectangle.Inflate(e.Bounds, -8, 0), Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
            };
            lv.DrawItem += (s, e) => { };
            lv.MouseMove += (s, e) =>
            {
                var item = lv.GetItemAt(e.X, e.Y);
                if (item == hotRow) return;
                var old = hotRow;
                hotRow = item;
                if (old != null && old.ListView == lv) lv.Invalidate(old.Bounds);
                if (item != null) lv.Invalidate(item.Bounds);
            };
            lv.MouseLeave += delegate { var old = hotRow; hotRow = null; if (old != null && old.ListView == lv) lv.Invalidate(old.Bounds); };
            lv.DrawSubItem += (s, e) =>
            {
                if (drawnElsewhere(e)) return;
                var rc = e.ColumnIndex == 0 ? FirstCell(lv, e.Item, e.Bounds) : e.Bounds;
                if (rc.Width <= 0) return;
                using (var b = new SolidBrush(RowBack(e.Item, e.ItemIndex))) e.Graphics.FillRectangle(b, rc);
                var textRc = Rectangle.Inflate(rc, -8, 0);
                var f = font();
                if (e.ColumnIndex == dotColumn && e.SubItem.Text.Contains("ms"))
                {
                    // a dot in the ping colour, on the reading side of the number
                    const int d = 8;
                    var size = TextRenderer.MeasureText(e.SubItem.Text, f);
                    int x = Lang.Ar ? textRc.Right - size.Width - d - 6 : textRc.X;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(e.SubItem.ForeColor)) e.Graphics.FillEllipse(b, x, rc.Y + (rc.Height - d) / 2f, d, d);
                    if (!Lang.Ar) textRc = new Rectangle(textRc.X + d + 6, textRc.Y, textRc.Width - d - 6, textRc.Height);
                }
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, f, textRc, e.SubItem.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
            };
            return lv;
        }
    }
}
