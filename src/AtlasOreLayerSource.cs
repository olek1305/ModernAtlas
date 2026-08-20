namespace ModernAtlas;

/// <summary>
/// Where the ore layer's colors came from across the whole prepared radius.
/// <see cref="Mixed"/> exists so the status can report both groups instead of
/// letting one found regional map hide the loaded columns that filled the
/// rest of the map.
/// </summary>
internal enum AtlasOreLayerSource
{
    None,
    RegionalPotential,
    LoadedColumns,
    Mixed
}
