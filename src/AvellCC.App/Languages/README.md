# Translations

Each file in this folder is one UI language. The file name is the language code
(`en.json`, `pt-BR.json`, `es.json`, `de.json`, ...).

To add a language:

1. Copy `en.json` to `<code>.json` (e.g. `es.json`).
2. Set `"_language"` to the language's own name (e.g. `"Español"`).
3. Translate the **values**. Do not change the keys.
4. Keep the `{0}`, `{1}`... placeholders: they are filled in by the app.
5. Drop the file next to the installed app (`C:\Program Files\AvellCC\App\Languages\`) or into this folder before building.
   It appears in **Settings → Language** without rebuilding.

Missing keys fall back to English, so a partial translation still works. A key that is missing everywhere
shows up as `[key.name]`, which makes it easy to spot.

`LanguageFilesTests` checks that every language file has exactly the same keys as `en.json`.
