/*
    Copyright 2024 Caelan Booker
    Licensed under the Apache License, Version 2.0
*/

using Godot;
using System;

namespace Tilemap;

[Tool]
[GlobalClass]
public partial class TilemapMeshDisplay : TilemapDisplay
{
    #region Class members
    #region  ========== Restricted Data ==========

    private static readonly ArrayMesh DefaultTileMesh;

    private MultiMesh _multimesh;

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
    }

    public TilemapMeshDisplay()
    {
        _multimesh = new()
        {
            UseCustomData = true,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            Mesh = DefaultTileMesh,
        };
        RawDataInCustom = true;
    }

    protected override void ClearTilemap()
    {
        _multimesh.VisibleInstanceCount = 0;
    }

    #endregion
    #region ============================== Functionality ==============================

    protected override void PreGenerateMapData(int tileCount)
    {
        _multimesh.VisibleInstanceCount = -1;
        _multimesh.InstanceCount = tileCount;
    }

    protected override void CreateTile(int index, Vector2I tileCoord, Color instanceData,
        Vector2I tileSize, Vector2I tileScale, Vector2 ySortOrigin)
    {
        Transform2D transform = new(0, tileScale * tileSize, 0, tileCoord * tileSize);
        _multimesh.SetInstanceCustomData(index, instanceData);
        _multimesh.SetInstanceTransform2D(index, transform);
    }

    #endregion
    #region ============================== Event Handlers ==============================

    public override void _Draw()
    {
        if (Tilemap != null)
            DrawMultimesh(_multimesh, null);
    }

    #endregion
    #region ============================== Disposal ==============================

    protected override void Dispose(bool safeToDisposeManagedObjects)
    {
        base.Dispose(safeToDisposeManagedObjects);
        if (safeToDisposeManagedObjects)
        {
            _multimesh = null; // must come after parent, which calls `ClearTilemap`
        }
    }

    #endregion
}