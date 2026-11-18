using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AslainAssistant
{
    // Composition and theme tokens only. Every visual control is provided by AntdUI.
    public static class UiTheme
    {
        public static Color Background { get { return AntdUI.Style.Db.BgLayout; } }
        public static Color Surface { get { return AntdUI.Style.Db.BgContainer; } }
        public static Color Border { get { return AntdUI.Style.Db.BorderSecondary; } }
        public static Color Ink { get { return AntdUI.Style.Db.Text; } }
        public static Color Muted { get { return AntdUI.Style.Db.TextSecondary; } }
        public static Color Primary { get { return AntdUI.Style.Db.Primary; } }
        public static Color Warning { get { return AntdUI.Config.IsDark ? AntdUI.Style.Db.Warning : AntdUI.Style.Db.WarningActive; } }
        static readonly Dictionary<Control,Action> themeBindings = new Dictionary<Control,Action>();
        public static void Initialize(bool dark = false)
        {
            AntdUI.Config.Mode = dark ? AntdUI.TMode.Dark : AntdUI.TMode.Light;
            AntdUI.Config.TextRenderingHighQuality = true;
            AntdUI.Config.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            AntdUI.Config.SetDpi(null);
        }
        public static void Bind(Control control,Action apply)
        {
            if (!themeBindings.ContainsKey(control)) control.Disposed += (s,e)=>themeBindings.Remove(control);
            themeBindings[control] = apply; apply();
        }
        public static void SetDark(bool dark)
        {
            AntdUI.Config.Mode = dark ? AntdUI.TMode.Dark : AntdUI.TMode.Light;
            foreach (var binding in themeBindings.ToArray()) if (!binding.Key.IsDisposed) binding.Value();
            foreach (Form form in Application.OpenForms) form.Invalidate(true);
        }
        public static void Secondary(AntdUI.Label label) { Bind(label,()=>label.ForeColor = Muted); }
        public static void LayoutVisible(Control root)
        {
            if (!root.Visible) return;
            // AntdUI layouts skip invisible controls. Re-run their own layout engine
            // after restoring the tray window or switching to a previously hidden page.
            root.PerformLayout();
            foreach (Control child in root.Controls) LayoutVisible(child);
        }
        public static AntdUI.Label Label(string text, float size = 10, bool bold = false)
        {
            return new AntdUI.Label { Text = text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true, TextMultiLine = true, Margin = Padding.Empty };
        }
        public static AntdUI.Button Button(string text, Action click, bool primary = false, string icon = null)
        {
            var button = new AntdUI.Button { Text = text, Type = primary ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default, BorderWidth = primary ? 0 : 1, Size = new Size(132,42), Radius = 8, IconSvg = icon, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI",10) };
            if (click != null) button.Click += (s,e) => click();
            return button;
        }
        public static AntdUI.Panel Card(int height)
        {
            var card = new AntdUI.Panel { Height = height, Radius = 12, BorderWidth = 1, Padding = new Padding(20), Margin = Padding.Empty, AutoContainerBgTransparent = true };
            Bind(card,()=>{card.Back = Surface; card.BorderColor = Border;}); return card;
        }
        public static AntdUI.StackPanel Column(params Control[] children)
        {
            var panel = new AntdUI.StackPanel { Vertical = true, Gap = 12, AutoScroll = false, AutoContainerBgTransparent = true, BackColor = Color.Transparent, Dock = DockStyle.Fill, Margin = Padding.Empty };
            // AntdUI layout follows WinForms z-order, from last child to first.
            for (int i = children.Length - 1; i >= 0; i--) { children[i].Margin = Padding.Empty; panel.Controls.Add(children[i]); }
            return panel;
        }
        public static AntdUI.FlowPanel Actions(params Control[] children)
        {
            var panel = new AntdUI.FlowPanel { Align = AntdUI.TAlignFlow.LeftCenter, Gap = 10, Height = 52, AutoContainerBgTransparent = true, BackColor = Color.Transparent, Margin = Padding.Empty };
            for (int i = children.Length - 1; i >= 0; i--) { children[i].Margin = Padding.Empty; panel.Controls.Add(children[i]); }
            return panel;
        }
        public static AntdUI.GridPanel Grid(string span, params Control[] children)
        {
            var panel = new AntdUI.GridPanel { Span = span, Gap = 6, BackColor = Color.Transparent, Dock = DockStyle.Fill, Margin = Padding.Empty };
            for (int i = children.Length - 1; i >= 0; i--) { children[i].Margin = Padding.Empty; panel.Controls.Add(children[i]); }
            return panel;
        }
        public static AntdUI.Window Dialog(string title, Size size)
        {
            var dialog = new AntdUI.Window();
            // Keep layout suspended until the caller has added the body, so Windows
            // scales the title bar and dynamically created content together.
            dialog.SuspendLayout();
            dialog.Text = title; dialog.ClientSize = size; dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Font = new Font("Microsoft YaHei UI",10); dialog.Icon = Brand.LoadIcon(); dialog.Resizable = false;
            Bind(dialog,()=>{dialog.BackColor = Background; dialog.ForeColor = Ink;});
            dialog.AutoScaleDimensions = new SizeF(96,96); dialog.AutoScaleMode = AutoScaleMode.Dpi;
            dialog.Controls.Add(new AntdUI.PageHeader { Text = title, Height = 58, Dock = DockStyle.Top, ShowButton = true, MaximizeBox = false, MinimizeBox = false, DividerShow = true, Font = new Font("Microsoft YaHei UI",12,FontStyle.Bold) });
            return dialog;
        }
        public static void Notice(Form owner, string title, string content, AntdUI.TType kind = AntdUI.TType.Info)
        {
            AntdUI.Modal.open(new AntdUI.Modal.Config(owner,title,content,kind) { OkText = "知道了", CancelText = null, MaskClosable = false, Width = 560, Font = new Font("Microsoft YaHei UI",10) });
        }
    }
}
