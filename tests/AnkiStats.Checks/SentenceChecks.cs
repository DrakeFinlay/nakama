using System.Text.Json;
using AnkiStats.Core;

internal static class SentenceChecks
{
    public static void Run(Action<bool, string> check, string sandbox)
    {
        var root = Path.Combine(sandbox, "sentences");
        CoverageUiFixture.Create(root);
        var store = new LocalStore(root);
        var settings = store.LoadSettings()!;
        var source = store.LoadCache(settings.CollectionKey)!;
        check(source.JapaneseSentenceCards is { Count: 2 } && source.JapaneseSentenceCards.Sum(c => c.Characters) == 22,
            "snapshot excludes English-only fields and unrelated decks and counts visible characters");
        check(SentenceAnalysis.Samples(source.JapaneseSentenceCards!).ToHashSet().SetEquals(["猫がいる。", "私は本を読む。", "猫もいる！", "犬もいる。"]),
            "random samples strip markup and furigana and split sentences");
        check(SentenceAnalysis.CountCharacters("👩‍👩‍👧‍👧　𠮷\t。\u200b") == 3, "character count uses visible Unicode graphemes");
        check(JsonSerializer.Deserialize<ImportedCollection>("""{"SentenceCards":[]}""")?.JapaneseSentenceCards is null,
            "older caches require a refresh for the fixed Japanese sentence scope");
        check(SentenceAnalysis.ContainsJapanese("𠮷") && SentenceAnalysis.ContainsJapanese("ｶﾅ")
            && !SentenceAnalysis.ContainsJapanese("Ｔｈｅ café 123"),
            "Japanese script detection accepts supplementary kanji and halfwidth kana and rejects Latin text");
        settings.Profiles = [new() { DeckRules = [new("English", true)] }];
        var outside = new CollectionImporter(store.TemporaryRoot).Import(settings);
        check(outside.Cards is [{ Deck: "English" }] && outside.JapaneseSentenceCards?.Count == 2,
            "fixed sentence scope includes Japanese subdecks independently of the saved stats profile");
        var inventoryReader = new CollectionReader(store.TemporaryRoot, () => throw new InvalidOperationException(), allowLive: false);
        var scoped = inventoryReader.ImportInventory(SentenceAnalysis.CollectionSettings(settings)).GetAwaiter().GetResult();
        check(scoped.Cards.All(c => SentenceAnalysis.IncludesDeck(c.Deck)) && !scoped.Notes.Any(n => n.Expression == "犬")
            && scoped.Reviews.Count == 0 && settings.Profiles.Count == 1,
            "sentence inventory ignores vocabulary cards outside the Japanese deck tree");
        settings.Mappings.Clear();
        var ignored = new CollectionImporter(store.TemporaryRoot).Import(settings);
        check(ignored.Cards.Count == 0 && ignored.JapaneseSentenceCards?.Count == 2, "sentence import works for unmapped types independently of vocabulary");

        DictionaryEntry Entry(string id, string form, string reading) => new(id, [form], [new(reading, ["*"])], id, false, []);
        var reference = new CoverageReference(new("test", "", "", 0, ""),
            [Entry("eat", "食べる", "たべる"), Entry("cat", "猫", "ねこ"), Entry("rain", "雨", "あめ"),
                Entry("candy", "飴", "あめ"), Entry("dog", "犬", "いぬ") with { Common = true },
                Entry("mountain", "山", "やま") with { Levels = ["N5"] }, Entry("the", "ザ", "ざ"),
                Entry("apricot", "杏子", "あんず"), Entry("emergency", "有事", "ゆうじ"), Entry("car", "カー", "カー")]);
        var matcher = new VocabularyMatcher(reference);
        var inventory = matcher.Analyze(new ImportedCollection
        {
            Notes = [new(1, "猫", "ねこ", "", "Vocabulary", "Word"), new(2, "あめ", "あめ", "", "Vocabulary", "Word"),
                new(3, "架空装具", "かくうそうぐ", "", "Vocabulary", "Word")],
            Cards = [new(11, 1, "Suspended", -1, 2, 0), new(12, 2, "Other", 2, 2, 0), new(13, 3, "Other", 2, 2, 0)]
        });
        var result = SentenceAnalysis.Compare(new MorphologyOutput("synthetic",
        [
            new("食べた", "食べる", "食べる", "タベタ", "動詞", 3, "食べた。", LemmaReading: "タベル"),
            new("食べる", "食べる", "食べる", "タベル", "動詞", 1, "食べる。"),
            new("猫", "猫", "猫", "ネコ", "名詞", 2),
            new("雨", "雨", "雨", "アメ", "名詞", 1),
            new("あめ", "あめ", "あめ", "アメ", "名詞", 1),
            new("架空語", "架空語", "架空語", "", "名詞", 1),
            new("the", "the", "ザ", "ザ", "名詞", 50, "the cat"),
            new("犬", "犬", "犬", "イヌ", "名詞", 2),
            new("山", "山", "山", "ヤマ", "名詞", 1)
        ]), matcher, inventory);
        check(result.Other is [{ EntryId: "eat", Occurrences: 4, Example: "食べた。" }],
            "other missing vocabulary merges inflections, skips Latin tokens, and excludes suspended inventory");
        check(result.CommonOrJlpt is [{ EntryId: "dog" }, { EntryId: "mountain" }],
            "common-only and JLPT-only entries share a separate missing vocabulary list");
        check(result.Review.Count == 3 && result.Review.Select(w => w.Status).ToHashSet().SetEquals(["Possible Anki match", "Ambiguous dictionary match", "No exact dictionary match"]),
            "review separates uncertain Anki matches, ambiguous forms, and unmatched tokens");

        var uncertain = SentenceAnalysis.Compare(new("synthetic",
        [
            new("ユウジ", "ユウジ", "有事", "ユウジ", "名詞", 1),
            new("かー", "かー", "か", "カー", "助詞", 1),
            new("杏子", "杏子", "杏子", "アンズ", "名詞", 1, ReviewReason: "Possible personal name"),
            new("食べた", "食べる", "食べる", "タベタ", "動詞", 1, LemmaReading: "モノ"),
            new("架空装具", "架空装具", "架空装具", "", "複合語", 1, IsCompound: true),
            new("未知装具", "未知装具", "未知装具", "", "複合語", 1, IsCompound: true)
        ]), matcher, inventory);
        check(uncertain.CommonOrJlpt.Count == 0 && uncertain.Other.Count == 0,
            "sentence lookup requires exact orthography and base reading, and does not promote possible names");
        check(uncertain.Review.Any(w => w.Word == "杏子" && w.Status == "Possible personal name")
            && uncertain.Review.Any(w => w.Word == "未知装具" && w.Status == "Unresolved compound")
            && uncertain.Review.All(w => w.Word != "架空装具"),
            "unresolved compounds remain whole and exact existing custom forms are accounted for");
    }

    public static async Task RunTokenizer(Action<bool, string> check, CoverageReference reference, string python)
    {
        var examples = new[]
        {
            "ユウジが帰った。", "有事に備える。", "杏子が笑った。", "杏を食べた。", "宝具を選んだ。", "海で宝を探した。",
            "何かないかー？", "レーシングカーではカーの性能が重要だ。", "学校生活を楽しんだ。", "サーヴァントを呼んだ。"
        };
        var cards = examples.Select((text, index) => new SentenceCard(index, index, "日本語", text, text.Length));
        var analysis = await SentenceAnalysis.Tokenize(cards, python, CancellationToken.None);
        check(analysis.Tokens.All(t => t.Surface is not ("ユウジ" or "かー" or "具"))
            && analysis.Tokens.Any(t => t.Surface == "宝具" && t.IsCompound)
            && analysis.Tokens.Single(t => t.Surface == "宝").Sentence == "海で宝を探した。",
            "Sudachi skips definite names and elongated particles and preserves the boundaries of compounds");
        check(analysis.Tokens.Any(t => t.Surface == "杏子" && t.ReviewReason == "Possible personal name")
            && analysis.Tokens.Any(t => t.Surface == "食べ" && t.LemmaReading == "タベル"),
            "Sudachi flags name alternatives and supplies the dictionary-form reading of inflected words");

        var matcher = new VocabularyMatcher(reference);
        var inventory = matcher.Analyze(new ImportedCollection
        {
            Notes = [new(1, "宝具", "ほうぐ", "treasured item", "Vocabulary", "Word")],
            Cards = [new(1, 1, "日本語", 2, 2, 0)]
        });
        var result = SentenceAnalysis.Compare(analysis, matcher, inventory);
        var missing = result.CommonOrJlpt.Concat(result.Other).ToDictionary(w => w.EntryId!);
        var expected = new Dictionary<string, string>
        {
            ["1745430"] = "有事に備える。", ["1586930"] = "杏を食べた。", ["1516160"] = "海で宝を探した。",
            ["1036170"] = "レーシングカーではカーの性能が重要だ。", ["1932030"] = "学校生活を楽しんだ。",
            ["1054990"] = "サーヴァントを呼んだ。"
        };
        check(expected.All(pair => missing.TryGetValue(pair.Key, out var word) && word.Occurrences == 1 && word.Example == pair.Value)
            && !missing.ContainsKey("2872054"),
            "installed JMdict recognizes genuine uses while the scoped Anki inventory suppresses the whole known compound");
    }
}
