using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Builds the two skin-textured lower arms from the local loaded Seraph model.
/// It deliberately owns only small, static meshes: neither atlas presentation
/// ever invokes the player's native renderer or advances its animation state.
/// </summary>
internal sealed class SeraphForearmRenderer : IDisposable
{
    private readonly ICoreClientAPI capi;
    private MeshRef? leftMesh;
    private MeshRef? rightMesh;
    private int skinTextureId;
    private Vec4f skinColor = new(1f, 1f, 1f, 1f);

    public bool IsReady => leftMesh != null && rightMesh != null && skinTextureId > 0;

    public SeraphForearmRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool Prepare()
    {
        if (IsReady) return true;
        DisposeMeshes();
        try
        {
            EntityPlayer player = capi.World.Player?.Entity
                ?? throw new InvalidOperationException("The local Seraph is unavailable.");
            Shape? loadedShape = player.Properties.Client.LoadedShapeForEntity
                ?? player.Properties.Client.LoadedShape;
            if (loadedShape == null)
            {
                throw new InvalidOperationException("The loaded Seraph shape is unavailable.");
            }

            Shape? bareSeraphShape = Shape.TryGet(
                capi,
                new AssetLocation("game", "shapes/entity/humanoid/seraph-hairless.json")
            );
            if (bareSeraphShape == null)
            {
                throw new InvalidOperationException("The base Seraph skin shape is unavailable.");
            }
            if (!PlayerSkinTextureAdapter.TryGet(
                    player,
                    out ITexPositionSource? textureSource,
                    out skinTextureId,
                    out skinColor
                ) || textureSource == null)
            {
                throw new InvalidOperationException("The player's composed skin texture is unavailable.");
            }

            string leftBoneName = player.GetBoneName("lowerArmLBoneName", "LowerArmL");
            string rightBoneName = player.GetBoneName("lowerArmRBoneName", "LowerArmR");
            if (FindShapeElement(loadedShape.Elements, leftBoneName) == null
                || FindShapeElement(loadedShape.Elements, rightBoneName) == null)
            {
                throw new InvalidOperationException("The loaded player model has no compatible forearm bones.");
            }

            leftMesh = UploadForearm("left", CreateSingleElementShape(bareSeraphShape, "LowerArmL"), textureSource);
            rightMesh = UploadForearm("right", CreateSingleElementShape(bareSeraphShape, "LowerArmR"), textureSource);
            return true;
        }
        catch (Exception exception)
        {
            DisposeMeshes();
            capi.Logger.Error(
                "[ModernAtlas] Could not build first-person arms from the loaded Seraph model: {0}",
                exception.Message
            );
            return false;
        }
    }

    public void RenderLeftArm(
        IShaderProgram shader,
        float[] parent,
        float startX,
        float startY,
        float endX,
        float endY,
        float alpha
    ) => RenderArm(shader, leftMesh, parent, startX, startY, endX, endY, alpha);

    public void RenderRightArm(
        IShaderProgram shader,
        float[] parent,
        float startX,
        float startY,
        float endX,
        float endY,
        float alpha
    ) => RenderArm(shader, rightMesh, parent, startX, startY, endX, endY, alpha);

    public void BindSkin(IShaderProgram shader)
    {
        shader.BindTexture2D("entityTex", skinTextureId, 0);
        shader.Uniform("entityColor", skinColor);
    }

    private MeshRef UploadForearm(string side, Shape shape, ITexPositionSource textureSource)
    {
        capi.Tesselator.TesselateShape(
            $"ModernAtlas {side} skin forearm",
            shape,
            out MeshData mesh,
            textureSource,
            new Vec3f(),
            0,
            0,
            0
        );
        NormalizeMesh(mesh);
        return capi.Render.UploadMesh(mesh);
    }

    private void RenderArm(
        IShaderProgram shader,
        MeshRef? mesh,
        float[] parent,
        float startX,
        float startY,
        float endX,
        float endY,
        float alpha
    )
    {
        if (mesh == null || alpha <= 0.001f) return;
        float dx = endX - startX;
        float dy = endY - startY;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        float[] model = Mat4f.CloneIt(parent);
        Mat4f.Translate(model, model, (startX + endX) * 0.5f, (startY + endY) * 0.5f, -0.035f);
        Mat4f.RotateZ(model, model, MathF.PI - MathF.Atan2(dx, dy));
        Mat4f.Scale(model, model, length * 1.35f, length, length * 1.35f);
        shader.UniformMatrix("modelViewMatrix", model);
        shader.Uniform("materialKind", 6);
        shader.Uniform("alpha", Math.Clamp(alpha, 0f, 1f));
        shader.Uniform("lightSweep", 0f);
        capi.Render.RenderMesh(mesh);
    }

    private void DisposeMeshes()
    {
        leftMesh?.Dispose();
        leftMesh = null;
        rightMesh?.Dispose();
        rightMesh = null;
        skinTextureId = 0;
        skinColor = new Vec4f(1f, 1f, 1f, 1f);
    }

    public void Dispose() => DisposeMeshes();

    private static ShapeElement? FindShapeElement(ShapeElement[]? elements, string name)
    {
        if (elements == null) return null;
        foreach (ShapeElement element in elements)
        {
            if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase)) return element;
            ShapeElement? child = FindShapeElement(element.Children, name);
            if (child != null) return child;
        }
        return null;
    }

    private static Shape CreateSingleElementShape(Shape source, string elementName)
    {
        Shape shape = source.Clone();
        ShapeElement element = FindShapeElement(shape.Elements, elementName)
            ?? throw new InvalidOperationException($"The base Seraph shape has no {elementName} element.");
        element.ParentElement = null;
        shape.Elements = new[] { element };
        shape.Animations = Array.Empty<Animation>();
        return shape;
    }

    private static void NormalizeMesh(MeshData mesh)
    {
        if (mesh.VerticesCount <= 0) throw new InvalidOperationException("The selected Seraph forearm contains no vertices.");
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        float maxZ = float.MinValue;
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            int index = vertex * 3;
            float x = mesh.xyz[index];
            float y = mesh.xyz[index + 1];
            float z = mesh.xyz[index + 2];
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
        }
        float sizeY = Math.Max(0.001f, maxY - minY);
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            int index = vertex * 3;
            mesh.xyz[index] = (mesh.xyz[index] - centerX) / sizeY;
            mesh.xyz[index + 1] = (mesh.xyz[index + 1] - centerY) / sizeY;
            mesh.xyz[index + 2] = (mesh.xyz[index + 2] - centerZ) / sizeY;
        }
    }
}
