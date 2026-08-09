using Vintagestory.API.Common.Entities;

namespace ModernAtlas;

internal enum AtlasEntityKind
{
    Player,
    Animal,
    Mob,
    Npc
}

/// <summary>
/// A living model that passed the atlas disclosure, distance and cave filters
/// in the current frame. UI inspection consumes only these entries.
/// </summary>
internal readonly record struct AtlasRenderedEntity(
    Entity Entity,
    AtlasEntityKind Kind
);
