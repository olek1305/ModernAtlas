using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Reuses the renderers and animated meshes of already client-loaded living
/// entities inside the atlas world framebuffer. It never invokes the global
/// entity stage, so dropped items, particles, labels and hidden entities stay
/// outside the atlas.
/// </summary>
internal sealed class AtlasEntityModelRendererAdapter
{
    private const int MaximumEntities = 512;

    private readonly ICoreClientAPI capi;
    private bool disabled;

    public AtlasEntityModelRendererAdapter(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public int Render(
        float deltaTime,
        double[] view,
        float[] projection,
        int viewDistanceBlocks,
        ModernAtlasServerPolicy policy,
        float pausedAnimationDeltaTime
    )
    {
        if (disabled || !policy.AnyEntityModels) return 0;

        try
        {
            List<RenderEntry> entries = CollectEntries(viewDistanceBlocks, policy);
            if (entries.Count == 0) return 0;

            foreach (RenderEntry entry in entries)
            {
                AdvancePausedAnimation(entry.Entity, pausedAnimationDeltaTime);
                entry.Renderer.BeforeRender(deltaTime);
                entry.ForceThirdPerson(capi.World.Player.Entity);
                entry.Renderer.DoRender3DOpaque(deltaTime, false);
            }

            IRenderAPI render = capi.Render;
            render.CurrentActiveShader?.Stop();
            IShaderProgram shader = render.GetEngineShader(EnumShaderProgram.Entityanimated);
            shader.Use();
            shader.Uniform("rgbaAmbientIn", new Vec3f(0.9f, 0.9f, 0.9f));
            shader.Uniform("rgbaFogIn", new Vec4f(0.32f, 0.38f, 0.40f, 1f));
            shader.Uniform("fogMinIn", 0f);
            shader.Uniform("fogDensityIn", 0f);
            shader.UniformMatrix("projectionMatrix", projection);
            shader.BindTexture2D(
                "entityTex",
                capi.EntityTextureAtlas.AtlasTextures[0].TextureId,
                0
            );
            shader.Uniform("alphaTest", 0.05f);
            shader.Uniform("lightPosition", new Vec3f(-0.34f, 0.86f, -0.38f));
            shader.Uniform("shadowIntensity", 0f);
            shader.Uniform("glitchStrength", 0f);
            shader.Uniform("glitchStrengthFL", 0f);
            shader.Uniform("psychedelicStrength", 0f);
            shader.Uniform("nightVisionStrength", 0f);
            shader.Uniform("globalWarpIntensity", 0f);
            shader.Uniform("perceptionEffectIntensity", 0f);
            shader.Uniform("fogSphereQuantity", 0);
            shader.Uniform("cameraUnderwater", 0f);
            shader.Uniform("pointLightQuantity", 0);
            shader.Uniform("depthOffset", 0f);

            render.GlMatrixModeModelView();
            render.GlPushMatrix();
            render.GlLoadMatrix(view);
            render.GlDisableCullFace();
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            render.GLEnableDepthTest();
            try
            {
                foreach (RenderEntry entry in entries)
                {
                    entry.Renderer.DoRender3DOpaqueBatched(deltaTime, false);
                }
            }
            finally
            {
                render.GlPopMatrix();
                shader.Stop();
                foreach (RenderEntry entry in entries)
                {
                    entry.RestoreRenderMode();
                }
            }

            return entries.Count;
        }
        catch (Exception exception)
        {
            disabled = true;
            capi.Logger.Error(
                "[ModernAtlas] Live 3D entity rendering failed and was disabled for this session: {0}",
                exception.Message
            );
            return 0;
        }
    }

    private List<RenderEntry> CollectEntries(
        int viewDistanceBlocks,
        ModernAtlasServerPolicy policy
    )
    {
        List<RenderEntry> entries = new();
        Entity playerEntity = capi.World.Player.Entity;
        double maximumDistanceSquared = (double)viewDistanceBlocks * viewDistanceBlocks;
        foreach (Entity entity in capi.World.LoadedEntities.Values)
        {
            if (entries.Count >= MaximumEntities
                || !entity.Alive
                || !entity.IsCreature
                || entity.Pos.Dimension != playerEntity.Pos.Dimension)
            {
                continue;
            }

            double dx = entity.Pos.X - playerEntity.Pos.X;
            double dz = entity.Pos.Z - playerEntity.Pos.Z;
            if (dx * dx + dz * dz > maximumDistanceSquared) continue;

            EntityKind kind = Classify(entity);
            if (!IsAllowed(kind, policy)) continue;

            EntityRenderer? renderer = entity.Properties?.Client?.Renderer;
            if (renderer == null) continue;
            entries.Add(new RenderEntry(entity, renderer));
        }

        return entries;
    }

    private static void AdvancePausedAnimation(Entity entity, float deltaTime)
    {
        if (deltaTime <= 0 || entity.AnimManager is not AnimationManager animationManager) return;

        bool oldRunWhilePaused = animationManager.RunWhilePaused;
        bool oldIsRendered = entity.IsRendered;
        try
        {
            animationManager.RunWhilePaused = true;
            entity.IsRendered = true;
            animationManager.OnClientFrame(deltaTime);
        }
        finally
        {
            entity.IsRendered = oldIsRendered;
            animationManager.RunWhilePaused = oldRunWhilePaused;
        }
    }

    private static EntityKind Classify(Entity entity)
    {
        string runtimeGroup = entity.Properties?.Server?.SpawnConditions?.Runtime?.Group ?? "";
        string worldgenGroup = entity.Properties?.Server?.SpawnConditions?.Worldgen?.Group ?? "";
        if (runtimeGroup.Equals("hostile", StringComparison.OrdinalIgnoreCase)
            || worldgenGroup.Equals("hostile", StringComparison.OrdinalIgnoreCase))
        {
            return EntityKind.Mob;
        }

        string className = entity.Properties?.Class ?? entity.GetType().Name;
        if (className.Contains("npc", StringComparison.OrdinalIgnoreCase)
            || className.Contains("trader", StringComparison.OrdinalIgnoreCase)
            || className.Contains("villager", StringComparison.OrdinalIgnoreCase)
            || className.Contains("playerbot", StringComparison.OrdinalIgnoreCase))
        {
            return EntityKind.Npc;
        }

        return entity is EntityPlayer ? EntityKind.Player : EntityKind.Animal;
    }

    private static bool IsAllowed(EntityKind kind, ModernAtlasServerPolicy policy)
    {
        return kind switch
        {
            EntityKind.Player => policy.ShowPlayers,
            EntityKind.Animal => policy.ShowAnimals,
            EntityKind.Mob => policy.ShowMobs,
            EntityKind.Npc => policy.ShowNpcs,
            _ => false
        };
    }

    private enum EntityKind
    {
        Player,
        Animal,
        Mob,
        Npc
    }

    private sealed class RenderEntry
    {
        private FieldInfo? renderModeField;
        private object? savedRenderMode;

        public Entity Entity { get; }
        public EntityRenderer Renderer { get; }

        public RenderEntry(Entity entity, EntityRenderer renderer)
        {
            Entity = entity;
            Renderer = renderer;
        }

        public void ForceThirdPerson(Entity localPlayer)
        {
            if (Entity != localPlayer) return;

            renderModeField = FindField(Renderer.GetType(), "renderMode");
            if (renderModeField?.FieldType.IsEnum != true) return;

            savedRenderMode = renderModeField.GetValue(Renderer);
            object thirdPerson = Enum.Parse(renderModeField.FieldType, "ThirdPerson");
            renderModeField.SetValue(Renderer, thirdPerson);
        }

        public void RestoreRenderMode()
        {
            if (renderModeField != null && savedRenderMode != null)
            {
                renderModeField.SetValue(Renderer, savedRenderMode);
            }
            renderModeField = null;
            savedRenderMode = null;
        }

        private static FieldInfo? FindField(Type type, string name)
        {
            for (Type? current = type; current != null; current = current.BaseType)
            {
                FieldInfo? field = current.GetField(
                    name,
                    BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                );
                if (field != null) return field;
            }
            return null;
        }
    }
}
