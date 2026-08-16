using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Tiled high-resolution atlas capture. The current map view is divided into
/// an N-by-N grid (1x to 4x). For every tile the atlas camera zooms in N times
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
    /// <summary>Upper bound for the stitched output so extreme scales cannot exhaust memory.</summary>
    private const long MaximumStitchedPixels = 36_000_000;

    private readonly ICoreClientAPI capi;
    private readonly object stateLock = new();

    private int generation;
    private bool captureActive;
    private bool busy;
    private int gridSize = 1;
    private int tileIndex;
    private float baselineZoom;
    private float baselineYawDegrees;
    private float baselinePitchDegrees;
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
    private string? pendingPath;
    private string? completedPath;
    private string? completedError;
    private bool logReadbackFailureOnce;
    private readonly bool debugSaveTiles = !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("MODERNATLAS_DEBUG_TILES")
    );

    public AtlasTiledScreenshot(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    /// <summary>True while tiles are captured or the stitched image is saved.</summary>
    public bool Busy
    {
        get
        {
            lock (stateLock)
            {
                return captureActive || busy;
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
                return captureActive;
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

    /// <summary>
    /// Starts a tiled capture of the current view. Must be called on the
    /// render thread while the atlas is open. The camera baseline is fixed by
    /// the caller through <see cref="GetTileCamera"/>.
    /// </summary>
    public bool StartCapture(
        int requestedGridSize,
        float zoom,
        float yawDegrees,
        float pitchDegrees,
        float viewportAspect
    )
    {
        lock (stateLock)
        {
            if (captureActive || busy) return false;
        }

        gridSize = Math.Clamp(requestedGridSize, 1, 20);
        baselineZoom = Math.Max(1f, zoom);
        baselineYawDegrees = yawDegrees;
        baselinePitchDegrees = pitchDegrees;
        capturedViewportAspect = Math.Max(0.05f, viewportAspect);

        IRenderAPI render = capi.Render;
        int frameWidth = Math.Max(1, render.FrameWidth);
        int frameHeight = Math.Max(1, render.FrameHeight);
        if (frameWidth < 96 || frameHeight < 96)
        {
            lock (stateLock)
            {
                completedError =
                    $"The game window is too small for a tiled capture ({frameWidth}x{frameHeight}).";
            }
            return false;
        }
        float frameAspect = frameWidth / (float)frameHeight;
        projectionVerticalFactor = Math.Max(1f, capturedViewportAspect / frameAspect);
        tileWidth = Math.Clamp(
            (int)Math.Round(
                frameWidth * capturedViewportAspect / (frameAspect * projectionVerticalFactor)
            ),
            1,
            frameWidth
        );
        tileHeight = Math.Clamp(
            (int)Math.Round(frameHeight / projectionVerticalFactor),
            1,
            frameHeight
        );

        // The tile camera is shifted by exactly one integer tile of Primary
        // pixels per grid step, in screen space. The camera is a tilted
        // LookAt, so a ground-plane forward offset moves the screen image by
        // only sin(pitch) pixels vertically; the compensating world-Y
        // component covers the rest, including the horizontal 0-degree
        // Creative pitch where ground offsets do not move the image
        // vertically at all.
        float tileZoom = baselineZoom / gridSize;
        worldPerPixel = 2.0 * tileZoom * projectionVerticalFactor / frameHeight;

        // A small overlap on every shared edge lets the stitcher crossfade
        // the seams, hiding the sub-pixel drift that different camera
        // positions introduce through the float pipeline. Each captured
        // frame stores the nominal tile plus `margin` extra pixels on every
        // side that the previous and next tile also captured.
        margin = Math.Max(2, (int)Math.Round(
            0.006f * Math.Min(tileWidth, tileHeight)
        ));
        nominalTileWidth = Math.Max(96, tileWidth - margin * 2);
        nominalTileHeight = Math.Max(96, tileHeight - margin * 2);
        storedWidth = Math.Min(frameWidth, nominalTileWidth + margin * 2);
        storedHeight = Math.Min(frameHeight, nominalTileHeight + margin * 2);
        readX = (frameWidth - storedWidth) / 2;
        readY = (frameHeight - storedHeight) / 2;

        long stitchedPixels =
            (long)nominalTileWidth * nominalTileHeight * gridSize * gridSize;
        downsampleScale = stitchedPixels > MaximumStitchedPixels
            ? MathF.Sqrt(MaximumStitchedPixels / (float)stitchedPixels)
            : 1f;

        try
        {
            pendingPath = BuildOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
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
        }
        capi.Logger.Notification(
            "[ModernAtlas] Started a {0}x{0} tiled atlas screenshot at {1}x{2} pixels per tile{3}.",
            gridSize,
            nominalTileWidth,
            nominalTileHeight,
            downsampleScale < 1f
                ? $" (downsampled to {downsampleScale:0.###}x for the pixel budget)"
                : ""
        );
        return true;
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
        tileZoom = baselineZoom / gridSize;
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
    public bool CaptureCurrentFrame()
    {
        int capturedTile;
        lock (stateLock)
        {
            if (!captureActive) return false;
            capturedTile = tileIndex;
        }

        try
        {
            IRenderAPI render = capi.Render;
            FrameBufferRef primary = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            render.CurrentFrameBuffer = primary;
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

            byte[] pixels = new byte[checked(storedWidth * storedHeight * 4)];
            GL.ReadPixels(
                readX,
                readY,
                storedWidth,
                storedHeight,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                pixels
            );
            if (tiles != null && capturedTile < tiles.Length)
            {
                tiles[capturedTile] = DownsampleToBudget(pixels);
                if (debugSaveTiles && pendingPath != null)
                {
                    SaveDebugTile(capturedTile, pixels);
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
                captureActive = false;
                completedError = exception.Message;
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
            if (!captureActive) return false;
            tileIndex++;
            if (tileIndex < gridSize * gridSize) return true;

            captureActive = false;
            busy = true;
        }
        StartBackgroundStitch();
        return true;
    }

    /// <summary>Cancels the capture. A running background save aborts without writing.</summary>
    public void Cancel()
    {
        lock (stateLock)
        {
            if (!captureActive && !busy) return;
            generation++;
            captureActive = false;
            busy = false;
            tiles = null;
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

    private string BuildOutputPath()
    {
        string folder = capi.GetOrCreateDataPath(
            Path.Combine("Screenshots", "ModernAtlas")
        );
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(folder, $"ModernAtlas_{stamp}.png");
    }

    /// <summary>
    /// Diagnostic: saves the raw readback of one tile so the per-tile camera
    /// alignment can be verified independently of the stitcher.
    /// </summary>
    private void SaveDebugTile(int tile, byte[] bgra)
    {
        if (string.IsNullOrEmpty(pendingPath)) return;
        string folder = Environment.GetEnvironmentVariable("MODERNATLAS_DEBUG_TILES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"tile{tile}.png");
            byte[] rgb = new byte[checked(storedWidth * storedHeight * 3)];
            for (int y = 0; y < storedHeight; y++)
            {
                // ReadPixels rows are bottom-up; write top-down rows.
                int sourceRow = (storedHeight - 1 - y) * storedWidth * 4;
                int targetRow = y * storedWidth * 3;
                for (int x = 0; x < storedWidth; x++)
                {
                    int s = sourceRow + x * 4;
                    int t = targetRow + x * 3;
                    rgb[t] = bgra[s + 2];
                    rgb[t + 1] = bgra[s + 1];
                    rgb[t + 2] = bgra[s];
                }
            }
            AtlasPngEncoder.SavePng(rgb, storedWidth, storedHeight, path);
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
        int capturedGeneration;
        string? path;
        lock (stateLock)
        {
            capturedGeneration = generation;
            path = pendingPath;
        }
        if (string.IsNullOrEmpty(path))
        {
            lock (stateLock)
            {
                busy = false;
                completedError = "No output path was prepared for the screenshot.";
            }
            return;
        }

        Task.Run(
            () =>
            {
                string? savedPath = null;
                string? failure = null;
                try
                {
                    byte[] stitched = StitchTopDown();
                    lock (stateLock)
                    {
                        if (generation != capturedGeneration) return;
                    }
                    AtlasPngEncoder.SavePng(
                        stitched,
                        stitchedWidth() + 2 * marginOutX(),
                        stitchedHeight() + 2 * marginOutY(),
                        path
                    );
                    savedPath = path;
                }
                catch (Exception exception)
                {
                    failure = exception.Message;
                }

                lock (stateLock)
                {
                    if (generation != capturedGeneration)
                    {
                        // A newer capture or a cancellation superseded this
                        // encode; the state is owned by the new run.
                        return;
                    }
                    busy = false;
                    completedPath = savedPath;
                    completedError = failure;
                }
            }
        );
    }

    private int stitchedWidth() => gridSize * outTileWidth();
    private int stitchedHeight() => gridSize * outTileHeight();

    private int marginOutX() => Math.Max(1, (int)Math.Round(margin * downsampleScale));
    private int marginOutY() => Math.Max(1, (int)Math.Round(margin * downsampleScale));

    private int outTileWidth() => Math.Max(1, (int)Math.Round(nominalTileWidth * downsampleScale));
    private int outTileHeight() => Math.Max(1, (int)Math.Round(nominalTileHeight * downsampleScale));

    /// <summary>
    /// Bilinear downsample of one BGRA tile to the pixel-budget size, keeping
    /// the overlap margin so the stitcher can crop identically at any scale.
    /// </summary>
    private byte[] DownsampleToBudget(byte[] source)
    {
        int targetWidth = Math.Max(1, (int)Math.Round(storedWidth * downsampleScale));
        int targetHeight = Math.Max(1, (int)Math.Round(storedHeight * downsampleScale));
        if (targetWidth == storedWidth && targetHeight == storedHeight)
        {
            return source;
        }

        byte[] result = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            float sampleY = (y + 0.5f) / downsampleScale - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sampleY), 0, storedHeight - 1);
            int y1 = Math.Min(y0 + 1, storedHeight - 1);
            float yBlend = Math.Clamp(sampleY - y0, 0f, 1f);
            for (int x = 0; x < targetWidth; x++)
            {
                float sampleX = (x + 0.5f) / downsampleScale - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sampleX), 0, storedWidth - 1);
                int x1 = Math.Min(x0 + 1, storedWidth - 1);
                float xBlend = Math.Clamp(sampleX - x0, 0f, 1f);

                int outputOffset = (y * targetWidth + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    float top = source[(y0 * storedWidth + x0) * 4 + channel]
                        * (1f - xBlend)
                        + source[(y0 * storedWidth + x1) * 4 + channel] * xBlend;
                    float bottom = source[(y1 * storedWidth + x0) * 4 + channel]
                        * (1f - xBlend)
                        + source[(y1 * storedWidth + x1) * 4 + channel] * xBlend;
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
    /// Assembles all tiles into one top-down RGB image. Each tile is stored
    /// bottom-up with an overlap margin on every shared edge. Inside the
    /// overlap the two neighbouring tiles are linearly crossfaded, so the
    /// sub-pixel drift between separately positioned cameras blends into an
    /// invisible seam instead of a hard cut. The outer margins stay in the
    /// image so the composite covers exactly the world area of the live
    /// view and nothing at the picture edges is cropped. The output row
    /// order is tile row 0 (top of the original view) first.
    /// </summary>
    private byte[] StitchTopDown()
    {
        byte[][] capturedTiles = tiles
            ?? throw new InvalidOperationException("No tiles were captured.");
        int tileOutWidth = outTileWidth();
        int tileOutHeight = outTileHeight();
        int storedTileWidth = Math.Max(1, (int)Math.Round(storedWidth * downsampleScale));
        int storedTileHeight = Math.Max(1, (int)Math.Round(storedHeight * downsampleScale));
        int overlap = Math.Max(1, (int)Math.Round(margin * downsampleScale));
        int marginX = (storedTileWidth - tileOutWidth) / 2;
        int marginY = (storedTileHeight - tileOutHeight) / 2;
        int outWidth = stitchedWidth() + 2 * marginX;
        int outHeight = stitchedHeight() + 2 * marginY;

        byte[] raw = new byte[checked(outWidth * outHeight * 3)];
        int outputOffset = 0;
        for (int y = 0; y < outHeight; y++)
        {
            int shiftedY = y - marginY;
            int tileRow = Math.Clamp(shiftedY / tileOutHeight, 0, gridSize - 1);
            int withinY = shiftedY - tileRow * tileOutHeight;
            // GL ReadPixels returns bottom-up rows: the top of the image is
            // the last row of the stored tile.
            int sourceRow = storedTileHeight - 1 - (marginY + withinY);
            float yBlend = 0f;
            bool blendRow = withinY >= tileOutHeight - overlap
                && tileRow + 1 < gridSize;
            if (blendRow)
            {
                yBlend = (withinY - (tileOutHeight - overlap) + 0.5f) / overlap;
                yBlend = Math.Clamp(yBlend, 0f, 1f);
            }
            int blendSourceRow = storedTileHeight - 1 - (marginY + withinY - tileOutHeight);
            for (int x = 0; x < outWidth; x++)
            {
                int shiftedX = x - marginX;
                int tileCol = Math.Clamp(shiftedX / tileOutWidth, 0, gridSize - 1);
                int withinX = shiftedX - tileCol * tileOutWidth;
                int sourceOffset =
                    (sourceRow * storedTileWidth + marginX + withinX) * 4;
                byte[] tile = capturedTiles[tileRow * gridSize + tileCol];

                float r = tile[sourceOffset + 2];
                float g = tile[sourceOffset + 1];
                float b = tile[sourceOffset];
                if (withinX >= tileOutWidth - overlap
                    && tileCol + 1 < gridSize)
                {
                    // Crossfade into the right neighbour inside the shared
                    // horizontal overlap.
                    float xBlend = Math.Clamp(
                        (withinX - (tileOutWidth - overlap) + 0.5f) / overlap,
                        0f,
                        1f
                    );
                    byte[] next = capturedTiles[tileRow * gridSize + tileCol + 1];
                    int nextOffset =
                        (sourceRow * storedTileWidth
                            + marginX + withinX - tileOutWidth) * 4;
                    r += (next[nextOffset + 2] - r) * xBlend;
                    g += (next[nextOffset + 1] - g) * xBlend;
                    b += (next[nextOffset] - b) * xBlend;
                }
                if (blendRow)
                {
                    // Crossfade into the bottom neighbour inside the shared
                    // vertical overlap.
                    byte[] bottom = capturedTiles[(tileRow + 1) * gridSize + tileCol];
                    int bottomOffset =
                        (blendSourceRow * storedTileWidth + marginX + withinX) * 4;
                    r += (bottom[bottomOffset + 2] - r) * yBlend;
                    g += (bottom[bottomOffset + 1] - g) * yBlend;
                    b += (bottom[bottomOffset] - b) * yBlend;
                }
                raw[outputOffset++] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                raw[outputOffset++] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                raw[outputOffset++] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
            }
        }
        return raw;
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

    private static uint Crc32(byte[] type, byte[] data)
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
