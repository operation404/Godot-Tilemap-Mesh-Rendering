using CommunityToolkit.HighPerformance;
using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Tilemap;

/* NOTE
When importing the texture, turn "Fix Alpha Border" and "Generate Mipmaps" off.
If mipmaps are desired, use compute shader to generate them.
Shader assumes a tile's texture origin is always top-left of the tile.
*/

[Tool]
[GlobalClass]
public partial class TilemapMeshDisplay : MultiMeshInstance2D
{
    #region Class members
    #region  ========== Delegate Instances ==========

    private Action _onTilemapChanged;

    #endregion
    #region  ========== Types ==========

    private struct TileImageData
    {
        // Just use single channel of float for 4 bytes of data storage
        public static Image.Format Format = Image.Format.Rf;

        public byte AtlasId;
        public byte X;
        public byte Y;

#pragma warning disable IDE0051, IDE0044, CS0169
        private byte _padding; // Padding to ensure struct is 4 bytes wide
#pragma warning restore IDE0051, IDE0044, CS0169
    }

    private struct MeshCustomData
    {
        public Vector2I MapCoord;
        public Vector2I TileScale;
    }

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
        set => TilemapPropertyUpdated(value);
    }
    private TileMapLayer _tilemap;

    [Export]
    public bool UseMipmaps
    {
        get => _useMipmaps;
        set => UseMipmapsPropertyUpdated(value);
    }
    private bool _useMipmaps = false;

    #endregion
    #region  ========== Static ==========

    private static readonly ArrayMesh DefaultTileMesh;
    private static readonly StringName UpdateDrawingSN = nameof(UpdateDrawing);

    private static readonly StringName Atlas = "atlas";
    private static readonly StringName TileSize = "tile_size";
    private static readonly StringName MapData = "map_data";

    public static readonly Shader MeshDisplayNoMipmaps = GD.Load<Shader>("res://source/TilemapMeshDisplay.gdshader");
    public static readonly Shader MeshDisplayMipmaps;

    #endregion
    #region  ========== Restricted Data ==========

    private ShaderMaterial _material;
    private Texture2D[] _sources;
    private ImageTexture _mapTex;
    private Image _mapImg;

    private Vector2I _tileScalePadding;
    private Vector2I _prevImageSize;

    #endregion
    #endregion
    #region ============================== Setup ==============================

    static TilemapMeshDisplay()
    {
        Godot.Collections.Array data = [];
        data.Resize((int)Mesh.ArrayType.Max);

        data[(int)Mesh.ArrayType.Index] = (int[])[1, 2, 0, 3];
        data[(int)Mesh.ArrayType.Vertex] = (Vector2[])[new(0, 0), new(0, 1), new(1, 1), new(1, 0)];
        data[(int)Mesh.ArrayType.TexUV] = data[(int)Mesh.ArrayType.Vertex];

        DefaultTileMesh = new();
        DefaultTileMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.TriangleStrip, data);

        StringBuilder shaderCode = new(MeshDisplayNoMipmaps.Code.Length + 50);
        shaderCode.Append("#define USE_MIPMAPS\n");
        shaderCode.Append(MeshDisplayNoMipmaps.Code);
        MeshDisplayMipmaps = new()
        {
            Code = shaderCode.ToString()
        };
    }

    public TilemapMeshDisplay()
    {
        _onTilemapChanged = OnTilemapChanged;
        _material = new()
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
        Multimesh = new()
        {
            UseCustomData = true,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            Mesh = DefaultTileMesh,
        };
        Material = _material;
    }

    private void TilemapPropertyUpdated(TileMapLayer newTilemap)
    {
        if (Tilemap == newTilemap)
            return;

        ClearTilemap();
        _tilemap = newTilemap;
        SetupTilemap();
    }

    private void UseMipmapsPropertyUpdated(bool useMipmaps)
    {
        if (_useMipmaps != useMipmaps)
        {
            _useMipmaps = useMipmaps;
            _material.Shader = _useMipmaps
                ? MeshDisplayMipmaps
                : MeshDisplayNoMipmaps;
            SetShaderUniforms();
        }
    }

    private void ClearTilemap()
    {
        if (Tilemap == null)
            return;

        Tilemap.Changed -= _onTilemapChanged;
        Multimesh.VisibleInstanceCount = 0;
    }

    private void SetupTilemap()
    {
        if (Tilemap == null)
            return;

        Tilemap.Changed += _onTilemapChanged;
        Multimesh.VisibleInstanceCount = -1;
        OnTilemapChanged();
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
        _material.SetShaderParameter(Atlas, _sources);
        _material.SetShaderParameter(TileSize, Tilemap.TileSet.TileSize);
        _material.SetShaderParameter(MapData, _mapTex);
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
        Multimesh.InstanceCount = tiles.Count;

        // Offset tile coord into the image such that the top-left tile is moved
        // to 0,0, assuming all tiles are scale 1x1. If there are tiles with a
        // larger scale, pad the image around the edges so that we can account
        // for tiles who's drawn canvas item is larger than a single tile
        Vector2I imageOffset = _tileScalePadding - usedRect.Position;

        for (int i = 0; i < tiles.Count; i++)
        {
            Vector2I tileCoord = tiles[i];

            // Check if tile is valid and is from an atlas source
            if (Tilemap.GetCellTileData(tileCoord) == null)
                continue;

            int atlasId = Tilemap.GetCellSourceId(tileCoord);
            Vector2I atlasCoords = Tilemap.GetCellAtlasCoords(tileCoord);
            Vector2I tileScale = (tileset.GetSource(atlasId) as TileSetAtlasSource)
                .GetTileSizeInAtlas(atlasCoords);

            { // Assert validations
                if (atlasId >= MAX_SOURCES)
                    throw new Exception("Tile atlas ID exceed maximum value.");

                if (atlasCoords.X >= MAX_ATLAS_OFFSET &&
                    atlasCoords.Y >= MAX_ATLAS_OFFSET)
                    throw new Exception("Tile atlas coords exceed maximum value.");
            }

            Vector2I mainImageCoord = tileCoord + imageOffset;

            MeshCustomData instanceData = new()
            {
                MapCoord = mainImageCoord,
                TileScale = tileScale,
            };
            Multimesh.SetInstanceCustomData(i, Unsafe.As<MeshCustomData, Color>(ref instanceData));
            Multimesh.SetInstanceTransform2D(i, new(0, tileScale * tileset.TileSize, 0, tileCoord * tileset.TileSize));

            // Write one map data texel for each standard tile's worth of space this tile takes.
            for (int x = 0; x < tileScale.X; x++)
                for (int y = 0; y < tileScale.Y; y++)
                {
                    Vector2I coord = mainImageCoord + new Vector2I(x, y);
                    tileData[coord.Y, coord.X] = new() // row-col indexing, y coord first
                    {
                        AtlasId = (byte)atlasId,
                        X = (byte)(atlasCoords.X + x),
                        Y = (byte)(atlasCoords.Y + y),
                    };
                }
        }

        SetMapTexture(imageSize, imageData);
    }

    private static void CreateDataArray(Vector2I imageSize, out byte[] imageData,
        out Span2D<TileImageData> tileData)
    {
        imageData = new byte[imageSize.X * imageSize.Y * Unsafe.SizeOf<TileImageData>()];
        Array.Fill(imageData, (byte)0xFF);
        tileData = MemoryMarshal.Cast<byte, TileImageData>(imageData)
             .AsSpan2D(imageSize.Y, imageSize.X);
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

    private void OnTilemapChanged() => CallDeferred(UpdateDrawingSN);

    #endregion
    #region ============================== Disposal ==============================

    protected override void Dispose(bool safeToDisposeManagedObjects)
    {
        if (safeToDisposeManagedObjects)
        {
            ClearTilemap();
            Array.Clear(_sources);
            _onTilemapChanged = null;
            _tilemap = null;
            _material = null;
            _sources = null;
            _mapTex = null;
            _mapImg = null;

        }
        base.Dispose(safeToDisposeManagedObjects);
    }

    #endregion
}