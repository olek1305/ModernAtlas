using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ModernAtlas;

/// <summary>
/// Reads the already blended weather at the local player. This class never
/// advances weather, creates particles, scans chunks or requests world data.
/// </summary>
internal sealed class AtlasScrollRealtimeWeather
{
    private readonly ICoreClientAPI capi;
    private WeatherSystemClient? weatherSystem;
    public string LastDiagnostic { get; private set; } = "not sampled";

    public AtlasScrollRealtimeWeather(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool TryGetExposedWeather(out AtlasScrollWeatherState state)
    {
        state = default;
        if (capi.World.Player?.Entity == null)
        {
            LastDiagnostic = "skipped: local player is unavailable";
            return false;
        }

        // The engine's environment-awareness tracker maintains this value at
        // the player once per second. Zero means precipitation can reach the
        // player; a roof, cave or enclosed room makes the distance positive.
        float distanceToRainfall = GlobalConstants.CurrentDistanceToRainfallClient;
        if (distanceToRainfall > 0)
        {
            LastDiagnostic = $"skipped: player is sheltered; distanceToRainfall={distanceToRainfall:0.###}";
            return false;
        }

        weatherSystem ??= capi.ModLoader.GetModSystem<WeatherSystemClient>();
        WeatherDataSnapshot? weather = weatherSystem?.BlendedWeatherData;
        if (weatherSystem == null || weather == null)
        {
            LastDiagnostic = "skipped: native blended weather is unavailable";
            return false;
        }

        // Keep the public precipitation state as an intensity fallback. The
        // native particle renderer selects the final type from the blended
        // weather snapshot below.
        PrecipitationState precipitationState =
            weatherSystem.GetPrecipitationState(capi.World.Player.Entity.Pos.XYZ);
        BlockPos playerBlockPos = new(
            (int)Math.Floor(capi.World.Player.Entity.Pos.X),
            (int)Math.Floor(capi.World.Player.Entity.Pos.Y),
            (int)Math.Floor(capi.World.Player.Entity.Pos.Z)
        );
        ClimateCondition? currentClimate = capi.World.BlockAccessor.GetClimateAt(
            playerBlockPos,
            EnumGetClimateMode.NowValues,
            0
        );
        float currentTemperature = currentClimate?.Temperature
            ?? weather.climateCond?.Temperature
            ?? 4f;
        // This is the exact type selected by the native particle renderer.
        // Weather events replace Auto with Hail here even though the public
        // precipitation-state query can continue reporting Auto.
        EnumPrecipitationType resolvedType = weather.BlendedPrecType;
        if (resolvedType == EnumPrecipitationType.Auto)
        {
            // Vintage Story 1.22.6 keeps Auto for ordinary weather patterns.
            // Its native particle renderer resolves that value from the
            // already blended local climate and the weather snapshot's snow
            // threshold; mirror that final choice without simulating weather.
            resolvedType = currentTemperature < weather.snowThresholdTemp
                ? EnumPrecipitationType.Snow
                : EnumPrecipitationType.Rain;
        }

        int precipitationKind = resolvedType switch
        {
            EnumPrecipitationType.Rain => 1,
            EnumPrecipitationType.Snow => 2,
            EnumPrecipitationType.Hail => 3,
            _ => 0
        };
        float precipitation = Math.Clamp(
            currentClimate?.Rainfall ?? (float)precipitationState.Level,
            0f,
            1f
        );
        if (precipitation < 0.015f) precipitationKind = 0;

        // Read only the weather pattern's own fog contribution. Player-local
        // underwater, vision, damage and other ambient modifiers must not tint
        // the scroll. Normal clear weather is near 1; dedicated haze patterns
        // rise above that baseline.
        float weatherFogDensity = weather.Ambient?.FogDensity?.Value ?? 0f;
        float fog = Math.Clamp((weatherFogDensity - 1.5f) / 18f, 0f, 1f);
        if (precipitationKind == 0 && fog < 0.015f)
        {
            LastDiagnostic = $"skipped: sourceType={precipitationState.Type}, resolvedType={resolvedType}, level={precipitation:0.###}, blendedType={weather.BlendedPrecType}, temperature={currentTemperature:0.###}, snowThreshold={weather.snowThresholdTemp:0.###}, distanceToRainfall={distanceToRainfall:0.###}, weatherFog={weatherFogDensity:0.###}";
            return false;
        }

        state = new AtlasScrollWeatherState(
            precipitationKind,
            precipitation,
            fog
        );
        LastDiagnostic = $"active: sourceType={precipitationState.Type}, resolvedType={resolvedType}, level={precipitation:0.###}, blendedType={weather.BlendedPrecType}, temperature={currentTemperature:0.###}, snowThreshold={weather.snowThresholdTemp:0.###}, distanceToRainfall={distanceToRainfall:0.###}, weatherFog={weatherFogDensity:0.###}, overlayFog={fog:0.###}";
        return true;
    }
}

internal readonly record struct AtlasScrollWeatherState(
    int PrecipitationKind,
    float PrecipitationIntensity,
    float FogIntensity
);
