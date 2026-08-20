using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
namespace ModernAtlas;

/// <summary>
/// Atlas-only directional celestial lighting: live and fixed-hour sun or moon
/// state, bounded exposure and the color helpers used by both.
/// </summary>
internal sealed partial class ExactChunkRendererAdapter
{
    private void ApplyAtlasLighting(
        IAmbientManager ambient,
        DefaultShaderUniforms shaderUniforms,
        bool performanceLightingEnabled,
        bool liveLightingEnabled,
        int fixedSunHour,
        Vec3f liveAmbientColor,
        float liveSceneBrightness,
        Vec3f liveLightPosition,
        float liveSkyDaylight,
        float visualExposureMultiplier
    )
    {
        float visualExposure = Math.Clamp(visualExposureMultiplier, 0.5f, 1.5f);
        if (!performanceLightingEnabled)
        {
            Vec3f neutralLight = new(1f, 1f, 1f);
            Vec3f overheadLight = new(0.08f, 0.99f, -0.10f);
            ambientColorProperty.SetValue(
                ambient,
                ScaleColor(neutralLight, visualExposure)
            );
            ambientSceneBrightnessProperty.SetValue(
                ambient,
                Math.Clamp(visualExposure, 0.5f, 1.5f)
            );
            shaderUniforms.LightPosition3D = overheadLight;
            shaderUniforms.SunPosition3D = overheadLight;
            skyDaylightUniformField.SetValue(shaderUniforms, 1f);
            shaderUniforms.SunsetMod = 0f;
            atlasSunDirection = overheadLight;
            atlasSunColor = neutralLight;
            atlasExposure = visualExposure;
            return;
        }
        IClientGameCalendar? calendar = capi.World.Calendar as IClientGameCalendar;
        if (liveLightingEnabled && calendar != null)
        {
            float daylight = Math.Clamp(calendar.DayLightStrength, 0f, 1f);
            Vec3f sunDirection = NormalizeDirection(
                calendar.SunPositionNormalized,
                liveLightPosition
            );
            bool moonlit = sunDirection.Y < -0.04f
                && calendar.MoonLightStrength > calendar.SunLightStrength;
            Vec3f lightDirection = moonlit
                ? NormalizeDirection(calendar.MoonPosition, liveLightPosition)
                : sunDirection;
            lightDirection = KeepDirectionalLightAboveTerrain(lightDirection);
            Vec3f atmosphericColor = moonlit
                ? NightLightColor
                : GetAtmosphericLightColor(
                    daylight,
                    sunDirection.Y,
                    calendar.Dusk,
                    calendar.SunColor
                );
            Vec3f tintedAmbient = TintAmbientColor(
                liveAmbientColor,
                atmosphericColor,
                0.52f
            );
            ambientColorProperty.SetValue(
                ambient,
                ScaleColor(tintedAmbient, visualExposure)
            );
            ambientSceneBrightnessProperty.SetValue(
                ambient,
                Math.Clamp(liveSceneBrightness * visualExposure, 0.02f, 1.5f)
            );
            shaderUniforms.LightPosition3D = lightDirection;
            // Chunk programs read lightPosition for directional face shading
            // and sunPosition for the celestial color contribution. Update
            // both from the same live world calendar so sunrise in the east,
            // sunset in the west and the seasonal north/south arc remain
            // visible instead of retaining the normal camera's stale sun.
            shaderUniforms.SunPosition3D = sunDirection;
            skyDaylightUniformField.SetValue(shaderUniforms, daylight);
            shaderUniforms.SunsetMod = calendar.SunsetMod;
            atlasSunDirection = lightDirection;
            atlasSunColor = atmosphericColor;
            atlasExposure = Math.Clamp(
                Math.Max(0.12f, daylight) * Math.Max(0.2f, liveSceneBrightness)
                    * visualExposure,
                0.04f,
                1.5f
            );
            return;
        }
        if (liveLightingEnabled)
        {
            ambientColorProperty.SetValue(
                ambient,
                ScaleColor(liveAmbientColor, visualExposure)
            );
            ambientSceneBrightnessProperty.SetValue(
                ambient,
                Math.Clamp(liveSceneBrightness * visualExposure, 0.02f, 1.5f)
            );
            shaderUniforms.LightPosition3D = liveLightPosition;
            Vec3f fallbackSunDirection = NormalizeDirection(
                liveLightPosition,
                atlasSunDirection
            );
            shaderUniforms.SunPosition3D = fallbackSunDirection;
            atlasSunDirection = fallbackSunDirection;
            atlasSunColor = DayLightColor;
            atlasExposure = Math.Clamp(
                liveSkyDaylight * Math.Max(0.2f, liveSceneBrightness)
                    * visualExposure,
                0.04f,
                1.5f
            );
            return;
        }

        int hour = Math.Clamp(fixedSunHour, 0, 23);
        Vec3f fixedSunDirection;
        Vec3f fixedMoonDirection;
        if (calendar != null)
        {
            double hoursPerDay = Math.Max(1.0, calendar.HoursPerDay);
            double fixedTotalDays = calendar.TotalDays
                + (hour - calendar.HourOfDay) / hoursPerDay;
            Vec3d playerPosition = new(
                capi.World.Player.Entity.Pos.X,
                capi.World.Player.Entity.Pos.Y,
                capi.World.Player.Entity.Pos.Z
            );
            fixedSunDirection = NormalizeDirection(
                calendar.GetSunPosition(playerPosition, fixedTotalDays),
                liveLightPosition
            );
            fixedMoonDirection = NormalizeDirection(
                calendar.GetMoonPosition(playerPosition, fixedTotalDays),
                new Vec3f(
                    -fixedSunDirection.X,
                    Math.Abs(fixedSunDirection.Y),
                    -fixedSunDirection.Z
                )
            );
        }
        else
        {
            float phase = (hour - 6f) / 24f * GameMath.TWOPI;
            fixedSunDirection = NormalizeDirection(
                new Vec3f(
                    MathF.Cos(phase + 0.45f),
                    MathF.Sin(phase),
                    MathF.Sin(phase + 0.45f)
                ),
                liveLightPosition
            );
            fixedMoonDirection = new Vec3f(
                -fixedSunDirection.X,
                Math.Abs(fixedSunDirection.Y),
                -fixedSunDirection.Z
            );
        }

        float fixedDaylight = SmoothStep(-0.10f, 0.20f, fixedSunDirection.Y);
        bool fixedDusk = hour >= 12;
        Vec3f fixedLightColor = GetAtmosphericLightColor(
            fixedDaylight,
            fixedSunDirection.Y,
            fixedDusk,
            null
        );
        Vec3f fixedLightDirection = fixedSunDirection.Y < -0.04f
            ? fixedMoonDirection
            : fixedSunDirection;
        fixedLightDirection = KeepDirectionalLightAboveTerrain(fixedLightDirection);
        shaderUniforms.LightPosition3D = fixedLightDirection;
        // Keep the real below/above-horizon solar vector for sky color while
        // the separate face-light vector is safely clamped above terrain.
        // GetSunPosition evaluates the current world date and player location,
        // so a fixed hour still has the world's real east/west and seasonal
        // north/south direction.
        shaderUniforms.SunPosition3D = fixedSunDirection;
        skyDaylightUniformField.SetValue(shaderUniforms, fixedDaylight);
        shaderUniforms.SunsetMod = calendar?.SunsetMod ?? 0f;
        atlasSunDirection = fixedLightDirection;
        atlasSunColor = fixedLightColor;
        float fixedBrightness = 0.20f + fixedDaylight * 0.80f;
        atlasExposure = Math.Clamp(
            fixedBrightness * visualExposure,
            0.04f,
            1.5f
        );
        ambientColorProperty.SetValue(
            ambient,
            ScaleColor(fixedLightColor, visualExposure)
        );
        ambientSceneBrightnessProperty.SetValue(
            ambient,
            Math.Clamp(fixedBrightness * visualExposure, 0.02f, 1.5f)
        );
    }

    private static readonly Vec3f NightLightColor = new(0.30f, 0.42f, 0.72f);
    private static readonly Vec3f DayLightColor = new(1.00f, 0.97f, 0.88f);
    private static readonly Vec3f DawnLightColor = new(1.00f, 0.68f, 0.34f);
    private static readonly Vec3f DuskLightColor = new(1.00f, 0.43f, 0.18f);

    private static Vec3f GetAtmosphericLightColor(
        float daylight,
        float solarAltitude,
        bool dusk,
        Vec3f? nativeSunColor
    )
    {
        float dayAmount = Math.Clamp(daylight, 0f, 1f);
        Vec3f color = MixColor(NightLightColor, DayLightColor, dayAmount);
        float horizonAmount = GetHorizonAmount(dayAmount, solarAltitude);
        color = MixColor(
            color,
            dusk ? DuskLightColor : DawnLightColor,
            horizonAmount * 0.82f
        );
        if (nativeSunColor != null && dayAmount > 0.02f)
        {
            Vec3f normalizedNativeColor = NormalizeColor(nativeSunColor);
            color = MixColor(color, normalizedNativeColor, dayAmount * 0.38f);
        }
        return color;
    }

    private static float GetHorizonAmount(float daylight, float solarAltitude) =>
        Math.Clamp(daylight, 0f, 1f)
            * (1f - SmoothStep(0.08f, 0.55f, solarAltitude));

    private static Vec3f TintAmbientColor(Vec3f ambient, Vec3f tint, float amount)
    {
        Vec3f tinted = new(
            ambient.X * (0.5f + 0.5f * tint.X),
            ambient.Y * (0.5f + 0.5f * tint.Y),
            ambient.Z * (0.5f + 0.5f * tint.Z)
        );
        return MixColor(ambient, tinted, Math.Clamp(amount, 0f, 1f));
    }

    private static Vec3f NormalizeDirection(Vec3f direction, Vec3f fallback)
    {
        float length = MathF.Sqrt(
            direction.X * direction.X
                + direction.Y * direction.Y
                + direction.Z * direction.Z
        );
        if (length < 0.0001f) return fallback;
        return new Vec3f(
            direction.X / length,
            direction.Y / length,
            direction.Z / length
        );
    }

    private static Vec3f KeepDirectionalLightAboveTerrain(Vec3f direction)
    {
        // The native chunk programs expect the directional light to illuminate
        // terrain from above. A calendar moon can legitimately be below the
        // horizon; feeding that vector into block-face lighting produces large
        // dark mesh patches that resemble square chunk shadows. Preserve the
        // real azimuth while using a shallow, stable sky elevation.
        return NormalizeDirection(
            new Vec3f(direction.X, Math.Max(0.16f, Math.Abs(direction.Y)), direction.Z),
            new Vec3f(-0.34f, 0.86f, -0.38f)
        );
    }

    private static Vec3f NormalizeColor(Vec3f color)
    {
        float maximum = Math.Max(0.001f, Math.Max(color.X, Math.Max(color.Y, color.Z)));
        if (maximum <= 0.001f) return DayLightColor;
        return new Vec3f(
            Math.Clamp(color.X / maximum, 0f, 1f),
            Math.Clamp(color.Y / maximum, 0f, 1f),
            Math.Clamp(color.Z / maximum, 0f, 1f)
        );
    }

    private static Vec3f MixColor(Vec3f from, Vec3f to, float amount)
    {
        float weight = Math.Clamp(amount, 0f, 1f);
        return new Vec3f(
            from.X + (to.X - from.X) * weight,
            from.Y + (to.Y - from.Y) * weight,
            from.Z + (to.Z - from.Z) * weight
        );
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float progress = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return progress * progress * (3f - 2f * progress);
    }

    private static Vec3f ScaleColor(Vec3f color, float scale) => new(
        Math.Clamp(color.X * scale, 0f, 1.5f),
        Math.Clamp(color.Y * scale, 0f, 1.5f),
        Math.Clamp(color.Z * scale, 0f, 1.5f)
    );

}
