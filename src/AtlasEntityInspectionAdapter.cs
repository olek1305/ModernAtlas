using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace ModernAtlas;

/// <summary>
/// Isolates the optional game-content health behavior from the atlas UI. The
/// base API exposes behaviors by code, while Health and MaxHealth live in the
/// installed game-content assembly. Reflection keeps that dependency small and
/// falls back to the public watched-attribute tree used for network updates.
/// </summary>
internal static class AtlasEntityInspectionAdapter
{
    private static readonly Dictionary<Type, HealthProperties> HealthPropertyCache = new();

    public static bool TryGetHealth(Entity entity, out float current, out float maximum)
    {
        current = 0;
        maximum = 0;

        EntityBehavior? behavior = entity.GetBehavior("health");
        if (behavior != null && TryReadBehaviorHealth(behavior, out current, out maximum))
        {
            return true;
        }

        ITreeAttribute? healthTree = entity.WatchedAttributes.GetTreeAttribute("health");
        if (healthTree == null) return false;

        current = healthTree.GetFloat("currenthealth", float.NaN);
        maximum = healthTree.GetFloat("maxhealth", float.NaN);
        return float.IsFinite(current)
            && float.IsFinite(maximum)
            && maximum > 0;
    }

    private static bool TryReadBehaviorHealth(
        EntityBehavior behavior,
        out float current,
        out float maximum
    )
    {
        current = 0;
        maximum = 0;
        Type behaviorType = behavior.GetType();
        if (!HealthPropertyCache.TryGetValue(behaviorType, out HealthProperties properties))
        {
            properties = new HealthProperties(
                behaviorType.GetProperty(
                    "Health",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                ),
                behaviorType.GetProperty(
                    "MaxHealth",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                )
            );
            HealthPropertyCache[behaviorType] = properties;
        }

        if (properties.Current == null || properties.Maximum == null) return false;

        try
        {
            current = Convert.ToSingle(properties.Current.GetValue(behavior));
            maximum = Convert.ToSingle(properties.Maximum.GetValue(behavior));
            return float.IsFinite(current)
                && float.IsFinite(maximum)
                && maximum > 0;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct HealthProperties(
        PropertyInfo? Current,
        PropertyInfo? Maximum
    );
}
