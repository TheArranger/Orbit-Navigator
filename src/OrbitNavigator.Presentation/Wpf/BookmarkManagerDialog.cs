#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Wpf;

public sealed record BookmarkSaveDraft(BookmarkId? ExistingId, Uri Target, string Title)
{
    public string? Note { get; init; }
}

public sealed class BookmarkSaveRequestedEventArgs : EventArgs
{
    public BookmarkSaveRequestedEventArgs(BookmarkSaveDraft draft) => Draft = draft;
    public BookmarkSaveDraft Draft { get; }
}

public sealed class BookmarkDeleteRequestedEventArgs : EventArgs
{
    public BookmarkDeleteRequestedEventArgs(BookmarkEntry bookmark) => Bookmark = bookmark;
    public BookmarkEntry Bookmark { get; }
}

/// <summary>Dark bookmark manager UI. Host owns queries and mutations.</summary>
public sealed class BookmarkManagerDialog : Window
{
    private readonly ListBox list = new() { MinHeight = 270 };
    private readonly TextBox url = new();
    private readonly TextBox title = new();
    private readonly TextBox note = new() { AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap };
    private readonly Border editor = OrbitVisualTheme.CreateSurface(12);
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button add = new() { Content = "Add bookmark" };
    private readonly Button open = new() { Content = "Open", MinWidth = 82 };
    private readonly Button edit = new() { Content = "Edit", MinWidth = 82 };
    private readonly Button delete = new() { Content = "Delete", MinWidth = 82 };
    private readonly Button save = new() { Content = "Save bookmark", IsDefault = true };
    private readonly Button cancelEdit = new() { Content = "Cancel" };
    private readonly Button close = new() { Content = "Close", IsCancel = true };
    private readonly bool canModify;
    private readonly bool canEdit;
    private IReadOnlyDictionary<BookmarkId, string> bookmarkNotes;
    private BookmarkEntry? editing;
    private BookmarkEntry? pendingDelete;

    public BookmarkManagerDialog(
        PrivacyContext context,
        IReadOnlyList<BookmarkEntry> bookmarks,
        bool canEditBookmarks,
        IReadOnlyDictionary<BookmarkId, string>? notes = null)
    {
        canModify = !context.IsPrivate;
        canEdit = canModify && canEditBookmarks;
        bookmarkNotes = notes ?? new Dictionary<BookmarkId, string>();
        Title = "Bookmarks — Orbit Navigator";
        Width = 720;
        Height = 650;
        MinWidth = 560;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = OrbitVisualTheme.Canvas;
        Foreground = OrbitVisualTheme.Ink;
        BuildLayout();
        SetBookmarks(bookmarks, bookmarkNotes);
        ApplyTheme();
        ContentRendered += (_, _) => list.Focus();
    }

    public event EventHandler<BookmarkEntry>? OpenRequested;
    public event EventHandler<BookmarkSaveRequestedEventArgs>? SaveRequested;
    public event EventHandler<BookmarkDeleteRequestedEventArgs>? DeleteRequested;

    public void SetBookmarks(IReadOnlyList<BookmarkEntry> bookmarks)
        => SetBookmarks(bookmarks, bookmarkNotes);

    public void SetBookmarks(
        IReadOnlyList<BookmarkEntry> bookmarks,
        IReadOnlyDictionary<BookmarkId, string> notes)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        bookmarkNotes = notes ?? throw new ArgumentNullException(nameof(notes));
        list.Items.Clear();
        foreach (var bookmark in bookmarks)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new OrbitEmberStar(OrbitEmberStarKind.Favorite)
            {
                IsActive = true,
                MotionEnabled = false,
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 10, 0),
            });
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock { Text = bookmark.Title, FontWeight = FontWeights.SemiBold });
            copy.Children.Add(new TextBlock
            {
                Text = bookmark.Target.AbsoluteUri,
                Foreground = OrbitVisualTheme.MutedInk,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (bookmarkNotes.TryGetValue(bookmark.Id, out var savedNote) && !string.IsNullOrWhiteSpace(savedNote))
            {
                copy.Children.Add(new TextBlock
                {
                    Text = savedNote,
                    Foreground = OrbitVisualTheme.MutedInk,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 520,
                });
            }
            content.Children.Add(copy);
            list.Items.Add(new ListBoxItem { Content = content, Tag = bookmark, Padding = new Thickness(10, 8, 10, 8) });
        }
        list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        UpdateActions();
    }

    public void ShowOperationResult(string safeMessage, bool isError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        status.Text = safeMessage.Trim();
        status.Foreground = isError ? OrbitVisualTheme.Danger : OrbitVisualTheme.SeaGlass;
        status.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(status, isError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (!isError) { editor.Visibility = Visibility.Collapsed; editing = null; add.Focus(); }
    }

    private void BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new OrbitEmberStar(OrbitEmberStarKind.Favorite)
        {
            IsActive = true,
            MotionEnabled = false,
            Width = 38,
            Height = 38,
            Margin = new Thickness(0, 0, 12, 0),
        });
        var headings = new StackPanel();
        var heading = new TextBlock { Text = "Bookmarks", FontSize = 26, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        headings.Children.Add(heading);
        headings.Children.Add(new TextBlock
        {
            Text = canModify ? "Open or manage local bookmarks." : "Private windows can view bookmarks but cannot change them.",
            Foreground = OrbitVisualTheme.MutedInk,
        });
        header.Children.Add(headings);
        root.Children.Add(header);
        list.Margin = new Thickness(0, 18, 0, 10);
        AutomationProperties.SetName(list, "Saved bookmarks");
        list.SelectionChanged += (_, _) => UpdateActions();
        list.MouseDoubleClick += (_, _) => OpenSelected();
        list.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { OpenSelected(); args.Handled = true; }
            else if (args.Key == Key.Delete && delete.IsEnabled) { RequestDelete(); args.Handled = true; }
        };
        root.Children.Add(list);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        add.Margin = open.Margin = edit.Margin = delete.Margin = new Thickness(0, 0, 8, 0);
        add.Click += (_, _) => BeginEdit(null);
        open.Click += (_, _) => OpenSelected();
        edit.Click += (_, _) => BeginEdit(Selected());
        delete.Click += (_, _) => RequestDelete();
        close.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Children.Add(add); actions.Children.Add(open); actions.Children.Add(edit); actions.Children.Add(delete); actions.Children.Add(close);
        root.Children.Add(actions);
        editor.Margin = new Thickness(0, 18, 0, 0);
        editor.Padding = new Thickness(18);
        editor.Visibility = Visibility.Collapsed;
        var editPanel = new StackPanel();
        var editorHeading = new TextBlock { Text = "Bookmark details", FontSize = 18, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(editorHeading, AutomationHeadingLevel.Level2);
        editPanel.Children.Add(editorHeading);
        editPanel.Children.Add(Label("Address / URL"));
        AutomationProperties.SetName(url, "Bookmark address or URL");
        editPanel.Children.Add(url);
        editPanel.Children.Add(Label("Title"));
        AutomationProperties.SetName(title, "Bookmark title");
        editPanel.Children.Add(title);
        editPanel.Children.Add(Label("Optional note"));
        AutomationProperties.SetName(note, "Bookmark note");
        AutomationProperties.SetHelpText(note, "Optional local note, up to 500 characters.");
        editPanel.Children.Add(note);
        var editorActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        cancelEdit.Margin = new Thickness(0, 0, 8, 0);
        cancelEdit.Click += (_, _) => CancelEditing();
        save.Click += (_, _) => SaveEditing();
        editorActions.Children.Add(cancelEdit); editorActions.Children.Add(save);
        editPanel.Children.Add(editorActions);
        editor.Child = editPanel;
        root.Children.Add(editor);
        status.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetName(status, "Bookmark operation status");
        root.Children.Add(status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(add, "Add bookmark");
        AutomationProperties.SetName(open, "Open selected bookmark");
        AutomationProperties.SetName(edit, "Edit selected bookmark");
        AutomationProperties.SetName(delete, "Delete selected bookmark");
        AutomationProperties.SetName(save, "Save bookmark");
        AutomationProperties.SetName(cancelEdit, "Cancel bookmark editing");
        AutomationProperties.SetName(close, "Close bookmarks");
        AutomationProperties.SetHelpText(edit, canEdit ? "Edit the selected bookmark." : "Editing existing bookmarks is unavailable.");
        AutomationProperties.SetHelpText(delete, canModify ? "Delete the selected bookmark after confirmation." : "Bookmark changes are unavailable in private browsing.");
        AutomationProperties.SetHelpText(add, canModify ? "Add a local bookmark." : "Bookmark changes are unavailable in private browsing.");
    }

    private void BeginEdit(BookmarkEntry? bookmark)
    {
        if (!canModify || (bookmark is not null && !canEdit)) return;
        ResetDeleteConfirmation();
        editing = bookmark;
        url.Text = bookmark?.Target.AbsoluteUri ?? string.Empty;
        title.Text = bookmark?.Title ?? string.Empty;
        note.Text = bookmark is not null && bookmarkNotes.TryGetValue(bookmark.Id, out var savedNote)
            ? savedNote
            : string.Empty;
        editor.Visibility = Visibility.Visible;
        status.Visibility = Visibility.Collapsed;
        url.Focus(); url.SelectAll();
    }

    private void CancelEditing() { editor.Visibility = Visibility.Collapsed; editing = null; ResetDeleteConfirmation(); list.Focus(); }

    private void SaveEditing()
    {
        var rawUrl = url.Text.Trim();
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
        { Fail("Enter a valid HTTP(S) bookmark address.", url); return; }
        var value = title.Text.Trim();
        if (value.Length is < 1 or > 200) { Fail("Enter a bookmark title of 1 to 200 characters.", title); return; }
        var noteValue = note.Text.Trim();
        if (noteValue.Length > 500) { Fail("Keep the bookmark note to 500 characters or fewer.", note); return; }
        status.Text = "Waiting for the browser to save this bookmark…";
        status.Foreground = OrbitVisualTheme.MutedInk;
        status.Visibility = Visibility.Visible;
        if (SaveRequested is null)
        {
            Fail("Saving bookmarks is unavailable in this build.", url);
            return;
        }
        SaveRequested.Invoke(this, new(new BookmarkSaveDraft(editing?.Id, target, value)
        {
            Note = noteValue.Length == 0 ? null : noteValue,
        }));
    }

    private void RequestDelete()
    {
        if (!canModify || Selected() is not { } bookmark) return;
        if (pendingDelete?.Id != bookmark.Id)
        {
            pendingDelete = bookmark;
            delete.Content = "Confirm delete";
            status.Text = $"Delete “{bookmark.Title}”? Choose Confirm delete to remove it.";
            status.Foreground = OrbitVisualTheme.WaypointGold;
            status.Visibility = Visibility.Visible;
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
            AutomationProperties.SetName(delete, $"Confirm delete bookmark {bookmark.Title}");
            delete.Focus();
            return;
        }

        ResetDeleteConfirmation();
        if (DeleteRequested is null)
        {
            ShowOperationResult("Deleting bookmarks is unavailable in this build.", isError: true);
            return;
        }
        DeleteRequested.Invoke(this, new(bookmark));
    }

    private void OpenSelected()
    {
        if (Selected() is not { } item) return;
        if (OpenRequested is null)
        {
            ShowOperationResult("Opening bookmarks is unavailable in this build.", isError: true);
            return;
        }
        OpenRequested.Invoke(this, item);
    }
    private BookmarkEntry? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as BookmarkEntry;
    private void UpdateActions()
    {
        ResetDeleteConfirmation();
        var selected = Selected() is not null;
        open.IsEnabled = selected;
        edit.IsEnabled = selected && canEdit;
        delete.IsEnabled = selected && canModify;
        add.IsEnabled = canModify;
    }

    private void ResetDeleteConfirmation()
    {
        pendingDelete = null;
        delete.Content = "Delete";
        AutomationProperties.SetName(delete, "Delete selected bookmark");
    }
    private void Fail(string message, Control control) { status.Text = message; status.Foreground = OrbitVisualTheme.Danger; status.Visibility = Visibility.Visible; AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive); control.Focus(); }

    private void ApplyTheme()
    {
        if (SystemParameters.HighContrast)
        {
            Background = SystemColors.WindowBrush;
            Foreground = SystemColors.WindowTextBrush;
            list.Background = SystemColors.WindowBrush;
            list.Foreground = SystemColors.WindowTextBrush;
            list.BorderBrush = SystemColors.WindowTextBrush;
        }
        else
        {
            Background = OrbitVisualTheme.Canvas;
            Foreground = OrbitVisualTheme.Ink;
            list.Background = OrbitVisualTheme.Surface;
            list.Foreground = OrbitVisualTheme.Ink;
            list.BorderBrush = OrbitVisualTheme.Divider;
        }
        OrbitVisualTheme.ApplyTextBox(url); OrbitVisualTheme.ApplyTextBox(title); OrbitVisualTheme.ApplyTextBox(note);
        OrbitVisualTheme.ApplyButton(add, OrbitButtonRole.Primary); OrbitVisualTheme.ApplyButton(open, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(edit, OrbitButtonRole.Quiet); OrbitVisualTheme.ApplyButton(delete, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(close, OrbitButtonRole.Quiet); OrbitVisualTheme.ApplyButton(save, OrbitButtonRole.Primary);
        OrbitVisualTheme.ApplyButton(cancelEdit, OrbitButtonRole.Quiet);
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Foreground = OrbitVisualTheme.MutedInk, Margin = new Thickness(0, 12, 0, 5) };
}
#endif
