using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Owns the atlas-local parchment and light cues. Reusing two controlled sound
/// channels prevents rapid open, close and map interactions from layering the
/// same short samples into an unnaturally loud burst.
/// </summary>
internal sealed class AtlasSoundController : IDisposable
{
    private const double InteractionCooldownSeconds = 0.30;
    private readonly ICoreClientAPI capi;
    private ILoadedSound? parchmentSound;
    private ILoadedSound? lightSound;
    private long lastInteractionTimestamp;
    private bool disposed;

    public AtlasSoundController(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool PlayOpeningUnroll() => PlayParchment(
        "sounds/held/bookturn3",
        0.34f,
        0.82f
    );

    public bool PlayClosingRoll()
    {
        StopLightCue();
        return PlayParchment("sounds/held/bookclose1", 0.36f, 0.82f);
    }

    public bool PlayPageTouch()
    {
        if (disposed) return false;
        long now = Stopwatch.GetTimestamp();
        if (lastInteractionTimestamp != 0
            && Stopwatch.GetElapsedTime(lastInteractionTimestamp, now).TotalSeconds
                < InteractionCooldownSeconds)
        {
            return true;
        }

        lastInteractionTimestamp = now;
        return PlayParchment("sounds/held/bookturn2", 0.18f, 0.88f);
    }

    public bool PlayLightSweep()
    {
        return PlaySound(
            ref lightSound,
            "sounds/effect/swoosh",
            0.24f,
            1.18f
        );
    }

    public void StopLightCue() => StopAndDispose(ref lightSound);

    public void StopAll()
    {
        StopAndDispose(ref parchmentSound);
        StopAndDispose(ref lightSound);
        lastInteractionTimestamp = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAll();
    }

    private bool PlayParchment(string path, float volume, float pitch)
    {
        return PlaySound(ref parchmentSound, path, volume, pitch);
    }

    private bool PlaySound(
        ref ILoadedSound? channel,
        string path,
        float volume,
        float pitch
    )
    {
        if (disposed) return false;

        try
        {
            var location = new AssetLocation("game", path);
            if (capi.Assets.TryGet(new AssetLocation("game", $"{path}.ogg")) == null)
            {
                capi.Logger.Warning(
                    "[ModernAtlas] Atlas sound asset is unavailable: {0}",
                    location
                );
                return false;
            }

            StopAndDispose(ref channel);
            channel = capi.World.LoadSound(new SoundParams(location)
            {
                RelativePosition = true,
                Position = new Vec3f(0, 0, 0),
                ShouldLoop = false,
                DisposeOnFinish = false,
                Pitch = pitch,
                Volume = volume,
                Range = 16,
                ReferenceDistance = 4,
                SoundType = EnumSoundType.Sound
            });
            channel.Start();
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "[ModernAtlas] Atlas sound {0} was unavailable: {1}",
                path,
                exception.Message
            );
            StopAndDispose(ref channel);
            return false;
        }
    }

    private static void StopAndDispose(ref ILoadedSound? sound)
    {
        if (sound == null) return;
        try
        {
            if (!sound.IsDisposed) sound.Stop();
            sound.Dispose();
        }
        catch
        {
            // Audio teardown can race the engine's world disposal. The sound
            // no longer belongs to the atlas even when the backend is gone.
        }
        finally
        {
            sound = null;
        }
    }
}
