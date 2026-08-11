using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ModernAtlas;

internal enum AtlasSearchAliasKind
{
    Block,
    Entity,
    Item
}

/// <summary>
/// Resolves search-only aliases through private translation services. They are
/// isolated from Vintage Story's global translation diagnostics, which the
/// survival handbook can mutate from a worker thread during world startup.
/// </summary>
internal sealed class AtlasSearchLanguageIndex
{
    private const int MinimumAutomatedQueryLength = 2;
    private static readonly object[] WildcardFormatArguments = CreateWildcardFormatArguments();

    private readonly ICoreClientAPI capi;
    private readonly ITranslationService? englishTranslations;
    private readonly ITranslationService? activeTranslations;
    private readonly Dictionary<AliasCacheKey, SearchAlias[]> aliasCache = new();
    private readonly Dictionary<string, SearchAlias[]> attributeBackedBlockAliasCache = new(
        StringComparer.Ordinal
    );
    private readonly SearchTranslationEntry[] blockTranslationEntries;
    private readonly SearchTranslationEntry[] wildcardBlockTranslationEntries;
    private readonly HashSet<string> matchingBlockCodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> matchingBlockPatterns = new(StringComparer.Ordinal);
    private int blockTranslationEntryIndex;

    public string ActiveLanguageCode { get; }
    public string LanguageSummary => string.Equals(
        ActiveLanguageCode,
        "en",
        StringComparison.OrdinalIgnoreCase
    )
        ? "ENG"
        : $"ENG + {ActiveLanguageCode.ToUpperInvariant()}";

    public bool Ready => englishTranslations != null
        && (string.Equals(ActiveLanguageCode, "en", StringComparison.OrdinalIgnoreCase)
            || activeTranslations != null);

    public AtlasSearchLanguageIndex(ICoreClientAPI capi)
    {
        this.capi = capi;
        ActiveLanguageCode = string.IsNullOrWhiteSpace(Lang.CurrentLocale)
            ? "en"
            : Lang.CurrentLocale.Trim().ToLowerInvariant();

        long started = Stopwatch.GetTimestamp();
        englishTranslations = LoadPrivateTranslations("en");
        activeTranslations = string.Equals(
            ActiveLanguageCode,
            "en",
            StringComparison.OrdinalIgnoreCase
        )
            ? englishTranslations
            : LoadPrivateTranslations(ActiveLanguageCode);
        blockTranslationEntries = BuildBlockTranslationEntries();
        wildcardBlockTranslationEntries = BuildWildcardBlockTranslationEntries(
            blockTranslationEntries
        );

        capi.Logger.Notification(
            "[ModernAtlas] Prepared isolated atlas search languages {0} with {1} block-name aliases in {2:0} ms.",
            LanguageSummary,
            blockTranslationEntries.Length,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds
        );
    }

    public void BeginBlockQuery()
    {
        matchingBlockCodes.Clear();
        matchingBlockPatterns.Clear();
        blockTranslationEntryIndex = 0;
    }

    public bool ResolveBlockQueryStep(string normalizedQuery, string compactQuery)
    {
        if (blockTranslationEntryIndex >= blockTranslationEntries.Length)
        {
            return false;
        }

        SearchTranslationEntry entry =
            blockTranslationEntries[blockTranslationEntryIndex++];
        if (!entry.Normalized.Contains(normalizedQuery, StringComparison.Ordinal)
            && (compactQuery.Length == 0
                || !entry.Compact.Contains(compactQuery, StringComparison.Ordinal)))
        {
            return true;
        }

        if (entry.CodePattern.Contains('*', StringComparison.Ordinal))
        {
            matchingBlockPatterns.Add(entry.CodePattern);
        }
        else
        {
            matchingBlockCodes.Add(entry.CodePattern);
        }
        return true;
    }

    public bool MatchesPreparedBlock(AssetLocation? code)
    {
        if (code == null) return false;
        string candidate = code.ToString();
        if (matchingBlockCodes.Contains(candidate)) return true;

        foreach (string pattern in matchingBlockPatterns)
        {
            if (MatchesGlobOrAttributeBacked(candidate, pattern)) return true;
        }
        return false;
    }

    public bool Matches(
        AssetLocation? code,
        AtlasSearchAliasKind kind,
        string normalizedQuery,
        string compactQuery
    )
    {
        if (code == null || normalizedQuery.Length == 0) return false;

        foreach (SearchAlias alias in GetAliases(code, kind))
        {
            if (alias.Normalized.Contains(normalizedQuery, StringComparison.Ordinal)
                || compactQuery.Length > 0
                    && alias.Compact.Contains(compactQuery, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public bool MatchesBlock(
        Block? block,
        string normalizedQuery,
        string compactQuery
    )
    {
        if (block?.Code == null || normalizedQuery.Length == 0) return false;
        foreach (SearchAlias alias in GetAttributeBackedBlockAliases(block))
        {
            if (alias.Normalized.Contains(normalizedQuery, StringComparison.Ordinal)
                || compactQuery.Length > 0
                    && alias.Compact.Contains(compactQuery, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public bool MatchesBlockVariantSuffix(
        AssetLocation? code,
        string? variantSuffix,
        string normalizedQuery,
        string compactQuery
    )
    {
        if (code == null) return false;

        var attributedCode = string.IsNullOrWhiteSpace(variantSuffix)
            ? code
            : new AssetLocation(code.Domain, $"{code.Path}-{variantSuffix.Trim()}");
        return Matches(
            attributedCode,
            AtlasSearchAliasKind.Block,
            normalizedQuery,
            compactQuery
        );
    }

    internal bool TryGetActiveBlockVariantQuery(
        AssetLocation? code,
        string? variantSuffix,
        out string query
    )
    {
        query = "";
        if (code == null) return false;

        var attributedCode = string.IsNullOrWhiteSpace(variantSuffix)
            ? code
            : new AssetLocation(code.Domain, $"{code.Path}-{variantSuffix.Trim()}");
        string candidate = attributedCode.ToString();
        var englishTokens = new HashSet<string>(StringComparer.Ordinal);
        CollectMatchingTranslationTokens(
            englishTranslations,
            candidate,
            englishTokens
        );

        if (activeTranslations == null) return false;
        foreach ((string translationKey, string translated) in activeTranslations.GetAllEntries())
        {
            if (!TryGetBlockCodePattern(translationKey, out string codePattern)
                || !codePattern.Contains('*', StringComparison.Ordinal)
                || !MatchesGlobOrAttributeBacked(candidate, codePattern))
            {
                continue;
            }

            string normalized = Normalize(translated);
            foreach (string token in normalized.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries
            ))
            {
                if (token.Length < MinimumAutomatedQueryLength
                    || englishTokens.Contains(token))
                {
                    continue;
                }

                query = token;
                return true;
            }

            if (normalized.Length >= MinimumAutomatedQueryLength)
            {
                query = normalized;
                return true;
            }
        }
        return false;
    }

    internal bool ValidateForAutomatedTest(out string diagnostic)
    {
        if (!Ready)
        {
            diagnostic = $"services unavailable ({LanguageSummary})";
            return false;
        }

        AssetLocation knownBlock = new("game:packeddirt");
        SearchAlias[] aliases = GetAliases(knownBlock, AtlasSearchAliasKind.Block);
        string englishProbe = Normalize(GetTranslation(
            englishTranslations,
            "game:block-packeddirt"
        ));
        string activeProbe = Normalize(GetTranslation(
            activeTranslations,
            "game:block-packeddirt"
        ));
        bool englishFound = englishProbe.Length > 0
            && Matches(
                knownBlock,
                AtlasSearchAliasKind.Block,
                englishProbe,
                Compact(englishProbe)
            );
        bool activeFound = string.Equals(
            ActiveLanguageCode,
            "en",
            StringComparison.OrdinalIgnoreCase
        ) || activeProbe.Length > 0
            && Matches(
                knownBlock,
                AtlasSearchAliasKind.Block,
                activeProbe,
                Compact(activeProbe)
            );
        bool englishIndexed = MatchesPreparedBlockQuery(knownBlock, englishProbe);
        bool activeIndexed = string.Equals(
            ActiveLanguageCode,
            "en",
            StringComparison.OrdinalIgnoreCase
        ) || MatchesPreparedBlockQuery(knownBlock, activeProbe);

        AssetLocation wildcardBlock = new("game:lantern-small-up");
        const string wildcardMaterial = "copper";
        string wildcardKey = "game:block-lantern-small-*-copper";
        string wildcardEnglishProbe = Normalize(GetTranslation(
            englishTranslations,
            wildcardKey
        ));
        string wildcardActiveProbe = Normalize(GetTranslation(
            activeTranslations,
            wildcardKey
        ));
        Block? registeredWildcardBlock = capi.World.GetBlock(wildcardBlock);
        bool wildcardEnglishFound = wildcardEnglishProbe.Length > 0
            && MatchesBlock(
                registeredWildcardBlock,
                wildcardEnglishProbe,
                Compact(wildcardEnglishProbe)
            );
        bool wildcardActiveFound = string.Equals(
            ActiveLanguageCode,
            "en",
            StringComparison.OrdinalIgnoreCase
        ) || wildcardActiveProbe.Length > 0
            && MatchesBlock(
                registeredWildcardBlock,
                wildcardActiveProbe,
                Compact(wildcardActiveProbe)
            );
        BeginBlockQuery();

        int lanternPatternCount = 0;
        string sampleLanternPattern = "none";
        bool sampleLanternPatternMatches = false;
        foreach (SearchTranslationEntry entry in wildcardBlockTranslationEntries)
        {
            if (!entry.CodePattern.Contains("lantern", StringComparison.Ordinal)) continue;
            lanternPatternCount++;
            if (!string.Equals(sampleLanternPattern, "none", StringComparison.Ordinal))
            {
                continue;
            }
            sampleLanternPattern = entry.CodePattern;
            sampleLanternPatternMatches = MatchesGlobOrAttributeBacked(
                wildcardBlock.ToString(),
                entry.CodePattern
            );
        }
        diagnostic = $"{LanguageSummary}; aliases={aliases.Length}; attributeBackedAliases={(registeredWildcardBlock == null ? 0 : GetAttributeBackedBlockAliases(registeredWildcardBlock).Length)}; candidate={wildcardBlock}; lanternPatterns={lanternPatternCount}; samplePattern={sampleLanternPattern}; sampleMatches={sampleLanternPatternMatches}; materialFixture={wildcardMaterial}; indexed={blockTranslationEntries.Length}";
        return englishFound
            && activeFound
            && englishIndexed
            && activeIndexed
            && wildcardEnglishFound
            && wildcardActiveFound;
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        bool insideMarkup = false;
        foreach (char character in decomposed)
        {
            if (character == '<')
            {
                insideMarkup = true;
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (insideMarkup)
            {
                if (character == '>') insideMarkup = false;
                continue;
            }
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = builder.Length > 0;
            }
        }

        return builder.ToString().Trim();
    }

    internal static string Compact(string value) => value.Replace(" ", "", StringComparison.Ordinal);

    private SearchAlias[] GetAliases(AssetLocation code, AtlasSearchAliasKind kind)
    {
        var cacheKey = new AliasCacheKey(code.Domain, code.Path, kind);
        if (aliasCache.TryGetValue(cacheKey, out SearchAlias[]? cached)) return cached;

        var aliases = new List<SearchAlias>(8);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string translationKey in GetTranslationKeys(code, kind))
        {
            AddAlias(aliases, unique, GetTranslation(englishTranslations, translationKey));
            if (!ReferenceEquals(activeTranslations, englishTranslations))
            {
                AddAlias(aliases, unique, GetTranslation(activeTranslations, translationKey));
            }
        }
        if (kind == AtlasSearchAliasKind.Block)
        {
            string candidate = code.ToString();
            foreach (SearchTranslationEntry entry in wildcardBlockTranslationEntries)
            {
                if (MatchesGlobOrAttributeBacked(candidate, entry.CodePattern))
                {
                    AddAlias(aliases, unique, entry.Normalized);
                }
            }
        }

        SearchAlias[] resolved = aliases.ToArray();
        aliasCache[cacheKey] = resolved;
        return resolved;
    }

    private SearchAlias[] GetAttributeBackedBlockAliases(Block block)
    {
        string blockCode = block.Code.ToString();
        if (attributeBackedBlockAliasCache.TryGetValue(
            blockCode,
            out SearchAlias[]? cached
        ))
        {
            return cached;
        }

        var aliases = new List<SearchAlias>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (CreativeTabAndStackList tabAndStacks in
            block.CreativeInventoryStacks ?? Array.Empty<CreativeTabAndStackList>())
        {
            foreach (JsonItemStack jsonStack in
                tabAndStacks.Stacks ?? Array.Empty<JsonItemStack>())
            {
                if (jsonStack.Code == null
                    || !string.Equals(
                        jsonStack.Code.ToString(),
                        blockCode,
                        StringComparison.Ordinal
                    ))
                {
                    continue;
                }

                string material = jsonStack.Attributes?["material"].AsString("") ?? "";
                if (material.Length == 0) continue;
                string translationKey = $"{block.Code.Domain}:block-{block.Code.Path}-{material}";
                AddAlias(
                    aliases,
                    unique,
                    GetTranslation(englishTranslations, translationKey)
                );
                if (!ReferenceEquals(activeTranslations, englishTranslations))
                {
                    AddAlias(
                        aliases,
                        unique,
                        GetTranslation(activeTranslations, translationKey)
                    );
                }
            }
        }

        SearchAlias[] resolved = aliases.ToArray();
        attributeBackedBlockAliasCache[blockCode] = resolved;
        return resolved;
    }

    private SearchTranslationEntry[] BuildBlockTranslationEntries()
    {
        var entries = new List<SearchTranslationEntry>();
        var unique = new HashSet<SearchTranslationEntry>();
        AddBlockTranslationEntries(englishTranslations, entries, unique);
        if (!ReferenceEquals(activeTranslations, englishTranslations))
        {
            AddBlockTranslationEntries(activeTranslations, entries, unique);
        }
        return entries.ToArray();
    }

    private static SearchTranslationEntry[] BuildWildcardBlockTranslationEntries(
        SearchTranslationEntry[] entries
    )
    {
        var wildcardEntries = new List<SearchTranslationEntry>();
        foreach (SearchTranslationEntry entry in entries)
        {
            if (entry.CodePattern.Contains('*', StringComparison.Ordinal))
            {
                wildcardEntries.Add(entry);
            }
        }
        return wildcardEntries.ToArray();
    }

    private bool MatchesPreparedBlockQuery(
        AssetLocation knownBlock,
        string normalizedProbe
    )
    {
        if (normalizedProbe.Length == 0) return false;

        BeginBlockQuery();
        string compactProbe = Compact(normalizedProbe);
        while (ResolveBlockQueryStep(normalizedProbe, compactProbe)) { }
        return MatchesPreparedBlock(knownBlock);
    }

    private static void AddBlockTranslationEntries(
        ITranslationService? service,
        List<SearchTranslationEntry> entries,
        HashSet<SearchTranslationEntry> unique
    )
    {
        if (service == null) return;

        foreach ((string translationKey, string translated) in service.GetAllEntries())
        {
            if (!TryGetBlockCodePattern(translationKey, out string codePattern))
            {
                continue;
            }

            string normalized = Normalize(translated);
            if (normalized.Length == 0) continue;
            var entry = new SearchTranslationEntry(
                codePattern,
                normalized,
                Compact(normalized)
            );
            if (unique.Add(entry)) entries.Add(entry);
        }
    }

    private static void CollectMatchingTranslationTokens(
        ITranslationService? service,
        string candidate,
        HashSet<string> tokens
    )
    {
        if (service == null) return;

        foreach ((string translationKey, string translated) in service.GetAllEntries())
        {
            if (!TryGetBlockCodePattern(translationKey, out string codePattern)
                || !codePattern.Contains('*', StringComparison.Ordinal)
                || !MatchesGlobOrAttributeBacked(candidate, codePattern))
            {
                continue;
            }

            foreach (string token in Normalize(translated).Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries
            ))
            {
                tokens.Add(token);
            }
        }
    }

    private static bool TryGetBlockCodePattern(
        string translationKey,
        out string codePattern
    )
    {
        codePattern = "";
        int separator = translationKey.IndexOf(':');
        string domain = separator >= 0 ? translationKey[..separator] : "game";
        string localKey = separator >= 0
            ? translationKey[(separator + 1)..]
            : translationKey;
        string path;
        if (localKey.StartsWith("block-", StringComparison.Ordinal))
        {
            path = localKey["block-".Length..];
        }
        else if (localKey.StartsWith("item-", StringComparison.Ordinal)
            && !localKey.StartsWith("item-handbook", StringComparison.Ordinal))
        {
            path = localKey["item-".Length..];
        }
        else
        {
            return false;
        }

        if (domain.Length == 0 || path.Length == 0) return false;
        codePattern = $"{domain}:{path}";
        return true;
    }

    private static IEnumerable<string> GetTranslationKeys(
        AssetLocation code,
        AtlasSearchAliasKind kind
    )
    {
        string domain = code.Domain;
        string path = code.Path;
        if (kind == AtlasSearchAliasKind.Block)
        {
            yield return $"{domain}:block-{path}";
            yield return $"{domain}:item-{path}";
            yield break;
        }
        if (kind == AtlasSearchAliasKind.Item)
        {
            yield return $"{domain}:item-{path}";
            yield break;
        }

        yield return $"{domain}:item-{path}";
        yield return $"{domain}:itemdesc-{path}";
        yield return $"{domain}:entity-{path}";
        if (!path.StartsWith("creature-", StringComparison.Ordinal)) yield break;

        string groupPath = path["creature-".Length..];
        for (int level = 0; level < 4 && groupPath.Length > 0; level++)
        {
            yield return $"{domain}:creaturegroup-{groupPath}";
            int separator = groupPath.LastIndexOf('-');
            if (separator < 0) break;
            groupPath = groupPath[..separator];
        }
    }

    private static void AddAlias(
        List<SearchAlias> aliases,
        HashSet<string> unique,
        string? translated
    )
    {
        string normalized = Normalize(translated);
        if (normalized.Length == 0 || !unique.Add(normalized)) return;
        aliases.Add(new SearchAlias(normalized, Compact(normalized)));
    }

    private static string? GetTranslation(
        ITranslationService? service,
        string translationKey
    )
    {
        if (service == null) return null;
        try
        {
            string exact = service.GetUnformatted(translationKey);
            if (!string.Equals(exact, translationKey, StringComparison.Ordinal))
            {
                return exact;
            }

            // Wildcard translations often contain one or more format slots.
            // Neutral values resolve those templates without invoking the
            // engine's missing-argument diagnostics during the incremental
            // registry scan. The stable asset code remains a separate alias.
            return service.GetMatchingIfExists(
                translationKey,
                WildcardFormatArguments
            );
        }
        catch (Exception)
        {
            // A malformed third-party translation must not break atlas search.
            return null;
        }
    }

    private static object[] CreateWildcardFormatArguments()
    {
        var arguments = new object[32];
        Array.Fill(arguments, "");
        return arguments;
    }

    private static bool MatchesGlob(string candidate, string pattern)
    {
        int candidateIndex = 0;
        int patternIndex = 0;
        int wildcardIndex = -1;
        int wildcardCandidateIndex = -1;
        while (candidateIndex < candidate.Length)
        {
            if (patternIndex < pattern.Length
                && pattern[patternIndex] == candidate[candidateIndex])
            {
                candidateIndex++;
                patternIndex++;
                continue;
            }
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                wildcardIndex = patternIndex++;
                wildcardCandidateIndex = candidateIndex;
                continue;
            }
            if (wildcardIndex >= 0)
            {
                patternIndex = wildcardIndex + 1;
                candidateIndex = ++wildcardCandidateIndex;
                continue;
            }
            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }
        return patternIndex == pattern.Length;
    }

    private static bool MatchesGlobOrAttributeBacked(
        string candidate,
        string pattern
    )
    {
        if (MatchesGlob(candidate, pattern)) return true;

        // Some blocks, including lanterns, keep their material variant in a
        // block-entity or item-stack attribute. Their registered block code
        // therefore has fewer dash-delimited variant segments than its name
        // translation pattern.
        string[] candidateSegments = candidate.Split('-');
        string[] patternSegments = pattern.Split('-');
        if (patternSegments.Length <= candidateSegments.Length) return false;

        bool matchedWildcardSegment = false;
        for (int index = 0; index < candidateSegments.Length; index++)
        {
            string patternSegment = patternSegments[index];
            matchedWildcardSegment |= patternSegment.Contains(
                '*',
                StringComparison.Ordinal
            );
            if (!MatchesGlob(candidateSegments[index], patternSegment)) return false;
        }
        return matchedWildcardSegment;
    }

    private ITranslationService? LoadPrivateTranslations(string languageCode)
    {
        try
        {
            var service = new TranslationService(
                languageCode,
                capi.Logger,
                capi.Assets,
                EnumLinebreakBehavior.Default
            );
            service.Load(false);
            return service;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not prepare isolated {0} atlas search translations: {1}",
                languageCode,
                exception.Message
            );
            return null;
        }
    }

    private readonly record struct AliasCacheKey(
        string Domain,
        string Path,
        AtlasSearchAliasKind Kind
    );

    private readonly record struct SearchAlias(string Normalized, string Compact);

    private readonly record struct SearchTranslationEntry(
        string CodePattern,
        string Normalized,
        string Compact
    );
}
