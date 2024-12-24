/*
    Copyright 2024 Caelan Booker
    Licensed under the Apache License, Version 2.0
*/

using Godot;
using System;
using System.Collections.Generic;

namespace Tilemap;

[Tool]
[GlobalClass]
public partial class TilemapCanvasItemDisplay : TilemapDisplay
{
    #region Class members
    #region  ========== Restricted Data ==========

    private List<Rid> _rids;
    private Rid _parentRid;
    private Rid _materialRid;

    #endregion
    #endregion
    #region ============================== Setup ==============================

    public TilemapCanvasItemDisplay()
    {
        _rids = [];
        _parentRid = GetCanvasItem();
        _materialRid = ShaderMat.GetRid();
    }

    protected override void ClearTilemap()
    {
        foreach (Rid rid in _rids)
            RenderingServer.FreeRid(rid);
        _rids.Clear();
    }

    #endregion
    #region ============================== Functionality ==============================

    protected override void PreGenerateMapData(int tileCount)
    {
        ClearTilemap();
        _rids.EnsureCapacity(tileCount);
    }

    protected override void CreateTile(int index, Vector2I tileCoord, Color instanceData,
        Vector2I tileSize, Vector2I tileScale, Vector2 ySortOrigin)
    {
        Rect2 tileRect = new(-ySortOrigin, tileScale * tileSize);
        Transform2D transform = new(0, Vector2.One, 0, tileCoord * tileSize + ySortOrigin);

        Rid ciRid = RenderingServer.CanvasItemCreate();
        _rids.Add(ciRid);
        RenderingServer.CanvasItemSetParent(ciRid, _parentRid);
        RenderingServer.CanvasItemSetMaterial(ciRid, _materialRid);
        RenderingServer.CanvasItemAddRect(ciRid, tileRect, instanceData);
        RenderingServer.CanvasItemSetTransform(ciRid, transform);
    }

    #endregion 
    #region ============================== Disposal ==============================

    protected override void Dispose(bool safeToDisposeManagedObjects)
    {
        base.Dispose(safeToDisposeManagedObjects);
        if (safeToDisposeManagedObjects)
        {
            _rids = null; // must come after parent, which calls `ClearTilemap`
        }
    }

    #endregion
}