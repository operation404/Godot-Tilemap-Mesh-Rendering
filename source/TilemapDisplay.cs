using CommunityToolkit.HighPerformance;
using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tilemap;

/* NOTE
When importing the texture, turn "Fix Alpha Border" and "Generate Mipmaps" off.
If mipmaps are desired, use compute shader to generate them.
Shader assumes a tile's texture origin is always top-left of the tile.
*/

public abstract partial class TilemapDisplay : Node2D
{
    #region Class members
    #region  ========== Delegate Instances ==========

    private Action _onTilemapChanged;

    #endregion
    #region  ========== Types ==========

    private struct TileImageData
    {
        public static Image.Format Format = Image.Format.Rf; // Single channel = 4 bytes of storage

        public byte AtlasId;
        public byte X;
        public byte Y;

#pragma warning disable IDE0051, IDE0044, CS0169
        private byte _padding; // Padding to ensure struct is 4 bytes wide
#pragma warning restore IDE0051, IDE0044, CS0169
    }

    private record struct MeshCustomData(Vector2I MapCoord, Vector2I TileScale);

    #endregion
    #region  ========== Public Data ==========

    // Valid source indices range from 0-14, 15 in total.
    // Any value about the limit represents no tile, and is rendered transparent.
    public const int MAX_SOURCES = 0xF;
    public const int MAX_ATLAS_OFFSET = 0xFF;

    [Export]
    public TileMapLayer Tilemap
    {
        get => _tilemap;
        set => TilemapUpdated(value);
    }
    private TileMapLayer _tilemap;

    [Export]
    public bool UseMipmaps
    {
        get => _useMipmaps;
        set => UseMipmapsUpdated(value);
    }
    private bool _useMipmaps = false;

    #endregion
    #region  ========== Static ==========

    private static readonly StringName UpdateDrawingSN = nameof(UpdateDrawing);
    private static readonly StringName Atlas = "atlas";
    private static readonly StringName TileSize = "tile_size";
    private static readonly StringName MapData = "map_data";
    private static readonly StringName RawInInstanceCustom = "raw_in_instance_custom";

    private static readonly Shader MeshDisplayNoMipmaps = GD.Load<Shader>("res://source/TilemapDisplay.gdshader");
    private static readonly Shader MeshDisplayMipmaps;

    #endregion
    #region  ========== Restricted Data ==========

    protected ShaderMaterial ShaderMat;
    private Texture2D[] _sources;
    private ImageTexture _mapTex;
    private Image _mapImg;

    protected bool RawDataInCustom = false;
    private Vector2I _tileScalePadding;
    private Vector2I _prevImageSize;

    #endregion
    #endregion
    #region ============================== Setup ==============================

    public TilemapDisplay()
    {
        _onTilemapChanged = OnTilemapChanged;
        ShaderMat = new()
        {
            Shader = MeshDisplayNoMipmaps
        };
        _sources = new Texture2D[MAX_SOURCES];
        _mapTex = new();
        _mapImg = new();
        _prevImageSize = Vector2I.Zero;
    }

    public override void _Ready()
    {
        Material = ShaderMat;
    }

    private void TilemapUpdated(TileMapLayer newTilemap)
    {
        if (Tilemap == newTilemap)
            return;

        if (Tilemap != null)
        {
            Tilemap.Changed -= _onTilemapChanged;
            ClearTilemap();
        }

        _tilemap = newTilemap;

        if (Tilemap != null)
        {
            Tilemap.Changed += _onTilemapChanged;
            CallDeferred(UpdateDrawingSN);
        }
    }

    protected abstract void ClearTilemap();

    private void UseMipmapsUpdated(bool useMipmaps)
    {
        if (_useMipmaps == useMipmaps)
            return;

        _useMipmaps = useMipmaps;

        ShaderMat.Shader = _useMipmaps
            ? MeshDisplayMipmaps
            : MeshDisplayNoMipmaps;
        SetShaderUniforms();
    }

    #endregion
    #region ============================== Functionality ==============================

    private void UpdateDrawing()
    {
        GetTilesetSources();
        GenerateMapData();
        SetShaderUniforms();
        QueueRedraw();
    }

    private void SetShaderUniforms()
    {
        ShaderMat.SetShaderParameter(Atlas, _sources);
        ShaderMat.SetShaderParameter(TileSize, Tilemap.TileSet.TileSize);
        ShaderMat.SetShaderParameter(MapData, _mapTex);
        ShaderMat.SetShaderParameter(RawInInstanceCustom, RawDataInCustom);
    }

    private void GetTilesetSources()
    {
        _tileScalePadding = Vector2I.One;
        Array.Clear(_sources);
        TileSet tileset = Tilemap.TileSet;
        Vector2I invalid = new(-1, -1);

        int sourceCount = tileset.GetSourceCount();
        for (int i = 0; i < sourceCount; i++)
        {
            int id = tileset.GetSourceId(i);
            if (tileset.GetSource(id) is TileSetAtlasSource source)
            {
                if (id >= MAX_SOURCES)
                    throw new Exception("Tile atlas ID exceed maximum value.");

                _sources[id] = source.Texture;

                Vector2I atlasGrid = source.GetAtlasGridSize();
                for (int x = 0; x < atlasGrid.X; x++)
                    for (int y = 0; y < atlasGrid.Y; y++)
                    {
                        Vector2I tileCoord = source.GetTileAtCoords(new Vector2I(x, y));
                        if (tileCoord != invalid)
                        {
                            Vector2I tileScale = source.GetTileSizeInAtlas(tileCoord);

                            _tileScalePadding.X = tileScale.X > _tileScalePadding.X
                                ? tileScale.X : _tileScalePadding.X;
                            _tileScalePadding.Y = tileScale.Y > _tileScalePadding.Y
                                ? tileScale.Y : _tileScalePadding.Y;
                        }
                    }
            }
        }

        _tileScalePadding -= Vector2I.One;
    }

    private void GenerateMapData()
    {
        TileSet tileset = Tilemap.TileSet;
        Rect2I usedRect = Tilemap.GetUsedRect();
        Vector2I imageSize = usedRect.Size + _tileScalePadding * 2;
        CreateDataArray(imageSize, out byte[] imageData, out Span2D<TileImageData> tileData);

        Godot.Collections.Array<Vector2I> tiles = Tilemap.GetUsedCells();
        PreGenerateMapData(tiles.Count);
        for (int i = 0; i < tiles.Count; i++)
        {
            Vector2I tileCoord = tiles[i];
            if (Tilemap.GetCellTileData(tileCoord) is not TileData data)
                continue;

            int atlasId = Tilemap.GetCellSourceId(tileCoord);
            Vector2I atlasCoord = Tilemap.GetCellAtlasCoords(tileCoord);

            { // Assert validations
                if (atlasId >= MAX_SOURCES)
                    throw new Exception("Tile atlas ID exceed maximum value.");

                if (atlasCoord.X >= MAX_ATLAS_OFFSET &&
                    atlasCoord.Y >= MAX_ATLAS_OFFSET)
                    throw new Exception("Tile atlas coords exceed maximum value.");
            }

            Vector2I mainImageCoord = tileCoord - usedRect.Position; // offset tiles, tl tile goes to 0,0
            Vector2 ySortOrigin = new(0, data.YSortOrigin + tileset.TileSize.Y / 2);
            Vector2I tileScale = (tileset.GetSource(atlasId) as TileSetAtlasSource)
                .GetTileSizeInAtlas(atlasCoord);
            MeshCustomData instanceData = new(mainImageCoord, tileScale);
            Color instanceColor = Unsafe.As<MeshCustomData, Color>(ref instanceData);
            CreateTile(i, tileCoord, instanceColor, tileset.TileSize, tileScale, ySortOrigin);

            WriteMapTexels(tileData, mainImageCoord, tileScale, atlasId, atlasCoord);
        }

        SetMapTexture(imageSize, imageData);
    }

    protected abstract void PreGenerateMapData(int tileCount);

    protected abstract void CreateTile(int index, Vector2I tileCoord, Color instanceData,
        Vector2I tileSize, Vector2I tileScale, Vector2 ySortOrigin);

    private static void CreateDataArray(Vector2I imageSize, out byte[] imageData,
        out Span2D<TileImageData> tileData)
    {
        imageData = new byte[imageSize.X * imageSize.Y * Unsafe.SizeOf<TileImageData>()];
        Array.Fill(imageData, (byte)0xFF);
        tileData = MemoryMarshal.Cast<byte, TileImageData>(imageData)
             .AsSpan2D(imageSize.Y, imageSize.X);
    }

    private static void WriteMapTexels(Span2D<TileImageData> tileData, Vector2I imageCoord,
        Vector2I tileScale, int atlasId, Vector2I atlasCoord)
    {
        // Write one map data texel for each standard tile's worth of space this tile takes.
        for (int x = 0; x < tileScale.X; x++)
            for (int y = 0; y < tileScale.Y; y++)
            {
                Vector2I coord = imageCoord + new Vector2I(x, y);
                tileData[coord.Y, coord.X] = new() // row-col indexing, y coord first
                {
                    AtlasId = (byte)atlasId,
                    X = (byte)(atlasCoord.X + x),
                    Y = (byte)(atlasCoord.Y + y),
                };
            }
    }

    private void SetMapTexture(Vector2I imageSize, byte[] imageData)
    {
        _mapImg.SetData(imageSize.X, imageSize.Y, false,
            TileImageData.Format, imageData);

        if (imageSize != _prevImageSize)
        {
            _mapTex.SetImage(_mapImg);
            _prevImageSize = imageSize;
        }
        else
            _mapTex.Update(_mapImg);
    }

    #endregion
    #region ============================== Event Handlers ==============================

    private void OnTilemapChanged()
    {
        if (!IsInstanceValid(Tilemap) || Tilemap.IsQueuedForDeletion())
        {
            ClearTilemap();
            return;
        }

        CallDeferred(UpdateDrawingSN);
    }

    #endregion
    #region ============================== Disposal ==============================

    protected override void Dispose(bool safeToDisposeManagedObjects)
    {
        if (safeToDisposeManagedObjects)
        {
            if (Tilemap != null)
                ClearTilemap();

            Array.Clear(_sources);
            _onTilemapChanged = null;
            _tilemap = null;
            ShaderMat = null;
            _sources = null;
            _mapTex = null;
            _mapImg = null;
        }
        base.Dispose(safeToDisposeManagedObjects);
    }

    #endregion
}