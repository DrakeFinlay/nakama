using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnkiStats.Core;

public sealed record SentenceCard(long CardId, long NoteId, string Deck, string Text, int Characters);
public sealed record SentenceWord(string? EntryId, string Word, string Reading, string Meaning, string Lists,
    long Occurrences, string Example, string Status);
public sealed record SentenceVocabulary(List<SentenceWord> CommonOrJlpt, List<SentenceWord> Other, List<SentenceWord> Review);

public static partial class SentenceAnalysis
{
    public const string DeckName = "日本語";

    public static bool IncludesDeck(string deck) => LearningAnalyzer.Under(deck, DeckName);

    public static Settings CollectionSettings(Settings current)
    {
        var scope = DeckProfiles.Copy(current);
        scope.Profiles = [];
        scope.ActiveProfileId = "";
        scope.DeckRules = [new(DeckName, true)];
        return scope;
    }

    public static bool ContainsJapanese(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is >= 0x3041 and <= 0x3096 or >= 0x309D and <= 0x309F
                or >= 0x30A1 and <= 0x30FA or >= 0x30FD and <= 0x30FF or >= 0x31F0 and <= 0x31FF
                or >= 0xFF66 and <= 0xFF6F or >= 0xFF71 and <= 0xFF9D
                or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
                or >= 0x20000 and <= 0x323AF or 0x3005 or 0x3007)
                return true;
        return false;
    }

    [GeneratedRegex("""[^。！？!?\r\n]+[。！？!?]*[」』”"]?|[。！？!?]+""")]
    private static partial Regex SentencePattern();

    public static int CountCharacters(string text)
    {
        var count = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            foreach (var rune in elements.GetTextElement().EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control)
                    continue;
                count++;
                break;
            }
        return count;
    }

    public static string[] Samples(IEnumerable<SentenceCard> cards) => cards.Select(c => c.Text)
        .Distinct(StringComparer.Ordinal).SelectMany(text => SentencePattern().Matches(text).Select(match => match.Value.Trim()))
        .Where(ContainsJapanese).Distinct(StringComparer.Ordinal).ToArray();

    public static async Task<MorphologyOutput> Tokenize(IEnumerable<SentenceCard> cards, string python, CancellationToken token)
    {
        // Sibling cards share a single stored field. Repeated text on separate notes contributes separate occurrences.
        var text = await Task.Run(() => string.Join('\n', cards.DistinctBy(c => c.NoteId).Select(c => c.Text)), token);
        return await LocalMediaText.Analyze(text, python, token, japaneseOnly: true);
    }

    public static SentenceVocabulary Compare(MorphologyOutput analysis, VocabularyMatcher matcher, CoverageAnalysis inventory,
        CancellationToken token = default)
    {
        var possible = inventory.Matches.SelectMany(m => m.ReviewEntryIds).ToHashSet();
        var existingForms = inventory.Matches.Where(m => m.Status != "Excluded")
            .Select(m => m.Expression.Normalize(NormalizationForm.FormKC)).ToHashSet(StringComparer.Ordinal);
        var rows = new List<SentenceWord>();
        foreach (var word in analysis.Tokens)
        {
            token.ThrowIfCancellationRequested();
            if (!ContainsJapanese(word.Surface)) continue;
            if (word.ReviewReason.Length > 0)
            {
                rows.Add(new(null, word.Surface, word.Reading, "", "", word.Count, word.Sentence, word.ReviewReason));
                continue;
            }
            // Use the base form's reading for inflections. A normalized spelling can be a different word.
            var reading = word.LemmaReading.Length > 0 ? word.LemmaReading : word.Surface == word.Lemma ? word.Reading : "";
            var matches = matcher.Find(word.Lemma, reading, exactOnly: true);
            if (matches is not [var entry])
            {
                if (matches.Count == 0 && word.IsCompound && existingForms.Contains(word.Lemma.Normalize(NormalizationForm.FormKC))) continue;
                var status = matches.Count > 0 ? "Ambiguous dictionary match" : word.IsCompound ? "Unresolved compound" : "No exact dictionary match";
                rows.Add(new(null, word.Lemma, reading, "", "", word.Count, word.Sentence, status));
                continue;
            }
            if (inventory.Database.Contains(entry.Id)) continue;
            rows.Add(new(entry.Id, entry.StudyForm?.Written ?? entry.Spellings.FirstOrDefault() ?? entry.Readings[0].Text,
                entry.StudyForm?.Reading ?? entry.Readings[0].Text, entry.Meaning,
                string.Join(" / ", (entry.Common ? new[] { "Common" } : []).Concat(entry.Levels)), word.Count, word.Sentence,
                possible.Contains(entry.Id) ? "Possible Anki match" : "Missing"));
        }
        var grouped = rows.GroupBy(w => (w.EntryId, Word: w.EntryId is null ? w.Word : "", Reading: w.EntryId is null ? w.Reading : "", w.Status))
            .Select(g => g.First() with { Occurrences = g.Sum(w => w.Occurrences), Example = g.FirstOrDefault(w => w.Example.Length > 0)?.Example ?? "" })
            .OrderByDescending(w => w.Occurrences).ThenBy(w => w.Word, StringComparer.Ordinal).ToList();
        return new(grouped.Where(w => w.Status == "Missing" && w.Lists.Length > 0).ToList(),
            grouped.Where(w => w.Status == "Missing" && w.Lists.Length == 0).ToList(),
            grouped.Where(w => w.Status != "Missing").ToList());
    }
}
