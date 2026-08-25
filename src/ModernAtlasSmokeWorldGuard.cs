using System;
using System.Collections.Generic;

namespace ModernAtlas;

/// <summary>
/// Keeps the automated smoke fixture guard tied to the explicit world that
/// the operator asked the game to open. SavegameIdentifier is a UUID in the
/// Vintage Story API, so it must never be compared with a save filename.
/// </summary>
internal static class ModernAtlasSmokeWorldGuard
{
    public const string CreativeWorldName = "MODERNATLAS_CREATIVE_TEST";
    public const string RealDamageWorldName = "MODERNATLAS_REAL_DAMAGE_TEST";

    public static string? LaunchWorldName =>
        TryGetLaunchWorldName(Environment.GetCommandLineArgs());

    public static string? TryGetLaunchWorldName(
        IReadOnlyList<string>? arguments
    )
    {
        if (arguments == null) return null;

        for (int index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "-o", StringComparison.Ordinal))
            {
                continue;
            }

            string candidate = arguments[index + 1].Trim();
            return candidate.Length == 0 || candidate.StartsWith("-", StringComparison.Ordinal)
                ? null
                : candidate;
        }

        return null;
    }

    public static bool IsAllowedWorld(string? worldName) =>
        string.Equals(worldName, CreativeWorldName, StringComparison.Ordinal)
        || string.Equals(worldName, RealDamageWorldName, StringComparison.Ordinal);

    public static string? Validate()
    {
        string[] creativeArguments = { "Vintagestory", "-o", CreativeWorldName };
        if (!string.Equals(
                TryGetLaunchWorldName(creativeArguments),
                CreativeWorldName,
                StringComparison.Ordinal
            ))
        {
            return "the exact -o creative launch target was not parsed";
        }

        string[] realArguments = { "Vintagestory", "-o", RealDamageWorldName };
        if (!string.Equals(
                TryGetLaunchWorldName(realArguments),
                RealDamageWorldName,
                StringComparison.Ordinal
            ))
        {
            return "the exact -o real-damage launch target was not parsed";
        }

        string[] missingValueArguments = { "Vintagestory", "-o" };
        if (TryGetLaunchWorldName(missingValueArguments) != null)
        {
            return "a -o option without a value was accepted";
        }

        string[] wrongWorldArguments = { "Vintagestory", "-o", "owner-save" };
        if (IsAllowedWorld(TryGetLaunchWorldName(wrongWorldArguments)))
        {
            return "an owner save was accepted as a disposable fixture";
        }

        return null;
    }
}
