using System.Windows;
using System.Windows.Controls;
using AnkiStats.Core;

namespace AnkiStats.App;

public partial class MainWindow
{
    private const string SentenceAnalysisPrompt = "Scans Japanese text and compares vocabulary in 日本語 and its subdecks when enabled.";
    private CancellationTokenSource? sentenceCancellation;
    private SentenceVocabulary? sentenceVocabulary;
    private string[]? sentenceSamples;
    private string? lastSentence;

    private void UpdateSentenceOverview()
    {
        sentenceSamples = null;
        var cards = importedData?.JapaneseSentenceCards;
        FindMissingSentenceWords.IsEnabled = cards is not null;
        SentenceCardCount.Text = cards?.Count.ToString("N0") ?? "—";
        SentenceCharacterCount.Text = cards?.Sum(c => (long)c.Characters).ToString("N0") ?? "—";
        SentenceScope.Text = cards is null ? "Refresh the collection to load Japanese Sentence fields into the saved results."
            : "Deck · 日本語 and subdecks   |   Characters include punctuation and exclude whitespace and furigana.";
        if (SentencesPage.Visibility == Visibility.Visible) ShowRandomSentence();
    }

    private void ShowRandomSentence()
    {
        if (importedData?.JapaneseSentenceCards is not { } cards)
        {
            RandomSentenceText.Text = "Refresh the collection to load sentences.";
            return;
        }
        sentenceSamples ??= SentenceAnalysis.Samples(cards);
        if (sentenceSamples.Length == 0)
        {
            RandomSentenceText.Text = "No Japanese sentence text in this deck tree.";
            return;
        }
        var previous = Array.IndexOf(sentenceSamples, lastSentence);
        var choices = sentenceSamples.Length;
        if (previous >= 0 && choices > 1) choices--;
        var index = Random.Shared.Next(choices);
        if (choices < sentenceSamples.Length && index >= previous) index++;
        RandomSentenceText.Text = lastSentence = sentenceSamples[index];
    }

    private void ResetSentenceAnalysis()
    {
        FindMissingSentenceWords.IsChecked = false;
        SentenceResults.Visibility = Visibility.Collapsed;
        SentenceWordGrid.ItemsSource = null;
        sentenceVocabulary = null;
        SentenceAnalysisStatus.Text = SentenceAnalysisPrompt;
    }

    private async void ToggleSentenceAnalysis(object sender, RoutedEventArgs e)
    {
        if (FindMissingSentenceWords.IsChecked != true)
        {
            ResetSentenceAnalysis();
            if (sentenceCancellation is not null)
            {
                sentenceCancellation.Cancel();
                FindMissingSentenceWords.IsEnabled = false;
                SentenceAnalysisStatus.Text = "Stopping analysis…";
            }
            return;
        }
        if (busy || settings is null)
        {
            FindMissingSentenceWords.IsChecked = false;
            SentenceAnalysisStatus.Text = "Wait for the current operation to finish, then try again.";
            return;
        }
        var sourceSettings = SentenceAnalysis.CollectionSettings(settings);
        var python = SentencePython.Text.Trim();
        await Guard(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            sentenceCancellation = cancellation;
            var token = cancellation.Token;
            try
            {
                if (python.Length == 0) throw new InvalidOperationException("Enter the Python executable with SudachiPy and SudachiDict-core installed.");
                if (vocabularyMatcher is null)
                {
                    var reference = await Task.Run(() => ReferenceStore.Load(), token)
                        ?? throw new InvalidOperationException("Install the dictionary data in Word Coverage to analyze sentences.");
                    vocabularyMatcher = await Task.Run(() => new VocabularyMatcher(reference), token);
                }
                var matcher = vocabularyMatcher;
                SentenceAnalysisStatus.Text = "Reading Sentence fields and vocabulary in 日本語 and its subdecks…";
                var collection = await ReaderFor(sourceSettings.ProfileFolder).ImportInventory(sourceSettings);
                token.ThrowIfCancellationRequested();
                var cards = collection.JapaneseSentenceCards ?? [];
                if (cards.Count == 0)
                {
                    ResetSentenceAnalysis();
                    SentenceAnalysisStatus.Text = "No Japanese sentence text in this deck tree.";
                    return;
                }
                SentenceAnalysisStatus.Text = $"Analyzing the text from {cards.Count:N0} cards locally with Sudachi…";
                var morphology = await SentenceAnalysis.Tokenize(cards, python, token);
                SentenceAnalysisStatus.Text = "Comparing words with the Anki vocabulary inventory…";
                var result = await Task.Run(() =>
                {
                    var inventory = matcher.Analyze(collection, store.LoadCoverageDecisions<MatchDecision>(collection.CollectionKey, "matches"),
                        aliases: LoadMappingAliases(collection.CollectionKey));
                    return SentenceAnalysis.Compare(morphology, matcher, inventory, token);
                }, token);
                token.ThrowIfCancellationRequested();
                sentenceVocabulary = result;
                SentenceResultFilter.SelectedIndex = 0;
                ShowSentenceResults();
                SentenceResults.Visibility = Visibility.Visible;
                SentenceAnalysisStatus.Text = $"{result.CommonOrJlpt.Count:N0} Common / JLPT missing · {result.Other.Count:N0} other missing · {result.Review.Count:N0} to review\n"
                    + $"{morphology.Analyzer} · JMdict {matcher.Reference.Metadata.DictionaryVersion}";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { SentenceAnalysisStatus.Text = SentenceAnalysisPrompt; }
            catch (Exception error)
            {
                ResetSentenceAnalysis();
                SentenceAnalysisStatus.Text = "Sentence analysis could not finish. " + error.Message;
            }
            finally
            {
                sentenceCancellation = null;
                FindMissingSentenceWords.IsEnabled = importedData?.JapaneseSentenceCards is not null;
            }
        });
    }

    private void SentenceResultFilterChanged(object sender, SelectionChangedEventArgs e) => ShowSentenceResults();

    private void ShowSentenceResults()
    {
        if (sentenceVocabulary is { } result)
            SentenceWordGrid.ItemsSource = SentenceResultFilter.SelectedIndex switch
            {
                0 => result.CommonOrJlpt,
                1 => result.Other,
                _ => result.Review
            };
    }
}
