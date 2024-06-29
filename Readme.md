# Non-Pixel Perfect Tilemap Rendering

This project countains the sources for a new node and accompanying shaders that improve the visual clarity of tilemaps when used in non-pixel perfect projects. This node was originally created for pixel art games that didn't use pixel-perfect alignment, but it should provide some benefits for tilemaps with higher resolution art as well.

This tool is still a work in progress, and while it is mostly finished, there are still a few improvements I'm working and likely some bugs to find and fix still.

The source code for the node and helper methods are written in C#.

## Examples

When rendering a tilemap that is aligned to the screen grid and has an integer scaling, this method should look no different than the default. The differences should only be noticeable when the tilemap is not perfectly aligned to the screen or is scaled/rotated.

Example video with the original tilemap (left) and mesh node (right) inside of a subviewport. The nodes are scaled by an integer number (3) and are slowly moved in a circular motion. As the default tilemap moves, the entire tilemap snaps as which texels are nearest to a given fragment changes over time. There is occasional flickering and texture bleeding as a result of the sub-pixel movement. The mesh render node doesn't use nearest sampling and so the tilemap moves smoothly along the path of motion, and the shader algorithm used prevents color bleed from neighboring tiles on the atlas texture.

https://github.com/operation404/Godot-Tilemap-Mesh-Rendering/assets/12285227/730af004-7f74-4886-bff1-fc1bd670c6fd

The same scene, but using a non-integer scale (2.9). Since the amount of pixels inside of a single tile is no longer uniform across the entire tilemap, the tiles flicker as they move. This is a result of the amount of fragments that map to a given texel changes over time, causing that texel to appear to grow and shrink depending on its screen position.

https://github.com/operation404/Godot-Tilemap-Mesh-Rendering/assets/12285227/e0971477-f67a-4fea-96e0-88eca3789c21

## Specifics

The following list is all the differences between this and the default tilemap rendering method.

- Fragments are checked if they lie on the border of multiple tiles or the inner region of a single tile before sampling.
- Fragments inside a tile contained inside a single texel will be that texels color, otherwise it will be the linear blend of all texels it overlaps.
  - The algorithm used for modifying UV coordinates is slightly modified from the work of CptPotato. His original algorithm can be found [here](https://github.com/CptPotato/GodotThings/tree/master/SmoothPixelFiltering).
- Fragments on the border of multiple tiles are a linear blend of the texels from each tile they overlap.
- Fragments that sample from texture regions with entirely transparent texels use an average of the color channels of all non-blank texels when linearly filtering.
  - Godot has a texture import setting "Fix Alpha Border" that fixes this on non-atlas textures, but it produces visual artifacts on atlas textures due to lacking knowledge about which texture regions belong to which tile. This can result in colors bleeding in from neighboring tile regions.
- If you choose to use mipmaps, the provided compute shader generates mipmaps for each tile in the atlas while ensuring it does not sampling outside a given tile's region, preventing color bleeding.

## Files and Classes

The main files of interest are in the `source` directory. There are two C# classes for the node and a helper class, and two shaders.

The `TilemapMeshDisplay` node has a `TileMapLayer` property which tells it what to display. The `TilemapMeshDisplay` does not replace the original `TileMapLayer`. I recommend assigning the `TileMapLayer` as a child of the `TilemapMeshDisplay` and disabling its visibility. This way you can still take advantage of the physics and other properties of the tilemap while using the mesh-based rendering.

The `TilemapMeshDisplay.gdshader` shader is used by the `TilemapMeshDisplay` node. The node will automatically create and set a material with this shader based on the properties of the tilemap and display nodes.

The `GenerateCustomTilesetMipmaps.comp` is a compute shader that generates tileset mipmaps while ensuring the different tile atlas regions do not bleed into each other. If you want to use mipmaps, you should generate them with this shader.

The `TilesetHelper` class is a utility class with a few exposed public methods for taking in a `TileSetAtlasSource` and computing a new texture with mipmaps from the source's original texture. In order to use this, you must first set up the atlas source and properly define all of the tiles in the atlas texture. The shader only generates mipmaps for defined tiles, and needs to know where each tile is in order to avoid accidentally sampling from neighboring tile regions in order to prevent bleeding.
