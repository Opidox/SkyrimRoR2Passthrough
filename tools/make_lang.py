# Fabrique les fichiers de langue du plugin Skyrim : skse/lang/<langue>.txt (english, french, german...).
#
# Utilisation (depuis le dossier du projet) :  python tools/make_lang.py
#
# Les traductions sont dans tools/lang_data1.py, lang_data2.py et lang_data3.py. Le script verifie que chaque texte
# du code (Lang::T("..."), Lang::F("...")) a sa ligne, et que chaque traduction garde le meme nombre de {}.

import glob
import os
import re
import string
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from lang_data1 import T1  # noqa: E402
from lang_data2 import T2  # noqa: E402
from lang_data3 import T3, GODS_RU  # noqa: E402

LANGUAGES = ["english", "french", "german", "italian", "spanish", "polish", "russian", "japanese", "chinese"]
ROOT = os.path.normpath(os.path.join(HERE, ".."))


def code_keys():
    keys = set()
    pattern = re.compile(r'(?:Lang::T|Lang::F|Notify)\(\s*"((?:[^"\\]|\\.)*)"')
    for path in glob.glob(os.path.join(ROOT, "skse", "src", "*.cpp")):
        text = open(path, encoding="utf-8").read()
        for match in pattern.finditer(text):
            keys.add(match.group(1).encode("utf-8").decode("unicode_escape").encode("latin-1").decode("utf-8"))
    return keys


def placeholders(text):
    count = 0
    for _, field, _, _ in string.Formatter().parse(text):
        if field is not None:
            count += 1
    return count


def escape(text):
    return text.replace("\\", "\\\\").replace("\n", "\\n")


def main():
    rows = T1 + T2 + T3
    errors = 0
    seen = set()
    for row in rows:
        key = row[0]
        if len(row) != 1 + len(LANGUAGES):
            print("Ligne incomplete :", key)
            errors += 1
            continue
        if key in seen:
            print("En double :", key)
            errors += 1
        seen.add(key)
        wanted = placeholders(key)
        for language, text in zip(LANGUAGES, row[1:]):
            if placeholders(text) != wanted:
                print("Nombre de {} different (%s) : %r -> %r" % (language, key, text))
                errors += 1
            if " = " in key:
                print("La cle contient ' = ' :", key)
                errors += 1

    missing = sorted(code_keys() - seen)
    for key in missing:
        print("Pas de traduction pour :", repr(key))
    errors += len(missing)
    if errors:
        print("%d erreur(s) : rien n'est ecrit." % errors)
        sys.exit(1)

    out = os.path.join(ROOT, "skse", "lang")
    os.makedirs(out, exist_ok=True)
    for index, language in enumerate(LANGUAGES):
        lines = [
            "# SkyrimRoR2 - %s" % language,
            "# One line per text:  original text = translation   (keep the {} and the spaces around '=').",
            "# \\n is a line break. A missing line falls back to English.",
            "",
        ]
        for row in rows:
            lines.append("%s = %s" % (escape(row[0]), escape(row[1 + index])))
        if language == "russian":
            lines.append("")
            lines.append("# Shrine names (part of the god's name as written on the shrine)")
            for god, local in GODS_RU.items():
                lines.append("god:%s = %s" % (god, local))
        with open(os.path.join(out, language + ".txt"), "w", encoding="utf-8", newline="\r\n") as f:
            f.write("\n".join(lines) + "\n")
    print("Ecrit : %s (%d langues, %d textes)" % (out, len(LANGUAGES), len(rows)))


if __name__ == "__main__":
    main()
