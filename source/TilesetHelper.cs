/*
    Copyright 2024 "Lich" Caelan B.
    Licensed under the Apache License, Version 2.0
    Repository: https://github.com/operation404/Godot-Tilemap-Mesh-Rendering
*/

using Godot;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tilemap;

public static partial class TilesetHelper
{
    #region Class members
    #region  ========== Types ==========

    private readonly record struct TileData(Vector2I Coordinates, Vector2I Size);
    private readonly record struct MiscUniforms(Vector2I RegionSize);

    #endregion
    #region  ========== Restricted Data ==========

    const string SHADER_PATH = "./source/GenerateCustomTilesetMipmaps.comp";

    const int MAX_USABLE_MIPMAPS = 9;
    const int SHADER_IMAGE_ARRAY_SIZE = MAX_USABLE_MIPMAPS + 1;

    const Image.Format SHADER_IMAGE_FORMAT = Image.Format.Rgbaf;

    private static readonly RenderingDevice _rd;
    private static readonly Rid _shader;

    #endregion
    #endregion
    #region ============================== Setup ==============================

    static TilesetHelper()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        RDShaderSource shaderF = new()
        {
            SourceCompute = File.ReadAllText(SHADER_PATH),
        };
        RDShaderSpirV shaderByteCode = _rd.ShaderCompileSpirVFromSource(shaderF);
        _shader = _rd.ShaderCreateFromSpirV(shaderByteCode);
    }

    #endregion
    #region ============================== Functionality ==============================

    public static ImageTexture GenerateMipmappedTexture(TileSetAtlasSource source,
        bool saveToFileSystem = false, string name = null)
    {
        Image mipmappedImage = GenerateMipmappedImage(source);
        ImageTexture texture = ImageTexture.CreateFromImage(mipmappedImage);

        if (name != null)
            texture.ResourceName = name;

        if (saveToFileSystem)
            Save(texture, source.Texture, name);

        return texture;
    }

    public static Image GenerateMipmappedImage(TileSetAtlasSource source,
        bool saveToFileSystem = false, string name = null)
    {
        Image image = source.Texture.GetImage();

        byte[] data = GpuComputeMipmaps(image, source);
        Image newImage = Image.CreateFromData(image.GetWidth(), image.GetHeight(),
            true, SHADER_IMAGE_FORMAT, data);

        newImage.Convert(Image.Format.Rgba8);

        if (name != null)
            newImage.ResourceName = name;

        if (saveToFileSystem)
            Save(newImage, source.Texture, name);

        return newImage;
    }

    private static void Save(Resource resource, Resource original, string name = null)
    {
        name ??= $"{original.ResourceName}CustomMipmaps";
        string path;

        if (!original.ResourceLocalToScene)
        {
            path = original.ResourcePath;
            path = $"{path.Substring(0, 1 + path.LastIndexOf('/'))}{name}.tres";
        }
        else
            path = $"res://{name}.tres";

        ResourceSaver.Save(resource, path, ResourceSaver.SaverFlags.None);
    }

    private static byte[] GpuComputeMipmaps(Image image, TileSetAtlasSource source)
    {
        int usableMipmapLevels = CalculateUsableMipmapLevels(source);

        if (usableMipmapLevels > MAX_USABLE_MIPMAPS)
            throw new Exception("Tileset texture requires more mipmap levels than the shader allows.");

        Span<Rid> mipmapViews = stackalloc Rid[usableMipmapLevels];
        Rid rdTexture = CreateRenderServerTexture(image);
        AssignMipmapViews(rdTexture, mipmapViews);
        Rid tileData = GenerateTileInvocationData(source, out uint invocationCount);
        Rid samplerFrame = CreateSampler();
        Rid uniformSet = SetupUniforms(rdTexture, tileData, mipmapViews, samplerFrame);
        Rid pipeline = SetupComputePipeline(uniformSet, invocationCount);

        _rd.Submit();
        _rd.Sync();

        byte[] data = _rd.TextureGetData(rdTexture, 0);

        _rd.FreeRid(rdTexture);
        _rd.FreeRid(tileData);

        return data;
    }

    private static Rid CreateRenderServerTexture(Image image)
    {
        CalculateMipmapMetadata(image, out int levels, out int byteCount);
        byte[] data = new byte[byteCount];
        InitializeTextureData(image, data);

        RDTextureFormat texFormat = new()
        {
            ArrayLayers = 1,
            Depth = 1,
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            Width = (uint)image.GetWidth(),
            Height = (uint)image.GetHeight(),
            Mipmaps = (uint)levels,
            Samples = RenderingDevice.TextureSamples.Samples1,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                        RenderingDevice.TextureUsageBits.StorageBit |
                        RenderingDevice.TextureUsageBits.CanCopyFromBit
        };

        return _rd.TextureCreate(texFormat, new RDTextureView(), [data]);
    }

    private static void InitializeTextureData(Image image, byte[] textureData)
    {
        // Ensure format is rgba floats before copying into shader texture buffer
        image.Convert(SHADER_IMAGE_FORMAT);
        byte[] sourceData = image.GetData();
        // Mipmaps are not 0 indexed for the GetMipmapOffset method
        int copyBound = image.HasMipmaps() ? image.GetMipmapOffset(1) : sourceData.Length;
        Buffer.BlockCopy(sourceData, 0, textureData, 0, copyBound);
    }

    private static void AssignMipmapViews(Rid rdTexture, Span<Rid> mipmaps)
    {
        RDTextureView defaultView = new();
        for (int i = 0; i < mipmaps.Length; i++)
        {
            uint mipmapIndex = (uint)i + 1;
            mipmaps[i] = _rd.TextureCreateSharedFromSlice(defaultView, rdTexture, 0,
                mipmapIndex, 1, RenderingDevice.TextureSliceType.Slice2D);
        }
    }

    private static Rid GenerateTileInvocationData(TileSetAtlasSource source, out uint invocations)
    {
        int tileCount = source.GetTilesCount();
        int miscUniformsSize = Unsafe.SizeOf<MiscUniforms>();
        byte[] uniformBuffer = new byte[miscUniformsSize + tileCount * Unsafe.SizeOf<TileData>()];
        Span<byte> buffer = uniformBuffer;

        Span<TileData> tiles = MemoryMarshal.Cast<byte, TileData>(buffer.Slice(miscUniformsSize));
        for (int i = 0; i < tileCount; i++)
        {
            Vector2I coord = source.GetTileId(i);
            Vector2I size = source.GetTileSizeInAtlas(coord);
            tiles[i] = new TileData(coord, size);
        }

        Span<MiscUniforms> uniforms = MemoryMarshal.Cast<byte, MiscUniforms>(
            buffer.Slice(0, miscUniformsSize));
        uniforms[0] = new MiscUniforms(source.TextureRegionSize);

        invocations = (uint)tileCount;
        return _rd.StorageBufferCreate((uint)uniformBuffer.Length, uniformBuffer);
    }

    private static Rid CreateSampler()
    {
        return _rd.SamplerCreate(new()
        {
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MipFilter = RenderingDevice.SamplerFilter.Linear,
            MaxLod = 0 // Ensure we don't sample from mipmaps accidentally
        });
    }

    private static Rid SetupUniforms(Rid rdTexture, Rid tileData, Span<Rid> mipmapViews, Rid samplerFrame)
    {
        RDUniform tileDataUniform = new()
        {
            Binding = 0,
            UniformType = RenderingDevice.UniformType.StorageBuffer
        };
        tileDataUniform.AddId(tileData);

        RDUniform imageUniform = new()
        {
            Binding = 1,
            UniformType = RenderingDevice.UniformType.Image,
        };

        // We want the texture available as a sampler so we can get free
        // hardware linear filtering. This will use the exact same opengl texture
        // object as the image uniform, so they both refer to the same data.
        RDUniform samplerUniform = new()
        {
            Binding = 17,
            UniformType = RenderingDevice.UniformType.SamplerWithTexture
        };

        // Set the base full quality image as the first array item for each uniform.
        imageUniform.AddId(rdTexture);
        samplerUniform.AddId(samplerFrame);
        samplerUniform.AddId(rdTexture);

        // Then load the mipmap views into the array.
        for (int i = 0; i < mipmapViews.Length; i++)
        {
            imageUniform.AddId(mipmapViews[i]);
            samplerUniform.AddId(samplerFrame);
            samplerUniform.AddId(mipmapViews[i]);
        }

        // Last, Godot requires all uniforms to be set even if unused, so just
        // create a ton of unchanged view copies of the image to fill the array.
        int unusedPaddingViews = MAX_USABLE_MIPMAPS - mipmapViews.Length;
        for (int i = 0; i < unusedPaddingViews; i++)
        {
            Rid filler = _rd.TextureCreateShared(new(), rdTexture);
            imageUniform.AddId(filler);
            samplerUniform.AddId(samplerFrame);
            samplerUniform.AddId(filler);
        }

        return _rd.UniformSetCreate([tileDataUniform, imageUniform, samplerUniform], _shader, 0);
    }

    private static Rid SetupComputePipeline(Rid uniformSet, uint invocations)
    {
        Rid pipeline = _rd.ComputePipelineCreate(_shader);

        long computeList = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(computeList, pipeline);
        _rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
        _rd.ComputeListDispatch(computeList, xGroups: invocations, yGroups: 1, zGroups: 1);
        _rd.ComputeListEnd();

        return pipeline;
    }

    #endregion
    #region ============================== Metadata Helpers ==============================

    public static int CalculateUsableMipmapLevels(TileSetAtlasSource source)
    {
        Vector2I tileSize = source.TextureRegionSize;
        return (int)Math.Floor(Math.Log2(Math.Min(tileSize.X, tileSize.Y)));
    }

    public static void CalculateMipmapMetadata(Image image, out int levels, out int byteCount)
    {
        Vector2I size = image.GetSize();
        int pixelCount = size.X * size.Y;
        // levels starts at 1 because godot image classes don't normally 
        // include the base image as a mipmap when counting mipmap levels
        levels = 1;

        while (size != Vector2I.One)
        {
            size.X = Math.Max(size.X / 2, 1);
            size.Y = Math.Max(size.Y / 2, 1);
            pixelCount += size.X * size.Y;
            levels++;
        }

        int bytesPerPixel = image.GetFormat() switch
        {
            Image.Format.Rgb8 => 3,
            Image.Format.Rgba8 => 4,
            Image.Format.Rgbf => 12,
            Image.Format.Rgbaf => 16,
            _ => throw new NotImplementedException(),
        };

        byteCount = pixelCount * bytesPerPixel;
    }

    #endregion
}
