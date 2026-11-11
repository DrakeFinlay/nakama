using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AnkiStats.Core;

namespace AnkiStats.App;

public partial class MainWindow
{
    private List<MediaMiningWord> mediaMiningWords = [];
    private HashSet<string>? mediaMiningTitleIds;

    private void ChooseMediaMiningTitles(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Owner = this, Title = "Choose completed titles", Width = 600, Height = 520,
            MinWidth = 420, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        dialog.Content = panel;

        var filters = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(filters, Dock.Top);
        panel.Children.Add(filters);
        filters.Children.Add(new TextBlock { Text = "Search completed titles", Margin = new Thickness(0, 0, 0, 4) });
        var search = new TextBox();
        AutomationProperties.SetName(search, "Search completed titles");
        filters.Children.Add(search);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        filters.Children.Add(actions);
        var selectMatches = new Button { Content = "Select all matches" };
        var clear = new Button { Content = "Clear selection" };
        actions.Children.Add(selectMatches);
        actions.Children.Add(clear);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        footer.Children.Add(summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(buttons);
        var apply = new Button { Content = "Apply", IsDefault = true };
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });

        var titles = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = titles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var choices = mediaLibrary.Where(c => c.State == MediaShelves.Completed).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new CheckBox { Tag = c.Id, Content = c.Title, ToolTip = c.Edition,
                IsChecked = mediaMiningTitleIds?.Contains(c.Id) == true }).ToArray();
        foreach (var choice in choices)
        {
            titles.Children.Add(choice);
            choice.Click += (_, _) => UpdateSummary();
        }

        void UpdateSummary()
        {
            var selected = choices.Count(choice => choice.IsChecked == true);
            var matches = choices.Count(choice => choice.Visibility == Visibility.Visible);
            summary.Text = $"{matches:N0} matching · " + (selected == 0 ? "All completed titles will be used." : $"{selected:N0} selected");
            selectMatches.IsEnabled = matches > 0;
            clear.IsEnabled = selected > 0;
        }
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            foreach (var choice in choices)
                choice.Visibility = ((string)choice.Content).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            UpdateSummary();
        };
        selectMatches.Click += (_, _) =>
        {
            foreach (var choice in choices.Where(choice => choice.Visibility == Visibility.Visible)) choice.IsChecked = true;
            UpdateSummary();
        };
        clear.Click += (_, _) =>
        {
            foreach (var choice in choices) choice.IsChecked = false;
            UpdateSummary();
        };
        apply.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => search.Focus();
        UpdateSummary();
        if (dialog.ShowDialog() != true) return;

        var selectedIds = choices.Where(choice => choice.IsChecked == true).Select(choice => (string)choice.Tag).ToHashSet(StringComparer.Ordinal);
        mediaMiningTitleIds = selectedIds.Count == 0 ? null : selectedIds;
        RefreshMediaMining();
    }

    private void MediaMiningFilterChanged(object sender, RoutedEventArgs e) { if (ready) RefreshMediaMining(); }
    private void RefreshMediaMining()
    {
        var completed = mediaLibrary.Where(c => c.State == MediaShelves.Completed).ToList();
        var titleCount = mediaMiningTitleIds is null ? completed.Count : completed.Count(c => mediaMiningTitleIds.Contains(c.Id));
        MediaMiningTitles.Content = mediaMiningTitleIds is null ? "Titles: All Completed…" : $"Titles: {titleCount:N0} selected…";
        MediaMiningTitles.IsEnabled = completed.Count > 0 || mediaMiningTitleIds is not null;
        if (!HasMediaComparison)
        {
            mediaMiningWords = []; MediaMiningGrid.ItemsSource = null;
            MediaMiningStatus.Text = "Compare all Anki vocabulary to browse missing words across Completed. Unknown Anki presence is not treated as missing.";
            return;
        }
        if (!int.TryParse(MediaMiningMinimum.Text, out var minimum) || minimum < 1)
        {
            mediaMiningWords = []; MediaMiningGrid.ItemsSource = null; MediaMiningStatus.Text = "Minimum titles must be a positive whole number."; return;
        }
        var search = MediaMiningSearch.Text.Trim();
        var scope = mediaMiningTitleIds is null ? $"all {titleCount:N0} completed titles" : $"{titleCount:N0} selected completed titles";
        if (minimum > titleCount)
        {
            mediaMiningWords = [];
            MediaMiningGrid.ItemsSource = null;
            MediaMiningStatus.Text = titleCount == 0
                ? (completed.Count == 0 ? "No completed titles are available." : "Choose at least one completed title or clear the title selection.")
                : $"Using {scope}. Lower Minimum titles from {minimum:N0} or include more completed titles.";
            return;
        }
        mediaMiningWords = MediaMining.Missing(mediaLibrary.Select(MappedCorpus), mediaCoverage!, minimum, MediaMiningPool.SelectedIndex == 0, mediaMiningTitleIds)
            .Where(w => search.Length == 0 || $"{w.Word}\n{w.Reading}\n{w.Meaning}\n{w.Titles}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        MediaMiningGrid.ItemsSource = mediaMiningWords;
        MediaMiningStatus.Text = $"{mediaMiningWords.Count:N0} missing entries across {scope} · minimum {minimum} titles · "
            + $"{mediaMiningWords.Count(w => w.Sentence.Length > 0):N0} with source sentences. Ranked by title count, then occurrences. "
            + "Select rows, prepare the first N, or prepare all filtered rows. Preview never creates notes by itself.";
    }
    private async void PreviewMediaMining(object sender, RoutedEventArgs e) => await MediaAction(async token =>
    {
        ValidateCreationSession();
        if (!HasMediaComparison) throw new InvalidOperationException("Compare all Anki vocabulary before preparing a media batch.");
        var mode = (string)((Button)sender).Tag;
        var words = mode == "selected" ? mediaMiningWords.Where(w => MediaMiningGrid.SelectedItems.Contains(w)).ToList() : mediaMiningWords.ToList();
        if (words.Count == 0) throw new InvalidOperationException("No missing words are selected in the current completed-media filter.");
        var count = words.Count;
        if (mode == "number" && (!int.TryParse(MediaMiningCount.Text, out count) || count < 1))
            throw new InvalidOperationException("Enter a positive number of words to prepare.");
        if (MediaMiningSentences.IsChecked != true) words = words.Select(w => w with { Sentence = "", SentenceSource = "" }).ToList();
        await ReviewMediaBatch(words, count, token, message => MediaMiningStatus.Text = message);
        RefreshMediaMining();
        if (!string.IsNullOrWhiteSpace(CreationStatus.Text)) MediaMiningStatus.Text += "\n" + CreationStatus.Text;
    });

    private void MediaWordsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        var rows = MediaWordsGrid.SelectedItems.OfType<MediaWordRow>().ToList();
        var available = rows.Count(MediaMining.CanImport);
        MediaWordSelectionSummary.Text = $"{rows.Count:N0} selected · {available:N0} available to import. Ctrl-click or Shift-click multiple rows; Ctrl+A selects all displayed rows.";
        PreviewSelectedMediaWordsButton.IsEnabled = HasMediaComparison && available > 0;
    }

    private async void PreviewSelectedMediaWords(object sender, RoutedEventArgs e) => await MediaAction(async token =>
    {
        ValidateCreationSession();
        if (!HasMediaComparison || selectedMedia is null) throw new InvalidOperationException("Open a title and compare all Anki vocabulary first.");
        var selectedIds = MediaWordsGrid.SelectedItems.OfType<MediaWordRow>().Select(w => w.EntryId).ToList();
        var words = MediaMining.Selected(MappedCorpus(selectedMedia), mediaCoverage!, selectedIds);
        if (words.Count == 0) throw new InvalidOperationException("Select resolved words that are missing from Anki. Existing notes, possible matches and unresolved forms cannot be imported.");
        if (MediaWordSentences.IsChecked != true) words = words.Select(w => w with { Sentence = "", SentenceSource = "" }).ToList();
        var skipped = selectedIds.Distinct().Count() - words.Count;
        await ReviewMediaBatch(words, words.Count, token, message => MediaWordImportStatus.Text = message);
        MediaWordImportStatus.Text = (skipped > 0 ? $"Skipped {skipped:N0} selected entries already present, unresolved, or awaiting mapping review.\n" : "") + CreationStatus.Text;
    });

    private async Task ReviewMediaBatch(List<MediaMiningWord> words, int count, CancellationToken token, Action<string> report)
    {
        var imported = importedData!; var selectedCoverage = mediaCoverage!; var chosenSettings = settings!;
        using var client = new AnkiConnectClient(AnkiApiKey.Password);
        var journal = store.LoadCoverageDecisions<CreationAttempt>(imported.CollectionKey, "creation");
        SaveGeneratedMatches(imported.CollectionKey, journal);
        var progress = new Progress<string>(report);
        var plan = await Task.Run(() => NoteCreation.PrepareMedia(client, selectedCoverage, imported, chosenSettings, words, count, journal, progress, token,
            store.LoadCoverageDecisions<MatchDecision>(imported.CollectionKey, "matches")), token);
        token.ThrowIfCancellationRequested();
        CreationStatus.Text = $"Prepared {plan.Notes.Count:N0} media words for preview. No notes were created unless you choose Create.";
        await ReviewNewNotes(client, plan, selectedCoverage, imported, chosenSettings, journal);
    }
    private async void FindMediaMiningSentences(object sender, RoutedEventArgs e) => await MediaAction(async token =>
    {
        var reference = await RequireMediaReference();
        var python = MediaMiningPython.Text.Trim(); var processed = 0;
        foreach (var corpus in mediaLibrary.Where(c => c.Source == "Local text").ToList())
        {
            var text = await Task.Run(() => MediaStore.LoadText(corpus), token);
            if (text is null) continue;
            MediaMiningStatus.Text = "Analyzing local source sentences: " + corpus.Title;
            var analysis = await LocalMediaText.Analyze(text, python, token);
            var resolved = await Task.Run(() => LocalMediaText.Resolve(corpus.Title, corpus.MediaType, corpus.Edition, text, analysis, reference), token);
            token.ThrowIfCancellationRequested();
            await SaveMedia(corpus with { Words = resolved.Words, Analyzer = resolved.Analyzer, DictionaryVersion = resolved.DictionaryVersion });
            processed++;
        }
        RefreshMediaMining();
        MediaMiningStatus.Text += processed == 0 ? "\nNo cached local scripts are available. Import local text in Add to enable source sentences; downloaded vocabulary alone does not contain a script."
            : $"\nAnalyzed {processed:N0} local scripts. Only sentences with a resolved matching entry are attached. No script text was uploaded.";
    });
}
