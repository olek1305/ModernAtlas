using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

internal enum AtlasScreenshotJobState
{
    Idle,
    Capturing,
    Stitching,
    Validating,
    Committed,
    Failed,
    Cancelled
}

/// <summary>
/// Dimensions and quality information shown before a tiled screenshot starts.
/// Requested dimensions describe the uncapped result; output dimensions are
/// the actual PNG dimensions after the memory safety budget is applied.
/// </summary>
internal readonly struct AtlasScreenshotPreview
{
    public bool IsValid { get; }
    public int ResolutionScale { get; }
    public int CaptureAreaPercent { get; }
    public int RequestedWidth { get; }
    public int RequestedHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public long RequestedPixels { get; }
    public long OutputPixels { get; }
    public float DownsampleScale { get; }
    public float RequestedDetailFactor { get; }

    public double RequestedMegapixels => RequestedPixels / 1_000_000d;
    public double OutputMegapixels => OutputPixels / 1_000_000d;
    public bool WasDownsampled => DownsampleScale < 0.9999f;
    public float EffectiveDetailFactor => RequestedDetailFactor * DownsampleScale;

    public AtlasScreenshotPreview(
        bool isValid,
        int resolutionScale,
        int captureAreaPercent,
        int requestedWidth,
        int requestedHeight,
        int outputWidth,
        int outputHeight,
        long requestedPixels,
        long outputPixels,
        float downsampleScale,
        float requestedDetailFactor
    )
    {
        IsValid = isValid;
        ResolutionScale = resolutionScale;
        CaptureAreaPercent = captureAreaPercent;
        RequestedWidth = requestedWidth;
        RequestedHeight = requestedHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        RequestedPixels = requestedPixels;
        OutputPixels = outputPixels;
        DownsampleScale = downsampleScale;
        RequestedDetailFactor = requestedDetailFactor;
    }
}

/// <summary>
/// Tiled high-resolution atlas capture. The current map view is divided into
/// an N-by-N grid (1x to 8x). For every tile the atlas camera zooms in N times
/// and re-renders the exact world into the engine Primary framebuffer; the
/// tile's sub-region is read back on the render thread. The tiles are stitched
/// into one seamless top-down RGB image and encoded to PNG on a background
/// thread, so neither the capture nor the save blocks the game.
///
/// The atlas uses an orthographic projection, so zooming and panning map
/// tiles to each other by an exact affine transform; every tile is captured
/// with a small overlap margin that is cropped at stitch time to absorb
/// floating point and antialiasing differences at the shared edges.
/// </summary>
internal sealed class AtlasTiledScreenshot : IDisposable
{
    /// <summary>
    /// Upper bound for the stitched output. With the band-streamed encoder
    /// the raw image is never held in memory, so higher scales may keep
    /// their full tile resolution longer before the budget downsamples
    /// them. The tile store is bounded by budget * 4 bytes (about 400 MB).
    /// </summary>
    internal const long MaximumStitchedPixels = 100_000_000;
    internal const int MinimumResolutionScale = 1;
    internal const int MaximumResolutionScale = 8;

    private readonly ICoreClientAPI capi;
    private readonly object stateLock = new();

    private int generation;
    private bool captureActive;
    private bool busy;
    private bool cleanupPending;
    private bool cleanupWorkerScheduled;
    private AtlasScreenshotJobState jobState = AtlasScreenshotJobState.Idle;
    private CaptureJob? activeJob;
    private int gridSize = 1;
    private int tileIndex;
    private float baselineZoom;
    private float baselineYawDegrees;
    private float baselinePitchDegrees;
    private int captureAreaPercent = 100;
    private float capturedViewportAspect;
    private float projectionVerticalFactor = 1f;
    private int tileWidth;
    private int tileHeight;
    private int nominalTileWidth;
    private int nominalTileHeight;
    private int margin;
    private double worldPerPixel;
    private int storedWidth;
    private int storedHeight;
    private int readX;
    private int readY;
    private float downsampleScale = 1f;
    private byte[][]? tiles;
    private readonly List<Task<bool>> tilePersistenceTasks = new();
    private AtlasScreenshotPreview activePreview;
    private string? pendingPath;
    private string? completedPath;
    private string? completedError;
    private bool logReadbackFailureOnce;
    private readonly bool debugSaveTiles = !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("MODERNATLAS_DEBUG_TILES")
    );

    private sealed class ScreenshotManifest
    {
        public int Version { get; set; } = 2;
        public string JobId { get; set; } = "";
        public string Stage { get; set; } = "capturing";
        public string UpdatedUtc { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string PartPath { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public string TileDirectory { get; set; } = "";
        public string TileFilePattern { get; set; } = "tile-0000.bin";
        public int ResolutionScale { get; set; }
        public int CaptureAreaPercent { get; set; }
        public int TotalTiles { get; set; }
        public int CapturedTiles { get; set; }
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
        public long OutputPixels { get; set; }
        public int RequestedWidth { get; set; }
        public int RequestedHeight { get; set; }
        public long RequestedPixels { get; set; }
        public bool TileDataCleaned { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Immutable settings and paths for one screenshot job. Background work
    /// never reads mutable dialog/configuration fields directly.
    /// </summary>
    private sealed class CaptureJob
    {
        public string Id { get; init; } = "";
        public string OutputPath { get; init; } = "";
        public string JobRoot { get; init; } = "";
        public string WorkingDirectory { get; init; } = "";
        public string ManifestPath { get; init; } = "";
        public string TileDirectory { get; init; } = "";
        public string PartPath { get; init; } = "";
        public int GridSize { get; init; }
        public int CaptureAreaPercent { get; init; }
        public float BaselineZoom { get; init; }
        public float BaselineYawDegrees { get; init; }
        public float BaselinePitchDegrees { get; init; }
        public float ViewportAspect { get; init; }
        public CaptureLayout Layout { get; init; }
        public AtlasScreenshotPreview Preview { get; init; }

        public int TotalTiles => GridSize * GridSize;
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public AtlasTiledScreenshot(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    /// <summary>True while a job or its exact staging cleanup is active.</summary>
    public bool Busy
    {
        get
        {
            lock (stateLock)
            {
                return cleanupPending || captureActive || busy;
            }
        }
    }

    public AtlasScreenshotJobState State
    {
        get
        {
            lock (stateLock)
            {
                return jobState;
            }
        }
    }

    public string? ActiveJobId
    {
        get
        {
            lock (stateLock)
            {
                return activeJob?.Id;
            }
        }
    }

    /// <summary>True while the tile grid is still being captured.</summary>
    public bool CaptureActive
    {
        get
        {
            lock (stateLock)
            {
                return jobState == AtlasScreenshotJobState.Capturing;
            }
        }
    }

    public int GridSize
    {
        get
        {
            lock (stateLock)
            {
                return gridSize;
            }
        }
    }

    public int TotalTiles
    {
        get
        {
            lock (stateLock)
            {
                return gridSize * gridSize;
            }
        }
    }

    /// <summary>Index of the tile that should be rendered next.</summary>
    public int CurrentTile
    {
        get
        {
            lock (stateLock)
            {
                return tileIndex;
            }
        }
    }

    /// <summary>
    /// Multiplier for the capture projection's vertical half-extent. In scroll
    /// presentation the map viewport is taller than the window aspect ratio,
    /// so the orthographic projection must cover more world height than the
    /// nominal zoom before the tile sub-region is cropped from Primary.
    /// </summary>
    public float ProjectionVerticalFactor
    {
        get
        {
            lock (stateLock)
            {
                return projectionVerticalFactor;
            }
        }
    }

    public string? LastSavedPath
    {
        get
        {
            lock (stateLock)
            {
                return completedPath;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (stateLock)
            {
                return completedError;
            }
        }
    }

    public string? PendingPath
    {
        get
        {
            lock (stateLock)
            {
                return pendingPath;
            }
        }
    }

    /// <summary>Preview for the currently active capture, if one is running.</summary>
    public AtlasScreenshotPreview ActivePreview
    {
        get
        {
            lock (stateLock)
            {
                return activePreview;
            }
        }
    }

    /// <summary>
    /// Maps arbitrary persisted values to one of the four supported centered
    /// capture areas. This keeps older or manually edited config files safe.
    /// </summary>
    internal static int NormalizeCaptureAreaPercent(int percent)
    {
        if (percent >= 88) return 100;
        if (percent >= 63) return 75;
        if (percent >= 38) return 50;
        return 25;
    }

    /// <summary>
    /// Calculates the exact PNG dimensions for the current framebuffer without
    /// starting a capture or changing camera state.
    /// </summary>
    public AtlasScreenshotPreview GetPreview(
        int requestedResolutionScale,
        int requestedCaptureAreaPercent,
        float viewportAspect
    )
    {
        int resolutionScale = Math.Clamp(
            requestedResolutionScale,
            MinimumResolutionScale,
            MaximumResolutionScale
        );
        int captureAreaPercent = NormalizeCaptureAreaPercent(
            requestedCaptureAreaPercent
        );
        CaptureLayout layout = CalculateCaptureLayout(
            resolutionScale,
            viewportAspect
        );
        return BuildPreview(layout, resolutionScale, captureAreaPercent);
    }

    /// <summary>
    /// Starts a tiled capture of the current view. Must be called on the
    /// render thread while the atlas is open. The camera baseline is fixed by
    /// the caller through <see cref="GetTileCamera"/>.
    /// </summary>
    public bool StartCapture(
        int requestedGridSize,
        int requestedCaptureAreaPercent,
        float zoom,
        float yawDegrees,
        float pitchDegrees,
        float viewportAspect
    )
    {
        lock (stateLock)
        {
            if (captureActive || busy || cleanupPending) return false;
        }

        gridSize = Math.Clamp(
            requestedGridSize,
            MinimumResolutionScale,
            MaximumResolutionScale
        );
        captureAreaPercent = NormalizeCaptureAreaPercent(
            requestedCaptureAreaPercent
        );
        baselineZoom = Math.Max(1f, zoom);
        baselineYawDegrees = yawDegrees;
        baselinePitchDegrees = pitchDegrees;
        capturedViewportAspect = Math.Max(0.05f, viewportAspect);

        CaptureLayout layout = CalculateCaptureLayout(
            gridSize,
            capturedViewportAspect
        );
        if (!layout.IsValid)
        {
            lock (stateLock)
            {
                completedError =
                    $"The game window is too small for a tiled capture ({layout.FrameWidth}x{layout.FrameHeight}).";
            }
            return false;
        }
        projectionVerticalFactor = layout.ProjectionVerticalFactor;
        tileWidth = layout.TileWidth;
        tileHeight = layout.TileHeight;
        nominalTileWidth = layout.NominalTileWidth;
        nominalTileHeight = layout.NominalTileHeight;
        margin = layout.Margin;
        storedWidth = layout.StoredWidth;
        storedHeight = layout.StoredHeight;
        readX = layout.ReadX;
        readY = layout.ReadY;

        // The tile camera is shifted by exactly one integer tile of Primary
        // pixels per grid step, in screen space. The camera is a tilted
        // LookAt, so a ground-plane forward offset moves the screen image by
        // only sin(pitch) pixels vertically; the compensating world-Y
        // component covers the rest, including the horizontal 0-degree
        // Creative pitch where ground offsets do not move the image
        // vertically at all.
        float captureArea = captureAreaPercent / 100f;
        float tileZoom = baselineZoom * captureArea / gridSize;
        worldPerPixel = 2.0 * tileZoom * projectionVerticalFactor
            / layout.FrameHeight;
        downsampleScale = layout.DownsampleScale;
        activePreview = BuildPreview(layout, gridSize, captureAreaPercent);

        CaptureJob job;
        try
        {
            string jobId = Guid.NewGuid().ToString("N");
            string outputPath = BuildOutputPath(jobId);
            string jobRoot = BuildJobRoot();
            string workingDirectory = Path.Combine(jobRoot, $"job-{jobId}");
            string privateTileDirectory = Path.Combine(
                workingDirectory,
                "tiles"
            );
            Directory.CreateDirectory(privateTileDirectory);
            job = new CaptureJob
            {
                Id = jobId,
                OutputPath = outputPath,
                JobRoot = jobRoot,
                WorkingDirectory = workingDirectory,
                ManifestPath = Path.Combine(workingDirectory, "job.json"),
                TileDirectory = privateTileDirectory,
                PartPath = Path.Combine(workingDirectory, "atlas.png.part"),
                GridSize = gridSize,
                CaptureAreaPercent = captureAreaPercent,
                BaselineZoom = baselineZoom,
                BaselineYawDegrees = baselineYawDegrees,
                BaselinePitchDegrees = baselinePitchDegrees,
                ViewportAspect = capturedViewportAspect,
                Layout = layout,
                Preview = activePreview
            };
            pendingPath = job.OutputPath;
        }
        catch (Exception exception)
        {
            lock (stateLock)
            {
                completedError = $"Cannot create screenshot folder: {exception.Message}";
            }
            return false;
        }

        tiles = new byte[gridSize * gridSize][];
        tileIndex = 0;
        lock (stateLock)
        {
            completedPath = null;
            completedError = null;
            captureActive = true;
            busy = true;
            cleanupPending = false;
            cleanupWorkerScheduled = false;
            jobState = AtlasScreenshotJobState.Capturing;
            activeJob = job;
            tilePersistenceTasks.Clear();
        }
        WriteManifestSnapshot(
            "capturing",
            0,
            null,
            false
        );
        capi.Logger.Notification(
            "[ModernAtlas] Started a {0}x{0} tiled atlas screenshot of the centered {1}% area at {2}x{3} pixels ({4:0.0} MP), {5:0.##}x detail{6}.",
            gridSize,
            captureAreaPercent,
            activePreview.OutputWidth,
            activePreview.OutputHeight,
            activePreview.OutputMegapixels,
            activePreview.EffectiveDetailFactor,
            activePreview.WasDownsampled
                ? $"; requested {activePreview.RequestedWidth}x{activePreview.RequestedHeight} ({activePreview.RequestedMegapixels:0.0} MP), capped at {MaximumStitchedPixels / 1_000_000d:0} MP"
                : ""
        );
        return true;
    }

    private readonly struct CaptureLayout
    {
        public bool IsValid { get; }
        public int FrameWidth { get; }
        public int FrameHeight { get; }
        public float ProjectionVerticalFactor { get; }
        public int TileWidth { get; }
        public int TileHeight { get; }
        public int NominalTileWidth { get; }
        public int NominalTileHeight { get; }
        public int Margin { get; }
        public int StoredWidth { get; }
        public int StoredHeight { get; }
        public int ReadX { get; }
        public int ReadY { get; }
        public int RequestedWidth { get; }
        public int RequestedHeight { get; }
        public int OutputWidth { get; }
        public int OutputHeight { get; }
        public long RequestedPixels { get; }
        public long OutputPixels { get; }
        public float DownsampleScale { get; }

        public CaptureLayout(
            bool isValid,
            int frameWidth,
            int frameHeight,
            float projectionVerticalFactor,
            int tileWidth,
            int tileHeight,
            int nominalTileWidth,
            int nominalTileHeight,
            int margin,
            int storedWidth,
            int storedHeight,
            int readX,
            int readY,
            int requestedWidth,
            int requestedHeight,
            int outputWidth,
            int outputHeight,
            long requestedPixels,
            long outputPixels,
            float downsampleScale
        )
        {
            IsValid = isValid;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
            ProjectionVerticalFactor = projectionVerticalFactor;
            TileWidth = tileWidth;
            TileHeight = tileHeight;
            NominalTileWidth = nominalTileWidth;
            NominalTileHeight = nominalTileHeight;
            Margin = margin;
            StoredWidth = storedWidth;
            StoredHeight = storedHeight;
            ReadX = readX;
            ReadY = readY;
            RequestedWidth = requestedWidth;
            RequestedHeight = requestedHeight;
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;
            RequestedPixels = requestedPixels;
            OutputPixels = outputPixels;
            DownsampleScale = downsampleScale;
        }
    }

    private CaptureLayout CalculateCaptureLayout(
        int requestedResolutionScale,
        float viewportAspect
    )
    {
        int resolutionScale = Math.Clamp(
            requestedResolutionScale,
            MinimumResolutionScale,
            MaximumResolutionScale
        );
        int frameWidth = Math.Max(1, capi.Render.FrameWidth);
        int frameHeight = Math.Max(1, capi.Render.FrameHeight);
        if (frameWidth < 96 || frameHeight < 96)
        {
            return new CaptureLayout(
                false,
                frameWidth,
                frameHeight,
                1,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                1
            );
        }

        float safeViewportAspect = Math.Max(0.05f, viewportAspect);
        float frameAspect = frameWidth / (float)frameHeight;
        float projectionVerticalFactor = Math.Max(
            1f,
            safeViewportAspect / frameAspect
        );
        int tileWidth = Math.Clamp(
            (int)Math.Round(
                frameWidth
                    * safeViewportAspect
                    / (frameAspect * projectionVerticalFactor)
            ),
            1,
            frameWidth
        );
        int tileHeight = Math.Clamp(
            (int)Math.Round(frameHeight / projectionVerticalFactor),
            1,
            frameHeight
        );

        // A small overlap on every shared edge lets the stitcher crossfade
        // the seams, hiding the sub-pixel drift that different camera
        // positions introduce through the float pipeline. Each captured
        // frame stores the nominal tile plus `margin` extra pixels on every
        // side that the previous and next tile also captured.
        int margin = Math.Max(
            2,
            (int)Math.Round(0.006f * Math.Min(tileWidth, tileHeight))
        );
        int nominalTileWidth = Math.Max(96, tileWidth - margin * 2);
        int nominalTileHeight = Math.Max(96, tileHeight - margin * 2);
        int storedWidth = Math.Min(frameWidth, nominalTileWidth + margin * 2);
        int storedHeight = Math.Min(frameHeight, nominalTileHeight + margin * 2);
        int readX = (frameWidth - storedWidth) / 2;
        int readY = (frameHeight - storedHeight) / 2;

        long requestedWidth = checked(
            (long)resolutionScale * nominalTileWidth + 2L * margin
        );
        long requestedHeight = checked(
            (long)resolutionScale * nominalTileHeight + 2L * margin
        );
        long requestedPixels = checked(requestedWidth * requestedHeight);
        float downsampleScale = requestedPixels > MaximumStitchedPixels
            ? (float)Math.Sqrt(
                MaximumStitchedPixels / (double)requestedPixels
            )
            : 1f;
        int outputWidth = 0;
        int outputHeight = 0;
        long outputPixels = 0;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            outputWidth = checked(
                resolutionScale * Math.Max(
                    1,
                    (int)Math.Round(nominalTileWidth * downsampleScale)
                )
                + 2
                    * Math.Max(
                        1,
                        (int)Math.Round(margin * downsampleScale)
                    )
            );
            outputHeight = checked(
                resolutionScale * Math.Max(
                    1,
                    (int)Math.Round(nominalTileHeight * downsampleScale)
                )
                + 2
                    * Math.Max(
                        1,
                        (int)Math.Round(margin * downsampleScale)
                    )
            );
            outputPixels = checked((long)outputWidth * outputHeight);
            if (outputPixels <= MaximumStitchedPixels) break;

            // Rounding each tile dimension can leave the first square-root
            // estimate a few pixels over budget. Nudge the scale down before
            // repeating so the promised cap also holds for the actual PNG.
            downsampleScale *= 0.999f * (float)Math.Sqrt(
                MaximumStitchedPixels / (double)outputPixels
            );
        }

        return new CaptureLayout(
            true,
            frameWidth,
            frameHeight,
            projectionVerticalFactor,
            tileWidth,
            tileHeight,
            nominalTileWidth,
            nominalTileHeight,
            margin,
            storedWidth,
            storedHeight,
            readX,
            readY,
            checked((int)requestedWidth),
            checked((int)requestedHeight),
            outputWidth,
            outputHeight,
            requestedPixels,
            outputPixels,
            downsampleScale
        );
    }

    private static AtlasScreenshotPreview BuildPreview(
        CaptureLayout layout,
        int resolutionScale,
        int captureAreaPercent
    )
    {
        if (!layout.IsValid)
        {
            return new AtlasScreenshotPreview(
                false,
                resolutionScale,
                captureAreaPercent,
                0,
                0,
                0,
                0,
                0,
                0,
                1,
                0
            );
        }

        return new AtlasScreenshotPreview(
            true,
            resolutionScale,
            captureAreaPercent,
            layout.RequestedWidth,
            layout.RequestedHeight,
            layout.OutputWidth,
            layout.OutputHeight,
            layout.RequestedPixels,
            layout.OutputPixels,
            layout.DownsampleScale,
            resolutionScale * (100f / captureAreaPercent)
        );
    }

    /// <summary>
    /// Camera values for one tile. The returned zoom and center deltas move
    /// the tile's share of the original view to the viewport center, with
    /// the vertical component decomposed into ground-forward and world-Y
    /// parts for the tilted LookAt camera.
    /// </summary>
    public void GetTileCamera(
        int tile,
        out float tileZoom,
        out double centerOffsetX,
        out double centerOffsetZ,
        out double centerOffsetY
    )
    {
        int col = tile % gridSize;
        int row = tile / gridSize;
        tileZoom = baselineZoom * (captureAreaPercent / 100f) / gridSize;
        double columnMid = col - (gridSize - 1) / 2.0;
        double rowMid = row - (gridSize - 1) / 2.0;
        double yaw = baselineYawDegrees * GameMath.DEG2RAD;
        double pitch = baselinePitchDegrees * GameMath.DEG2RAD;
        double rightX = Math.Cos(yaw);
        double rightZ = -Math.Sin(yaw);
        double forwardX = Math.Sin(yaw);
        double forwardZ = Math.Cos(yaw);
        // Screen-space pixel offsets scaled into world units. The eye moves
        // opposite the intended image shift; the vertical screen shift is
        // produced by a ground-forward part (sin pitch) plus a world-Y lift
        // (cos pitch) so it stays exact from top-down to horizontal views.
        // Signs follow the working drag convention: dragging down moves the
        // eye backward and the map content down, so tile row 0 (the top of
        // the stitched output) captures the content above the view center.
        double deltaRight = columnMid * nominalTileWidth * worldPerPixel;
        double deltaForward =
            rowMid * nominalTileHeight * worldPerPixel * Math.Sin(pitch);
        double deltaY =
            -rowMid * nominalTileHeight * worldPerPixel * Math.Cos(pitch);
        centerOffsetX = deltaRight * rightX + deltaForward * forwardX;
        centerOffsetZ = deltaRight * rightZ + deltaForward * forwardZ;
        centerOffsetY = deltaY;
    }

    /// <summary>
    /// Reads the just-rendered Primary frame region for the current tile.
    /// Call from the render thread right after the atlas world render that
    /// used <see cref="GetTileCamera"/> for <see cref="CurrentTile"/>.
    /// </summary>
    public bool CaptureCurrentFrame(FrameBufferRef sourceFramebuffer)
    {
        CaptureJob? captureJob;
        int capturedTile;
        CaptureLayout captureLayout;
        lock (stateLock)
        {
            if (jobState != AtlasScreenshotJobState.Capturing
                || !captureActive
                || activeJob == null
                || tiles == null)
            {
                return false;
            }
            captureJob = activeJob;
            capturedTile = tileIndex;
            captureLayout = captureJob.Layout;
        }

        try
        {
            IRenderAPI render = capi.Render;
            if (sourceFramebuffer.Disposed
                || sourceFramebuffer.ColorTextureIds is not { Length: > 0 })
            {
                return false;
            }
            render.CurrentFrameBuffer = sourceFramebuffer;
            // The engine's OIT machinery can leave GL_READ_BUFFER on one of
            // its multi-attachment color buffers. Binding Primary does not
            // reset it; an inherited foreign read buffer returns invalid
            // pixels for our single color attachment.
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            // Texture uploads can leave GL_PACK_ROW_LENGTH set for
            // sub-rectangle transfers. A stale row length shifts every row
            // and turns the capture into diagonal stripes.
            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
            GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

            byte[] pixels = new byte[
                checked(captureLayout.StoredWidth * captureLayout.StoredHeight * 4)
            ];
            GL.ReadPixels(
                captureLayout.ReadX,
                captureLayout.ReadY,
                captureLayout.StoredWidth,
                captureLayout.StoredHeight,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                pixels
            );
            byte[]? capturedTilePixels = null;
            lock (stateLock)
            {
                if (jobState == AtlasScreenshotJobState.Capturing
                    && captureActive
                    && ReferenceEquals(activeJob, captureJob)
                    && tiles != null
                    && capturedTile < tiles.Length)
                {
                    capturedTilePixels = DownsampleToBudget(
                        pixels,
                        captureLayout.StoredWidth,
                        captureLayout.StoredHeight,
                        captureLayout.DownsampleScale
                    );
                    tiles[capturedTile] = capturedTilePixels;
                }
            }
            if (capturedTilePixels != null)
            {
                if (!QueueTilePersistence(
                    captureJob,
                    capturedTile,
                    capturedTilePixels
                ))
                {
                    return false;
                }
                WriteManifestForJob(
                    captureJob,
                    "Capturing",
                    capturedTile + 1,
                    null
                );
                if (debugSaveTiles)
                {
                    SaveDebugTile(
                        capturedTile,
                        pixels,
                        captureLayout.StoredWidth,
                        captureLayout.StoredHeight
                    );
                }
            }
        }
        catch (Exception exception)
        {
            if (!logReadbackFailureOnce)
            {
                logReadbackFailureOnce = true;
                capi.Logger.Error(
                    "[ModernAtlas] Could not read back tile {0} of {1}: {2}",
                    capturedTile + 1,
                    TotalTiles,
                    exception.Message
                );
            }
            lock (stateLock)
            {
                if (ReferenceEquals(activeJob, captureJob))
                {
                    captureActive = false;
                    busy = true;
                    cleanupPending = true;
                    jobState = AtlasScreenshotJobState.Failed;
                    completedPath = null;
                    completedError = exception.Message;
                }
            }
            return false;
        }
        finally
        {
            capi.Render.CurrentFrameBuffer = null;
            // The window target's correct read buffer is the back buffer.
            GL.ReadBuffer(ReadBufferMode.Back);
        }

        lock (stateLock)
        {
            if (jobState != AtlasScreenshotJobState.Capturing
                || !captureActive
                || !ReferenceEquals(activeJob, captureJob))
            {
                return false;
            }
            tileIndex++;
            if (tileIndex < captureJob.TotalTiles) return true;

            captureActive = false;
            busy = true;
            jobState = AtlasScreenshotJobState.Stitching;
            cleanupPending = true;
        }
        StartBackgroundStitch();
        return true;
    }

    /// <summary>
    /// Cancels the capture. A running background save aborts without exposing
    /// its private partial PNG. The final committed image is never removed by
    /// a late modal close or world-dispose callback.
    /// </summary>
    public void Cancel()
    {
        CaptureJob? cancelledJob;
        Task<bool>[] pendingTileTasks;
        bool scheduleLocalCleanup;
        bool preserveFailure;
        lock (stateLock)
        {
            if (activeJob == null || (!captureActive && !busy && !cleanupPending))
                return;
            // Once the atomic move and validation succeeded, the public PNG is
            // committed. A close/dispose arriving during the tiny cleanup
            // window must not turn a successful capture into a deletion.
            if (jobState == AtlasScreenshotJobState.Committed) return;

            cancelledJob = activeJob;
            preserveFailure = jobState == AtlasScreenshotJobState.Failed;
            pendingTileTasks = tilePersistenceTasks.ToArray();
            tilePersistenceTasks.Clear();
            generation++;
            captureActive = false;
            busy = true;
            cleanupPending = true;
            if (!preserveFailure)
            {
                jobState = AtlasScreenshotJobState.Cancelled;
                completedPath = null;
                completedError = null;
            }
            tiles = null;
            scheduleLocalCleanup = !cleanupWorkerScheduled;
            if (scheduleLocalCleanup) cleanupWorkerScheduled = true;
        }

        if (!scheduleLocalCleanup || cancelledJob == null) return;

        CaptureJob job = cancelledJob;
        Task.Run(
            () => FinishUnstartedJobCleanup(
                job,
                pendingTileTasks,
                preserveFailure
            )
        );
    }

    private void FinishUnstartedJobCleanup(
        CaptureJob job,
        Task<bool>[] pendingTileTasks,
        bool preserveFailure
    )
    {
        try
        {
            if (pendingTileTasks.Length > 0)
            {
                Task.WhenAll(pendingTileTasks).GetAwaiter().GetResult();
            }
            WriteManifestForJob(
                job,
                preserveFailure ? "Failed" : "Cancelled",
                CurrentCapturedTileCount(job),
                preserveFailure ? LastError : null
            );
        }
        finally
        {
            bool cleaned = TryDeleteJobDirectory(job);
            lock (stateLock)
            {
                if (ReferenceEquals(activeJob, job))
                {
                    cleanupWorkerScheduled = false;
                    cleanupPending = false;
                    busy = false;
                    captureActive = false;
                    if (!cleaned)
                    {
                        string cleanupMessage =
                            $"Screenshot job {job.Id} private staging cleanup failed.";
                        completedError = completedError == null
                            ? cleanupMessage
                            : $"{completedError} {cleanupMessage}";
                    }
                }
            }
        }
    }

    /// <summary>Forgets the previous saved-path/error status.</summary>
    public void ClearStatus()
    {
        lock (stateLock)
        {
            completedPath = null;
            completedError = null;
        }
    }

    private string BuildOutputPath(string jobId)
    {
        string folder = capi.GetOrCreateDataPath(
            Path.Combine("Screenshots", "ModernAtlas")
        );
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string path = Path.Combine(folder, $"ModernAtlas_{stamp}.png");
        if (!File.Exists(path)) return path;

        string suffix = jobId[..Math.Min(8, jobId.Length)];
        int attempt = 0;
        do
        {
            string attemptSuffix = attempt == 0
                ? suffix
                : $"{suffix}_{attempt}";
            path = Path.Combine(
                folder,
                $"ModernAtlas_{stamp}_{attemptSuffix}.png"
            );
            attempt++;
        }
        while (File.Exists(path));
        return path;
    }

    private string BuildJobRoot()
    {
        return capi.GetOrCreateDataPath(
            Path.Combine("ModData", "ModernAtlas", "ScreenshotJobs")
        );
    }

    /// <summary>
    /// Diagnostic: saves the raw readback of one tile so the per-tile camera
    /// alignment can be verified independently of the stitcher.
    /// </summary>
    private void SaveDebugTile(
        int tile,
        byte[] bgra,
        int debugStoredWidth,
        int debugStoredHeight
    )
    {
        string? folder = Environment.GetEnvironmentVariable(
            "MODERNATLAS_DEBUG_TILES"
        );
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"tile{tile}.png");
            byte[] rgb = new byte[
                checked(debugStoredWidth * debugStoredHeight * 3)
            ];
            for (int y = 0; y < debugStoredHeight; y++)
            {
                // ReadPixels rows are bottom-up; write top-down rows.
                int sourceRow =
                    (debugStoredHeight - 1 - y) * debugStoredWidth * 4;
                int targetRow = y * debugStoredWidth * 3;
                for (int x = 0; x < debugStoredWidth; x++)
                {
                    int s = sourceRow + x * 4;
                    int t = targetRow + x * 3;
                    rgb[t] = bgra[s + 2];
                    rgb[t + 1] = bgra[s + 1];
                    rgb[t + 2] = bgra[s];
                }
            }
            AtlasPngEncoder.SavePng(
                rgb,
                debugStoredWidth,
                debugStoredHeight,
                path
            );
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not save the debug tile {0}: {1}",
                tile,
                exception.Message
            );
        }
    }

    private void StartBackgroundStitch()
    {
        CaptureJob? job;
        int capturedGeneration;
        byte[][]? currentTiles;
        Task<bool>[] capturedTilePersistenceTasks;
        lock (stateLock)
        {
            job = activeJob;
            capturedGeneration = generation;
            currentTiles = tiles;
            capturedTilePersistenceTasks = tilePersistenceTasks.ToArray();
            tilePersistenceTasks.Clear();
            if (job != null && currentTiles != null)
            {
                jobState = AtlasScreenshotJobState.Stitching;
                cleanupPending = true;
            }
        }
        if (job == null || currentTiles == null)
        {
            if (job != null)
            {
                ScheduleUnstartedJobCleanup(
                    job,
                    capturedTilePersistenceTasks,
                    currentTiles == null
                        ? "No tiles were captured for the screenshot."
                        : "The screenshot job was lost before stitching started."
                );
            }
            return;
        }

        // Keep the worker independent from the mutable capture state. Cancel()
        // deliberately releases the large tile array when the world closes,
        // so the background stitch must own its own outer array snapshot.
        byte[][] capturedTiles = new byte[currentTiles.Length][];
        Array.Copy(currentTiles, capturedTiles, currentTiles.Length);
        for (int tile = 0; tile < capturedTiles.Length; tile++)
        {
            if (capturedTiles[tile] == null)
            {
                ScheduleUnstartedJobCleanup(
                    job,
                    capturedTilePersistenceTasks,
                    $"Tile {tile + 1} was not captured for the screenshot."
                );
                return;
            }
        }

        TryDeleteFile(job.PartPath);

        lock (stateLock)
        {
            if (!ReferenceEquals(activeJob, job)
                || generation != capturedGeneration
                || jobState != AtlasScreenshotJobState.Stitching)
            {
                return;
            }
            cleanupWorkerScheduled = true;
        }
        Task.Run(
            () => RunBackgroundStitch(
                job,
                capturedGeneration,
                capturedTiles,
                capturedTilePersistenceTasks
            )
        );
    }

    private void ScheduleUnstartedJobCleanup(
        CaptureJob job,
        Task<bool>[] pendingTileTasks,
        string error
    )
    {
        bool schedule;
        lock (stateLock)
        {
            if (!ReferenceEquals(activeJob, job)) return;
            jobState = AtlasScreenshotJobState.Failed;
            captureActive = false;
            busy = true;
            cleanupPending = true;
            completedPath = null;
            completedError = error;
            schedule = !cleanupWorkerScheduled;
            if (schedule) cleanupWorkerScheduled = true;
        }
        if (!schedule) return;
        Task.Run(
            () => FinishUnstartedJobCleanup(job, pendingTileTasks, true)
        );
    }

    private void RunBackgroundStitch(
        CaptureJob job,
        int capturedGeneration,
        byte[][] capturedTiles,
        Task<bool>[] capturedTilePersistenceTasks
    )
    {
        Exception? failure = null;
        bool cancelled = false;
        bool movedToPublic = false;
        try
        {
            if (IsGenerationCancelled(capturedGeneration))
            {
                cancelled = true;
            }
            else
            {
                WriteManifestForJob(job, "Stitching", capturedTiles.Length);
                bool[] persistenceResults = Task.WhenAll(
                        capturedTilePersistenceTasks
                    )
                    .GetAwaiter()
                    .GetResult();
                for (int tile = 0; tile < persistenceResults.Length; tile++)
                {
                    if (!persistenceResults[tile])
                    {
                        throw new IOException(
                            $"Could not persist tile {tile + 1} of {capturedTiles.Length}."
                        );
                    }
                }
                ThrowIfGenerationCancelled(capturedGeneration);

                using (AtlasPngStreamWriter writer = new(
                    job.PartPath,
                    job.Preview.OutputWidth,
                    job.Preview.OutputHeight
                ))
                {
                    StitchToWriter(
                        writer,
                        job.Preview.OutputWidth,
                        job.Preview.OutputHeight,
                        capturedTiles,
                        job.GridSize,
                        job.Layout.NominalTileWidth,
                        job.Layout.NominalTileHeight,
                        job.Layout.StoredWidth,
                        job.Layout.StoredHeight,
                        job.Layout.Margin,
                        job.Layout.DownsampleScale,
                        () => IsGenerationCancelled(capturedGeneration)
                    );
                }
                ThrowIfGenerationCancelled(capturedGeneration);

                SetJobState(job, AtlasScreenshotJobState.Validating, true);
                WriteManifestForJob(job, "Validating", capturedTiles.Length);
                EnsureValidPng(
                    job.PartPath,
                    job.Preview.OutputWidth,
                    job.Preview.OutputHeight
                );
                ThrowIfGenerationCancelled(capturedGeneration);

                // The public folder sees the image only at this atomic move
                // point. All manifests, tiles and .part data stay private.
                File.Move(job.PartPath, job.OutputPath, false);
                movedToPublic = true;
                EnsureValidPng(
                    job.OutputPath,
                    job.Preview.OutputWidth,
                    job.Preview.OutputHeight
                );
                ThrowIfGenerationCancelled(capturedGeneration);

                lock (stateLock)
                {
                    if (!ReferenceEquals(activeJob, job)
                        || generation != capturedGeneration)
                    {
                        cancelled = true;
                    }
                    else
                    {
                        jobState = AtlasScreenshotJobState.Committed;
                        completedPath = job.OutputPath;
                        completedError = null;
                        cleanupPending = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            IsGenerationCancelled(capturedGeneration)
        )
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            if (IsGenerationCancelled(capturedGeneration))
            {
                cancelled = true;
            }
            else
            {
                failure = exception;
            }
        }

        if (cancelled || failure != null)
        {
            if (movedToPublic) TryDeleteFile(job.OutputPath);
            if (cancelled)
            {
                SetJobState(job, AtlasScreenshotJobState.Cancelled, true);
                WriteManifestForJob(job, "Cancelled", capturedTiles.Length);
            }
            else
            {
                SetJobState(
                    job,
                    AtlasScreenshotJobState.Failed,
                    true,
                    failure?.Message ?? "The screenshot job failed."
                );
                WriteManifestForJob(
                    job,
                    "Failed",
                    capturedTiles.Length,
                    failure?.Message ?? "The screenshot job failed."
                );
            }
            TryDeleteFile(job.PartPath);
        }
        else
        {
            WriteManifestForJob(job, "Committed", capturedTiles.Length);
        }

        bool cleaned = TryDeleteJobDirectory(job);
        lock (stateLock)
        {
            if (ReferenceEquals(activeJob, job))
            {
                cleanupPending = false;
                busy = false;
                captureActive = false;
                cleanupWorkerScheduled = false;
                if (!cleaned)
                {
                    string cleanupMessage =
                        $"Screenshot job {job.Id} committed state, but private staging cleanup failed.";
                    completedError = completedError == null
                        ? cleanupMessage
                        : $"{completedError} {cleanupMessage}";
                }
            }
        }

        if (failure != null)
        {
            AtlasScreenshotJobState failedState;
            lock (stateLock)
            {
                failedState = jobState;
            }
            capi.Logger.Error(
                "[ModernAtlas] Tiled screenshot job {0} failed during {1}: {2}",
                job.Id,
                StateName(failedState),
                failure.ToString()
            );
        }
        else if (!cancelled)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Finished tiled atlas screenshot job {0}: {1} ({2}x{3}, {4:0.0} MP).",
                job.Id,
                job.OutputPath,
                job.Preview.OutputWidth,
                job.Preview.OutputHeight,
                job.Preview.OutputMegapixels
            );
        }
    }

    private bool IsGenerationCancelled(int expectedGeneration)
    {
        lock (stateLock)
        {
            return generation != expectedGeneration;
        }
    }

    private void ThrowIfGenerationCancelled(int expectedGeneration)
    {
        if (IsGenerationCancelled(expectedGeneration))
        {
            throw new OperationCanceledException();
        }
    }

    private void SetJobState(
        CaptureJob job,
        AtlasScreenshotJobState state,
        bool keepBusy,
        string? error = null
    )
    {
        lock (stateLock)
        {
            if (!ReferenceEquals(activeJob, job)) return;
            jobState = state;
            cleanupPending = keepBusy;
            if (error != null)
            {
                completedPath = null;
                completedError = error;
            }
        }
    }

    private void WriteManifestForJob(
        CaptureJob job,
        string stage,
        int capturedTiles,
        string? error = null,
        bool tileDataCleaned = false
    )
    {
        // Serialize the small metadata file while holding the job lock. This
        // prevents a concurrent Cancel/world-dispose cleanup from deleting
        // the exact job directory halfway through a manifest transaction.
        lock (stateLock)
        {
            if (!ReferenceEquals(activeJob, job)) return;
            WriteManifestFile(
                job.ManifestPath,
                job.Id,
                job.OutputPath,
                job.PartPath,
                job.TileDirectory,
                stage,
                job.GridSize,
                job.CaptureAreaPercent,
                job.TotalTiles,
                capturedTiles,
                job.Preview,
                error,
                tileDataCleaned
            );
        }
    }

    private static string StateName(AtlasScreenshotJobState state) =>
        state.ToString();

    internal static void ValidateCommittedPngForAutomation(string path)
    {
        EnsureValidPng(path, 0, 0);
    }

    private static void EnsureValidPng(
        string path,
        int expectedWidth,
        int expectedHeight
    )
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            byte[] signature = new byte[8];
            stream.ReadExactly(signature);
            byte[] expectedSignature =
            {
                0x89,
                0x50,
                0x4e,
                0x47,
                0x0d,
                0x0a,
                0x1a,
                0x0a
            };
            for (int index = 0; index < expectedSignature.Length; index++)
            {
                if (signature[index] != expectedSignature[index])
                {
                    throw new InvalidDataException(
                        "PNG signature is invalid."
                    );
                }
            }

            bool hasHeader = false;
            bool hasData = false;
            bool hasEnd = false;
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 12)
                {
                    throw new InvalidDataException(
                        "PNG ended with a truncated chunk header."
                    );
                }
                byte[] lengthBytes = new byte[4];
                stream.ReadExactly(lengthBytes);
                uint chunkLength = ReadBigEndianUInt32(lengthBytes);
                byte[] typeBytes = new byte[4];
                stream.ReadExactly(typeBytes);
                string chunkType = Encoding.ASCII.GetString(typeBytes);
                if (chunkLength > int.MaxValue
                    || stream.Length - stream.Position < chunkLength + 4)
                {
                    throw new InvalidDataException(
                        $"PNG chunk {chunkType} extends past the file end."
                    );
                }
                byte[] chunkData = new byte[(int)chunkLength];
                stream.ReadExactly(chunkData);
                byte[] crcBytes = new byte[4];
                stream.ReadExactly(crcBytes);
                uint expectedCrc = ReadBigEndianUInt32(crcBytes);
                uint actualCrc = AtlasPngCrc.Crc32(typeBytes, chunkData);
                if (expectedCrc != actualCrc)
                {
                    throw new InvalidDataException(
                        $"PNG chunk {chunkType} has an invalid CRC."
                    );
                }

                if (chunkType == "IHDR")
                {
                    if (chunkLength != 13 || hasHeader)
                    {
                        throw new InvalidDataException(
                            "PNG has an invalid IHDR chunk."
                        );
                    }
                    if (chunkData.Length != 13)
                    {
                        throw new InvalidDataException(
                            "PNG has an invalid IHDR chunk."
                        );
                    }
                    int width = checked((int)ReadBigEndianUInt32(chunkData, 0));
                    int height = checked((int)ReadBigEndianUInt32(chunkData, 4));
                    if ((expectedWidth > 0 && width != expectedWidth)
                        || (expectedHeight > 0 && height != expectedHeight))
                    {
                        throw new InvalidDataException(
                            $"PNG dimensions are {width}x{height}, expected {expectedWidth}x{expectedHeight}."
                        );
                    }
                    if (chunkData[8] != 8 || chunkData[9] != 2)
                    {
                        throw new InvalidDataException(
                            "PNG is not an 8-bit RGB image."
                        );
                    }
                    hasHeader = true;
                }
                else if (chunkType == "IDAT")
                {
                    hasData |= chunkData.Length > 0;
                }
                else if (chunkType == "IEND")
                {
                    if (chunkData.Length != 0)
                    {
                        throw new InvalidDataException(
                            "PNG has a non-empty IEND chunk."
                        );
                    }
                    hasEnd = true;
                }
                if (hasEnd)
                {
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException(
                            "PNG contains data after IEND."
                        );
                    }
                    break;
                }
            }

            if (!hasHeader || !hasData || !hasEnd)
            {
                throw new InvalidDataException(
                    "PNG is missing IHDR, IDAT or IEND."
                );
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"PNG could not be read completely: {exception.Message}",
                exception
            );
        }
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset = 0)
    {
        return ((uint)bytes[offset] << 24)
            | ((uint)bytes[offset + 1] << 16)
            | ((uint)bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of an abandoned sidecar.
        }
    }

    private bool QueueTilePersistence(
        CaptureJob job,
        int tile,
        byte[] pixels
    )
    {
        Task<bool> persistenceTask;
        lock (stateLock)
        {
            if (!ReferenceEquals(activeJob, job)
                || jobState != AtlasScreenshotJobState.Capturing)
            {
                return false;
            }

            string finalPath = Path.Combine(
                job.TileDirectory,
                $"tile-{tile:D4}.bin"
            );
            string temporaryPath = finalPath + ".part";
            persistenceTask = Task.Run(
                () =>
                {
                    try
                    {
                        File.WriteAllBytes(temporaryPath, pixels);
                        File.Move(temporaryPath, finalPath, true);
                        return true;
                    }
                    catch
                    {
                        TryDeleteFile(temporaryPath);
                        return false;
                    }
                }
            );
            tilePersistenceTasks.Add(persistenceTask);
        }
        return true;
    }

    private int CurrentCapturedTileCount(CaptureJob job)
    {
        lock (stateLock)
        {
            if (!ReferenceEquals(activeJob, job)) return 0;
            return Math.Clamp(tileIndex, 0, job.TotalTiles);
        }
    }

    private void WriteManifestSnapshot(
        string stage,
        int capturedTiles,
        string? error,
        bool tileDataCleaned
    )
    {
        CaptureJob? currentJob;
        lock (stateLock)
        {
            currentJob = activeJob;
        }
        if (currentJob == null) return;
        WriteManifestForJob(
            currentJob,
            stage,
            capturedTiles,
            error,
            tileDataCleaned
        );
    }

    private void WriteManifestFile(
        string currentManifestPath,
        string jobId,
        string outputPath,
        string currentPartPath,
        string currentTileDirectory,
        string stage,
        int resolutionScale,
        int areaPercent,
        int totalTiles,
        int capturedTiles,
        AtlasScreenshotPreview preview,
        string? error,
        bool tileDataCleaned
    )
    {
        string temporaryManifestPath = currentManifestPath + ".part";
        try
        {
            ScreenshotManifest manifest = new()
            {
                JobId = jobId,
                Stage = stage,
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                OutputPath = outputPath,
                PartPath = currentPartPath,
                ManifestPath = currentManifestPath,
                TileDirectory = currentTileDirectory,
                ResolutionScale = resolutionScale,
                CaptureAreaPercent = areaPercent,
                TotalTiles = totalTiles,
                CapturedTiles = Math.Clamp(capturedTiles, 0, totalTiles),
                OutputWidth = preview.OutputWidth,
                OutputHeight = preview.OutputHeight,
                OutputPixels = preview.OutputPixels,
                RequestedWidth = preview.RequestedWidth,
                RequestedHeight = preview.RequestedHeight,
                RequestedPixels = preview.RequestedPixels,
                TileDataCleaned = tileDataCleaned,
                Error = error
            };
            string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            File.WriteAllText(temporaryManifestPath, json);
            File.Move(temporaryManifestPath, currentManifestPath, true);
        }
        catch (Exception exception)
        {
            TryDeleteFile(temporaryManifestPath);
            capi.Logger.Warning(
                "[ModernAtlas] Could not update the tiled screenshot manifest: {0}",
                exception.Message
            );
        }
    }

    private static bool TryDeleteJobDirectory(CaptureJob job)
    {
        string expectedPath = Path.Combine(job.JobRoot, $"job-{job.Id}");
        string fullRoot = Path.GetFullPath(job.JobRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        string fullExpectedPath = Path.GetFullPath(expectedPath);
        string fullWorkingPath = Path.GetFullPath(job.WorkingDirectory);
        if (!string.Equals(
                fullExpectedPath,
                fullWorkingPath,
                StringComparison.Ordinal
            )
            || string.Equals(fullRoot, fullWorkingPath, StringComparison.Ordinal)
            || !fullWorkingPath.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            ))
        {
            return false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!Directory.Exists(fullWorkingPath)) return true;
            try
            {
                Directory.Delete(fullWorkingPath, true);
                return !Directory.Exists(fullWorkingPath);
            }
            catch
            {
                Thread.Sleep(25);
            }
        }
        return !Directory.Exists(fullWorkingPath);
    }

    /// <summary>
    /// Bilinear downsample of one BGRA tile to the pixel-budget size, keeping
    /// the overlap margin so the stitcher can crop identically at any scale.
    /// </summary>
    private static byte[] DownsampleToBudget(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        float scale
    )
    {
        int targetWidth = Math.Max(
            1,
            (int)Math.Round(sourceWidth * scale)
        );
        int targetHeight = Math.Max(
            1,
            (int)Math.Round(sourceHeight * scale)
        );
        if (targetWidth == sourceWidth && targetHeight == sourceHeight)
        {
            return source;
        }

        byte[] result = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            float sampleY = (y + 0.5f) / scale - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sampleY), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            float yBlend = Math.Clamp(sampleY - y0, 0f, 1f);
            for (int x = 0; x < targetWidth; x++)
            {
                float sampleX = (x + 0.5f) / scale - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sampleX), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                float xBlend = Math.Clamp(sampleX - x0, 0f, 1f);

                int outputOffset = (y * targetWidth + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    float top = source[(y0 * sourceWidth + x0) * 4 + channel]
                        * (1f - xBlend)
                        + source[(y0 * sourceWidth + x1) * 4 + channel] * xBlend;
                    float bottom = source[(y1 * sourceWidth + x0) * 4 + channel]
                        * (1f - xBlend)
                        + source[(y1 * sourceWidth + x1) * 4 + channel] * xBlend;
                    result[outputOffset + channel] = (byte)Math.Clamp(
                        (int)MathF.Round(top * (1f - yBlend) + bottom * yBlend),
                        0,
                        255
                    );
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Assembles all tiles into one top-down RGB image and streams it into
    /// the PNG writer band by band. Each tile is stored bottom-up with an
    /// overlap margin on every shared edge. Inside the overlap the two
    /// neighbouring tiles are linearly crossfaded, so the sub-pixel drift
    /// between separately positioned cameras blends into an invisible seam
    /// instead of a hard cut. The outer margins stay in the image so the
    /// composite covers exactly the world area of the live view and nothing
    /// at the picture edges is cropped. The output row order is tile row 0
    /// (top of the original view) first.
    /// </summary>
    private void StitchToWriter(
        AtlasPngStreamWriter writer,
        int outWidth,
        int outHeight,
        byte[][] capturedTiles,
        int capturedGridSize,
        int capturedNominalTileWidth,
        int capturedNominalTileHeight,
        int capturedStoredWidth,
        int capturedStoredHeight,
        int capturedMargin,
        float capturedDownsampleScale,
        Func<bool>? isCancelled
    )
    {
        int tileOutWidth = Math.Max(
            1,
            (int)Math.Round(capturedNominalTileWidth * capturedDownsampleScale)
        );
        int tileOutHeight = Math.Max(
            1,
            (int)Math.Round(capturedNominalTileHeight * capturedDownsampleScale)
        );
        int storedTileWidth = Math.Max(
            1,
            (int)Math.Round(capturedStoredWidth * capturedDownsampleScale)
        );
        int storedTileHeight = Math.Max(
            1,
            (int)Math.Round(capturedStoredHeight * capturedDownsampleScale)
        );
        int overlap = Math.Max(
            1,
            (int)Math.Round(capturedMargin * capturedDownsampleScale)
        );
        int sourceMarginX = Math.Max(0, (storedTileWidth - tileOutWidth) / 2);
        int sourceMarginY = Math.Max(0, (storedTileHeight - tileOutHeight) / 2);
        int outputMarginX = (outWidth - capturedGridSize * tileOutWidth) / 2;
        int outputMarginY = (outHeight - capturedGridSize * tileOutHeight) / 2;
        if (capturedGridSize < 1
            || capturedTiles.Length < checked(capturedGridSize * capturedGridSize)
            || tileOutWidth < 1
            || tileOutHeight < 1
            || storedTileWidth < 1
            || storedTileHeight < 1
            || outputMarginX < 0
            || outputMarginY < 0)
        {
            throw new InvalidDataException(
                $"Invalid stitch geometry: output {outWidth}x{outHeight}, grid {capturedGridSize}, tile {tileOutWidth}x{tileOutHeight}, stored {storedTileWidth}x{storedTileHeight}."
            );
        }
        overlap = Math.Min(
            overlap,
            Math.Max(1, Math.Min(tileOutWidth, tileOutHeight))
        );

        const int bandRows = 256;
        byte[] band = new byte[checked((outWidth * 3 + 1) * bandRows)];
        for (int bandStart = 0; bandStart < outHeight; bandStart += bandRows)
        {
            if (isCancelled?.Invoke() == true)
            {
                throw new OperationCanceledException();
            }
            int bandHeight = Math.Min(bandRows, outHeight - bandStart);
            int offset = 0;
            for (int row = 0; row < bandHeight; row++)
            {
                int y = bandStart + row;
                int shiftedY = y - outputMarginY;
                int tileRow = Math.Clamp(
                    shiftedY / tileOutHeight,
                    0,
                    capturedGridSize - 1
                );
                int withinY = shiftedY - tileRow * tileOutHeight;
                // GL ReadPixels returns bottom-up rows: the top of the image
                // is the last row of the stored tile.
                int sourceRow = Math.Clamp(
                    storedTileHeight - 1 - (sourceMarginY + withinY),
                    0,
                    storedTileHeight - 1
                );
                float yBlend = 0f;
                bool blendRow = withinY >= tileOutHeight - overlap
                    && tileRow + 1 < capturedGridSize;
                if (blendRow)
                {
                    yBlend = (withinY - (tileOutHeight - overlap) + 0.5f) / overlap;
                    yBlend = Math.Clamp(yBlend, 0f, 1f);
                }
                int blendSourceRow = Math.Clamp(
                    storedTileHeight
                        - 1
                        - (sourceMarginY + withinY - tileOutHeight),
                    0,
                    storedTileHeight - 1
                );

                band[offset++] = 0; // filter: None
                for (int x = 0; x < outWidth; x++)
                {
                    int shiftedX = x - outputMarginX;
                    int tileCol = Math.Clamp(
                        shiftedX / tileOutWidth,
                        0,
                        capturedGridSize - 1
                    );
                    int withinX = shiftedX - tileCol * tileOutWidth;
                    int sourceX = Math.Clamp(
                        sourceMarginX + withinX,
                        0,
                        storedTileWidth - 1
                    );
                    int sourceOffset =
                        (sourceRow * storedTileWidth + sourceX) * 4;
                    byte[] tile = capturedTiles[
                        tileRow * capturedGridSize + tileCol
                    ];

                    float r = tile[sourceOffset + 2];
                    float g = tile[sourceOffset + 1];
                    float b = tile[sourceOffset];
                    if (withinX >= tileOutWidth - overlap
                        && tileCol + 1 < capturedGridSize)
                    {
                        // Crossfade into the right neighbour inside the
                        // shared horizontal overlap.
                        float xBlend = Math.Clamp(
                            (withinX - (tileOutWidth - overlap) + 0.5f) / overlap,
                            0f,
                            1f
                        );
                        byte[] next =
                            capturedTiles[
                                tileRow * capturedGridSize + tileCol + 1
                            ];
                        int nextSourceX = Math.Clamp(
                            sourceMarginX + withinX - tileOutWidth,
                            0,
                            storedTileWidth - 1
                        );
                        int nextOffset =
                            (sourceRow * storedTileWidth + nextSourceX) * 4;
                        r += (next[nextOffset + 2] - r) * xBlend;
                        g += (next[nextOffset + 1] - g) * xBlend;
                        b += (next[nextOffset] - b) * xBlend;
                    }
                    if (blendRow)
                    {
                        // Crossfade into the bottom neighbour inside the
                        // shared vertical overlap.
                        byte[] bottom =
                            capturedTiles[
                                (tileRow + 1) * capturedGridSize + tileCol
                            ];
                        int bottomOffset =
                            (blendSourceRow * storedTileWidth + sourceX) * 4;
                        r += (bottom[bottomOffset + 2] - r) * yBlend;
                        g += (bottom[bottomOffset + 1] - g) * yBlend;
                        b += (bottom[bottomOffset] - b) * yBlend;
                    }
                    band[offset++] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                    band[offset++] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                    band[offset++] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
                }
            }
            writer.WriteBand(band, offset);
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}

/// <summary>
/// Minimal PNG encoder for stitched top-down RGB pixels. Runs on a worker
/// thread so the render loop never waits for compression or disk I/O.
/// </summary>
internal static class AtlasPngEncoder
{
    public static void SavePng(
        byte[] rgbPixels,
        int width,
        int height,
        string outputPath
    )
    {
        using FileStream output = new(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        WriteSignature(output);

        byte[] header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 2; // color type: truecolor RGB
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        WriteChunk(output, "IHDR", header);

        int stride = checked(width * 3);
        byte[] raw = new byte[checked((stride + 1) * height)];
        for (int row = 0; row < height; row++)
        {
            raw[row * (stride + 1)] = 0; // filter: None
            System.Buffer.BlockCopy(
                rgbPixels,
                row * stride,
                raw,
                row * (stride + 1) + 1,
                stride
            );
        }

        using MemoryStream compressed = new();
        using (DeflateStream deflater = new(
            compressed,
            CompressionLevel.Optimal,
            leaveOpen: true
        ))
        {
            deflater.Write(raw, 0, raw.Length);
        }
        byte[] zlibData = compressed.ToArray();
        byte[] zlib = new byte[checked(zlibData.Length + 6)];
        zlib[0] = 0x78;
        zlib[1] = 0x9c;
        System.Buffer.BlockCopy(zlibData, 0, zlib, 2, zlibData.Length);
        WriteInt32(zlib, zlib.Length - 4, (int)Adler32(raw));
        WriteChunk(output, "IDAT", zlib);
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteSignature(Stream output)
    {
        output.WriteByte(0x89);
        output.WriteByte(0x50); // P
        output.WriteByte(0x4e); // N
        output.WriteByte(0x47); // G
        output.WriteByte(0x0d);
        output.WriteByte(0x0a);
        output.WriteByte(0x1a);
        output.WriteByte(0x0a);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] lengthBytes = new byte[4];
        WriteInt32(lengthBytes, 0, data.Length);
        output.Write(lengthBytes, 0, 4);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, 4);
        output.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteInt32(crcBytes, 0, (int)crc);
        output.Write(crcBytes, 0, 4);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xff);
        buffer[offset + 1] = (byte)((value >> 16) & 0xff);
        buffer[offset + 2] = (byte)((value >> 8) & 0xff);
        buffer[offset + 3] = (byte)(value & 0xff);
    }

    private static uint Crc32(byte[] type, byte[] data) =>
        AtlasPngCrc.Crc32(type, data);

    private static uint Adler32(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}

/// <summary>
/// Minimal streaming PNG writer. Scanlines (filter: None, truecolor RGB)
/// arrive band by band and are compressed through one continuous zlib stream
/// that is emitted as multiple IDAT chunks, so arbitrarily large stitched
/// images never need a full uncompressed buffer. The zlib header leads the
/// first IDAT chunk and the Adler-32 checksum trails the last one.
/// </summary>
internal sealed class AtlasPngStreamWriter : IDisposable
{
    private readonly FileStream output;
    private readonly DeflateStream deflater;
    private readonly MemoryStream compressed = new();
    private uint adlerA = 1;
    private uint adlerB = 0;
    private bool zlibHeaderWritten;
    private bool disposed;

    public AtlasPngStreamWriter(string path, int width, int height)
    {
        output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        WriteSignature(output);
        byte[] header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 2; // color type: truecolor RGB
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        WriteChunk(output, "IHDR", header);
        deflater = new DeflateStream(
            compressed,
            CompressionLevel.Optimal,
            leaveOpen: true
        );
    }

    /// <summary>Appends one band of scanlines (filter byte + RGB row each).</summary>
    public void WriteBand(byte[] rawBand, int length)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AtlasPngStreamWriter));

        int index = 0;
        while (index < length)
        {
            int chunk = Math.Min(5552, length - index);
            for (int i = 0; i < chunk; i++)
            {
                adlerA += rawBand[index + i];
                adlerB += adlerA;
            }
            adlerA %= 65521;
            adlerB %= 65521;
            index += chunk;
        }

        compressed.SetLength(0);
        deflater.Write(rawBand, 0, length);
        deflater.Flush();
        byte[] payload = compressed.ToArray();
        if (!zlibHeaderWritten)
        {
            byte[] withHeader = new byte[checked(payload.Length + 2)];
            withHeader[0] = 0x78;
            withHeader[1] = 0x9c;
            System.Buffer.BlockCopy(payload, 0, withHeader, 2, payload.Length);
            WriteChunk(output, "IDAT", withHeader);
            zlibHeaderWritten = true;
        }
        else
        {
            WriteChunk(output, "IDAT", payload);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            compressed.SetLength(0);
            // Emits the final deflate block into the shared buffer.
            deflater.Dispose();
            byte[] tail = compressed.ToArray();
            byte[] withAdler = new byte[checked(tail.Length + 4)];
            System.Buffer.BlockCopy(tail, 0, withAdler, 0, tail.Length);
            withAdler[tail.Length] = (byte)(adlerB >> 8);
            withAdler[tail.Length + 1] = (byte)adlerB;
            withAdler[tail.Length + 2] = (byte)(adlerA >> 8);
            withAdler[tail.Length + 3] = (byte)adlerA;
            WriteChunk(output, "IDAT", withAdler);
            WriteChunk(output, "IEND", Array.Empty<byte>());
        }
        finally
        {
            output.Dispose();
        }
    }

    private static void WriteSignature(Stream output)
    {
        output.WriteByte(0x89);
        output.WriteByte(0x50); // P
        output.WriteByte(0x4e); // N
        output.WriteByte(0x47); // G
        output.WriteByte(0x0d);
        output.WriteByte(0x0a);
        output.WriteByte(0x1a);
        output.WriteByte(0x0a);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] lengthBytes = new byte[4];
        WriteInt32(lengthBytes, 0, data.Length);
        output.Write(lengthBytes, 0, 4);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, 4);
        output.Write(data, 0, data.Length);
        uint crc = AtlasPngCrc.Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteInt32(crcBytes, 0, (int)crc);
        output.Write(crcBytes, 0, 4);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xff);
        buffer[offset + 1] = (byte)((value >> 16) & 0xff);
        buffer[offset + 2] = (byte)((value >> 8) & 0xff);
        buffer[offset + 3] = (byte)(value & 0xff);
    }
}

/// <summary>Shared CRC-32 table for the PNG encoders.</summary>
internal static class AtlasPngCrc
{
    public static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (byte value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }
        return crc ^ 0xffffffff;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
