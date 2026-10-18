using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

/// <summary>A bounded, incrementally updated display. The document never owns raw recording.</summary>
public sealed class SerialLogView : UserControl
{
    private sealed record DisplayRun(long Start, string Direction, Run Run)
    {
        public long End => Start + Run.Text.Length;
    }
    public sealed record Reading(long Anchor, double AnchorY, long SelectionStart, long SelectionEnd, bool Following);
    private readonly LinkedList<DisplayRun> runs = new();
    private readonly Paragraph paragraph = new() { Margin = new Thickness(0) };
    private readonly TextBlock caption = new(), notice = new();
    private readonly Button latest = new() { Content = "回到最新", Margin = new Thickness(4, 0, 0, 3), Padding = new Thickness(6, 2, 6, 2), MinHeight = 26 };
    public RichTextBox Editor { get; }
    public SerialTextSnapshot? Snapshot { get; private set; }
    public bool Following { get; private set; } = true;
    public int RunCount => runs.Count;
    private bool updating;
    private long renderedEnd;
    public string Heading { get; set; } = "实时日志";

    public SerialLogView()
    {
        var layout = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Height = 32 };
        caption.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(latest, Dock.Right); header.Children.Add(latest); header.Children.Add(caption);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        notice.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        notice.FontSize = 11; notice.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(notice, Dock.Bottom); layout.Children.Add(notice);
        Editor = new RichTextBox(new FlowDocument(paragraph) { PagePadding = new Thickness(0) })
        {
            IsReadOnly = true,
            IsUndoEnabled = false,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0)
        };
        Editor.SetResourceReference(Control.BackgroundProperty, "ConsoleBrush");
        Editor.SetResourceReference(Control.ForegroundProperty, "ConsoleText");
        Editor.Document.FontFamily = Editor.FontFamily; Editor.Document.FontSize = Editor.FontSize;
        layout.Children.Add(Editor); Content = layout;
        latest.Click += (_, _) => ResumeFollowing();
        Editor.PreviewMouseWheel += (_, _) => PauseFollowing();
        Editor.PreviewMouseDown += (_, _) => PauseFollowing();
        Editor.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseFollowing(); };
        Editor.SelectionChanged += (_, _) => { if (!updating && !Editor.Selection.IsEmpty) PauseFollowing(); };
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (!updating && e.ExtentHeightChange == 0 && e.VerticalChange != 0 && Editor.VerticalOffset + Editor.ViewportHeight < Editor.ExtentHeight - 2) PauseFollowing();
        }));
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            var text = SelectedText();
            if (text.Length > 0) Clipboard.SetText(text);
            e.Handled = true;
        }, (_, e) => { e.CanExecute = !Editor.Selection.IsEmpty; e.Handled = true; }));
        UpdateHeading();
    }
    public void PauseFollowing() { Following = false; UpdateHeading(); }
    public void ResumeFollowing()
    {
        Following = true; updating = true;
        try { Editor.Selection.Select(paragraph.ContentEnd, paragraph.ContentEnd); Editor.ScrollToEnd(); }
        finally { updating = false; }
        UpdateHeading();
    }
    private void UpdateHeading()
    {
        caption.Text = Heading + (Snapshot == null ? "" : " · " + Snapshot.Mode);
        latest.Visibility = Heading == "历史快照" ? Visibility.Collapsed : Visibility.Visible;
        latest.IsEnabled = !Following;
    }
    private long Offset(TextPointer pointer)
    {
        foreach (var run in runs)
        {
            if (pointer.CompareTo(run.Run.ContentStart) <= 0) return run.Start;
            if (pointer.CompareTo(run.Run.ContentEnd) <= 0) return run.Start + run.Run.ContentStart.GetOffsetToPosition(pointer);
        }
        return renderedEnd;
    }
    private TextPointer Pointer(long offset)
    {
        foreach (var run in runs)
            if (offset <= run.End) return run.Run.ContentStart.GetPositionAtOffset((int)Math.Clamp(offset - run.Start, 0, run.Run.Text.Length))!;
        return paragraph.ContentEnd;
    }
    public Reading ReadPosition()
    {
        var pointer = Editor.GetPositionFromPoint(new Point(9, 9), true) ?? paragraph.ContentStart;
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        return new(Offset(pointer), rect.IsEmpty ? 0 : rect.Y, Offset(Editor.Selection.Start), Offset(Editor.Selection.End), Following);
    }
    public void RestorePosition(Reading state)
    {
        updating = true;
        try
        {
            Following = state.Following;
            Editor.Selection.Select(Pointer(state.SelectionStart), Pointer(state.SelectionEnd));
            Editor.UpdateLayout();
            if (Following) Editor.ScrollToEnd();
            else if (Snapshot != null && state.Anchor < Snapshot.Start)
            { Editor.ScrollToHome(); notice.Text = "阅读位置已移出实时窗口；较早内容已裁剪。"; }
            else
            {
                var rect = Pointer(state.Anchor).GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty) Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Y - state.AnchorY));
            }
        }
        finally { updating = false; }
        UpdateHeading();
    }
    public void Render(SerialTextSnapshot snapshot)
    {
        var position = ReadPosition();
        updating = true;
        try
        {
            if (snapshot.Start == snapshot.End) notice.Text = "";
            else if (Snapshot != null && snapshot.Start > Snapshot.Start && snapshot.Start < renderedEnd)
                notice.Text = "较早内容已移出实时窗口；可在已启用的原始记录中查阅。";
            if (snapshot.Start > renderedEnd || snapshot.End < renderedEnd || snapshot.Start == snapshot.End)
            { paragraph.Inlines.Clear(); runs.Clear(); renderedEnd = snapshot.Start; }
            while (runs.First is { } first && first.Value.End <= snapshot.Start)
            { paragraph.Inlines.Remove(first.Value.Run); runs.RemoveFirst(); }
            if (runs.First is { } head && head.Value.Start < snapshot.Start)
            {
                var remove = (int)(snapshot.Start - head.Value.Start);
                new TextRange(head.Value.Run.ContentStart, head.Value.Run.ContentStart.GetPositionAtOffset(remove)!).Text = "";
                head.Value = head.Value with { Start = snapshot.Start };
            }
            foreach (var span in snapshot.Spans)
            {
                if (span.End <= renderedEnd) continue;
                var start = Math.Max(span.Start, renderedEnd);
                var value = span.Text[(int)(start - span.Start)..];
                if (runs.Last is { } last && last.Value.Direction == span.Direction && last.Value.End == start && last.Value.Run.Text.Length + value.Length <= 1024)
                    last.Value.Run.ContentEnd.InsertTextInRun(value);
                else
                {
                    var run = new Run(value);
                    run.SetResourceReference(TextElement.ForegroundProperty, span.Direction == "TX" ? "SerialTxBrush" : "SerialRxBrush");
                    paragraph.Inlines.Add(run); runs.AddLast(new DisplayRun(start, span.Direction, run));
                }
                renderedEnd = span.End;
            }
            Snapshot = snapshot;
        }
        finally { updating = false; }
        RestorePosition(position);
    }
    public string SelectedText()
    {
        var result = new StringBuilder();
        var start = Offset(Editor.Selection.Start); var end = Offset(Editor.Selection.End);
        foreach (var run in runs)
        {
            var from = (int)Math.Clamp(start - run.Start, 0, run.Run.Text.Length);
            var to = (int)Math.Clamp(end - run.Start, 0, run.Run.Text.Length);
            if (to > from) result.Append(run.Run.Text.AsSpan(from, to - from));
        }
        return result.ToString();
    }
    public string PlainText => string.Concat(runs.Select(r => r.Run.Text));
    public void Release()
    {
        updating = true;
        try { paragraph.Inlines.Clear(); runs.Clear(); renderedEnd = 0; Snapshot = null; notice.Text = ""; }
        finally { updating = false; }
    }
}
