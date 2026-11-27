// Copyright (c) 2026 Anu. All rights reserved.
// RSM (Reforger Server Manager) — 무단 복제·수정·배포를 금합니다.

using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>Guesses from a mod's name whether it is a base / convenience / system mod (ACE, admin tools,
/// radio, UI, loadout editor, translation …) that every kind of game needs, so it belongs in every category.
/// Only a default: the user can switch it either way on the mods page and that choice is kept.</summary>
public static class ModCategoryDetector
{
    // whole words (case-insensitive) that mark a utility mod. Kept narrow on purpose: words like "Core",
    // "Map" or "Sound" also appear in content packs (WCS_Core, a New York map, a Vietnam sound pack).
    private static readonly string[] Words =
    {
        "ACE", "ACEX", "Admin", "Tools", "Tool", "Utility", "Utilities", "Utils", "Util",
        "Framework", "Lib", "Library", "UI", "HUD", "Interface", "QoL",
        "Radio", "Radios", "ACRE", "TFAR", "VON", "Compass", "Nametag", "Nametags", "Marker", "Markers",
        "Loadout", "Arsenal", "Inventory", "Spectator", "Camera", "Zeus", "GME",
        "Persistence", "Persistent", "Keybind", "Keybinds", "Fix", "Fixes", "Bugfix", "Performance",
        "Medical", "Medic", "Localization", "Localisation", "Translation", "Korean", "KOR",
    };

    // phrases / Korean words that can sit anywhere in the name
    private static readonly string[] Phrases =
    {
        "Name Tag", "Quality of Life", "Better Sounds", "BetterSounds", "Server Admin", "Game Master Enhanced",
        "한국어", "한글", "번역", "편의", "시스템", "유틸",
    };

    private static readonly HashSet<string> WordSet = new(Words, StringComparer.OrdinalIgnoreCase);

    /// <summary>The word that made the guess (for the tooltip), or null when the mod does not look like a system mod.</summary>
    public static string? SystemKeyword(params string?[] names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            foreach (var phrase in Phrases)
                if (name.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    return phrase;
            foreach (var word in SplitWords(name))
                if (WordSet.Contains(word))
                    return Words.First(w => w.Equals(word, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public static bool LooksLikeSystemMod(params string?[] names) => SystemKeyword(names) != null;

    /// <summary>"ACE_Medical-Core", "BetterUI", "RHS - Status Quo" → words. Splits on anything that is not a
    /// letter or digit and on lower→Upper steps ("BetterUI" → Better, UI), keeping runs of capitals whole.</summary>
    internal static IEnumerable<string> SplitWords(string name)
    {
        foreach (var chunk in Regex.Split(name, @"[^\p{L}\p{Nd}]+"))
        {
            if (chunk.Length == 0)
                continue;
            yield return chunk;
            var parts = Regex.Split(chunk, @"(?<=\p{Ll})(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})|(?<=\p{L})(?=\p{Nd})|(?<=\p{Nd})(?=\p{L})");
            if (parts.Length > 1)
                foreach (var p in parts)
                    if (p.Length > 0)
                        yield return p;
        }
    }
}
