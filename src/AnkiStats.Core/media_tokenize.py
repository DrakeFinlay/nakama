"""Local stdin -> aggregated Sudachi morphemes. No network or source-file access."""
import collections
import functools
import importlib.metadata
import json
import re
import sys

from sudachipy import dictionary, tokenizer

japanese_only = "--japanese-only" in sys.argv[1:]
sudachi = dictionary.Dictionary(dict="core")
analyzer = sudachi.create()
kanji_chars = r"\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U000323af々〇"
katakana_chars = r"\u30a1-\u30fa\u30fd-\u30ff\u31f0-\u31ff\uff66-\uff6f\uff71-\uff9d"
japanese = re.compile(r"[\u3041-\u3096\u309d-\u309f" + kanji_chars + katakana_chars + "]")
kanji = re.compile("[" + kanji_chars + "]+")
katakana = re.compile("[" + katakana_chars + "ーｰﾞﾟ]+")
ignored_pos = ("補助記号", "記号", "空白")


def token_fields(word):
    return (word.surface(), word.dictionary_form(), word.normalized_form(), word.reading_form(),
            "/".join(word.part_of_speech()))


def compound_script(word):
    pos = word.part_of_speech()
    if pos[0] not in ("名詞", "接頭辞", "接尾辞") or pos[1] == "数詞":
        return None
    if kanji.fullmatch(word.surface()):
        return kanji
    if katakana.fullmatch(word.surface()):
        return katakana
    return None


@functools.cache
def personal_names(surface):
    return tuple((entry.part_of_speech()[3], entry.reading_form()) for entry in sudachi.lookup(surface)
                 if entry.part_of_speech()[1:3] == ("固有名詞", "人名"))


def possible_name(word, following):
    if word.part_of_speech()[0] != "名詞":
        return False
    names = personal_names(word.surface())
    if not names:
        return False
    given_names = [reading for kind, reading in names if kind == "名"]
    if len(word.surface()) > 1 and any(reading != word.reading_form() for reading in given_names):
        return True
    if following is None or word.end() != following.begin():
        return False
    return (following.surface() in ("「", "『")
            or (following.part_of_speech()[0] == "接尾辞"
                and following.surface() in ("さん", "ちゃん", "くん", "君", "さま", "様", "氏"))
            or (given_names and following.part_of_speech()[0] == "助詞"
                and following.normalized_form() in ("が", "は", "も")))


def sentence_tokens(words):
    index = 0
    while index < len(words):
        word = words[index]
        end = index + 1
        script = compound_script(word)
        while (script is not None and end < len(words) and words[end - 1].end() == words[end].begin()
               and compound_script(words[end]) is script):
            end += 1
        if end > index + 1:
            # Hold adjacent noun/affix pieces together. JMdict can validate the whole compound.
            parts = [words[i] for i in range(index, end)]
            index = end
            if not any(part.part_of_speech()[1] == "固有名詞" for part in parts):
                surface = "".join(part.surface() for part in parts)
                yield (surface, surface, surface, "", "複合語", "", "", True)
            continue

        index = end
        surface = word.surface()
        pos = word.part_of_speech()
        if (pos[0] in ignored_pos or pos[1] == "固有名詞" or not japanese.search(surface)
                or (pos[0] in ("助詞", "助動詞") and surface.endswith(("ー", "ｰ")))):
            continue
        following = words[index] if index < len(words) else None
        review = "Possible personal name" if possible_name(word, following) else ""
        base = word.dictionary_form_morpheme()
        yield (*token_fields(word), base.reading_form() if base is not None else "", review, False)


counts = collections.Counter()
examples = {}
for line in sys.stdin:
    # Bound individual tokenizer input while preserving normal lines and sentences.
    for sentence in re.findall(r'[^。！？!?]+[。！？!?]*[」』”"]?', line):
        sentence = sentence.strip()
        text = sentence
        if japanese_only:
            if not japanese.search(sentence):
                continue
            # Leave a boundary where foreign letters occurred, while keeping the original example.
            text = "".join(char if not char.isalpha() or char in "ーｰﾞﾟ" or japanese.fullmatch(char) else " "
                           for char in sentence)
        for start in range(0, len(text), 8000):
            words = analyzer.tokenize(text[start:start + 8000], tokenizer.Tokenizer.SplitMode.C)
            tokens = sentence_tokens(words) if japanese_only else (
                (*token_fields(word), "", "", False) for word in words if word.part_of_speech()[0] not in ignored_pos)
            for key in tokens:
                counts[key] += 1
                if len(sentence) <= 500:
                    examples.setdefault(key, sentence)
json.dump({"analyzer": "SudachiPy " + importlib.metadata.version("SudachiPy")
           + "; SudachiDict-core " + importlib.metadata.version("SudachiDict-core") + "; split C; helper v2; source sentences"
           + ("; Japanese sentence lookup v2" if japanese_only else ""),
           "tokens": [dict(zip(("surface", "lemma", "normalized", "reading", "pos", "lemmaReading", "reviewReason", "isCompound",
                                "count", "sentence"), key + (count, examples.get(key, ""))))
                      for key, count in counts.items()]}, sys.stdout, ensure_ascii=False)
