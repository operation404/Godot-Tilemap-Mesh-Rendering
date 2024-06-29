using Godot;
using System;

[Tool]
[GlobalClass]
public partial class TesterMoveAndRotate : Node2D
{
    #region Class members
    #region  ========== Exports ==========

    [Export]
    bool Moving = true;

    [Export]
    bool Rotating = true;

    [Export]
    float PositionSpeedScale = 1.0f;

    [Export]
    float RotationSpeedScale = 1.0f;

    #endregion
    #endregion
    #region ============================== Functionality ==============================

    public override void _Process(double delta)
    {
        float deltaF = (float)delta;

        if (Moving)
            Position = Position.Rotated(deltaF * PositionSpeedScale);

        if (Rotating)
            Rotation += deltaF * RotationSpeedScale;
    }

    #endregion
}