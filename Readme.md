# Custom Tilemap Rendering for Non-Pixel Perfect Projects

This project countains the sources for a new node and accompanying shaders that improve the visual clarity of tilemaps when used in non-pixel perfect projects. This node was originally created for pixel art games that didn't use pixel-perfect alignment, but it should provide some benefits for tilemaps with higher resolution art as well.

This tool is still a work in progress, and while it is mostly finished, there are still a few improvements I'm working and likely some bugs to find and fix still.

The source code for the new node and helper methods are written in C#. I unfortunately never bothered to learn GDScript and I wasn't really interested in doing so just to port this. My apologies.

## What Does It Do

The following list is all the differences between this and the default tilemap rendering method.

- Fragments are checked if they lie on the border of multiple tiles or the inner region of a single tile before sampling.
- Fragments inside a tile will be the color of a single texel if the fragment is contained entirely within that texel, otherwise it will be the result of a basic linear sampling.
  - The algorithm I use was modified from another algorithm originally by CptPotato. His version can be found [here](https://github.com/CptPotato/GodotThings/tree/master/SmoothPixelFiltering).
- Fragments on the border of multiple tiles use a custom sampling method with samples texels from each tile and then performs a linear filtering on them.
- Fragments that sample from texture regions with entirely transparent blank texels use an average of the color channels of all non-blank texels when linearly filtering.
  - Godot has a texture import setting "Fix Alpha Border" that fixes this on non-atlas textures, but it produces visual artifacts on atlas textures due to lacking knowledge about which texture regions belong to which tile. This can result in colors bleeding in from neighboring tile regions.
- If you choose to use mipmaps, the provided compute shader generates mipmaps for each tile in the atlas while ensuring it does not sampling outside a given tile's region, preventing color bleeding.

When rendering a tilemap with this method and perfectly aligned to a grid as well as scaled such that each tile has the same number of pixels, this method should look no different than the default. The differences should only be noticeable when the tilemap is not perfectly aligned to the screen or is scaled/rotated.

https://github.com/operation404/Godot-Tilemap-Mesh-Rendering/raw/master/videos/integer%20scaled.mp4

## What Is It

The main files of interest are in the `source` directory. There are two C# classes for the node and a helper class, and two shaders.

The `TilemapMeshDisplay` node has a `TileMapLayer` property which tells it what to display. The `TilemapMeshDisplay` does not replace the original `TileMapLayer`, it only renders the tilemap. I recommend assigning the `TileMapLayer` as a child of the `TilemapMeshDisplay` and disabling its visibility. This way you can still take advantage of the physics and other properties of the tilemap while using the mesh-based rendering.

The `TilemapMeshDisplay.gdshader` shader is used by the `TilemapMeshDisplay` node. The node will automatically create and set a material with this shader based on the properties of the tilemap and display nodes.

The `GenerateCustomTilesetMipmaps.comp` is a compute shader that generates tileset mipmaps while ensuring the different tile atlas regions do not bleed into each other. If you want to use mipmaps, you should generate them with this shader.

The `TilesetHelper` class is a utility class with a few exposed public methods for taking in a `TileSetAtlasSource` and computing a new texture with mipmaps from the source's original texture. In order to use this, you must first set up the atlas source and properly define all of the tiles in the atlas texture. The shader only generates mipmaps for defined tiles, and needs to know where each tile is in order to avoid accidentally sampling from neighboring tile regions in order to prevent bleeding.
