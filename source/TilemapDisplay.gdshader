shader_type canvas_item;

// ============================================================================
//                                  Constants
// ============================================================================

const int MAX_LEVELS = 10;
const int MAX_USABLE_MIPMAPS = MAX_LEVELS - 1;

const int MAX_SOURCES = 15;
const int INVALID_ATLAS_ID = MAX_SOURCES; // Different name for clearer intent

// ============================================================================
//                                  Uniforms
// ============================================================================

uniform ivec2 tile_size = ivec2(1, 1); // Each side length must be >= 1
uniform sampler2D map_data;
uniform sampler2D atlas[MAX_SOURCES];
uniform bool raw_in_instance_custom = true;

#ifdef USE_MIPMAPS
uniform float lod_bias = 0.0;
#endif

// ============================================================================
//                                  Varyings
// ============================================================================

varying flat ivec2 map_texel;
varying flat ivec2 tile_scale;
varying flat ivec2 main_tile_size;

#ifdef USE_MIPMAPS
varying flat float used_mipmaps;
#endif

// ============================================================================
//                                  Vertex
// ============================================================================

void vertex()
{
    vec4 raw_data = raw_in_instance_custom ? INSTANCE_CUSTOM : COLOR;
    map_texel = floatBitsToInt(raw_data.xy);
    tile_scale = floatBitsToInt(raw_data.zw);
    main_tile_size = tile_size * tile_scale;

#ifdef USE_MIPMAPS
    // Calculate the number of valid mipmaps for the tile size
    used_mipmaps = min(float(MAX_USABLE_MIPMAPS),
                       floor(log2(float(min(tile_size.x, tile_size.y)))));
#endif
}

// ============================================================================
//                                  Tile Data
// ============================================================================

struct TileData
{
    lowp int atlas_id;
    lowp ivec2 atlas_coord;
    bool valid;
};

TileData getTileData(ivec2 coord)
{
    TileData tile;
    int packed = floatBitsToInt(texelFetch(map_data, coord, 0).r);

    ivec2 map_size = textureSize(map_data, 0);
    bool valid_coord = coord.x >= 0 && coord.x < map_size.x && coord.y >= 0 &&
                       coord.y < map_size.y;

    int id = packed & 0xFF;
    bool valid_id = id < INVALID_ATLAS_ID;

    tile.valid = valid_coord && valid_id;
    // Clamp id so that it can be used safely to access arrays
    tile.atlas_id = clamp(id, 0, MAX_SOURCES - 1);
    tile.atlas_coord = ivec2((packed >> 8) & 0xFF, (packed >> 16) & 0xFF);

    return tile;
}

// ============================================================================
//                                  Helpers
// ============================================================================

#ifdef USE_MIPMAPS
float calculateLodBuiltIn(sampler2D tex, vec2 uv)
{
    float lod = textureQueryLod(tex, uv).y + lod_bias;
    return clamp(lod, 0.0, used_mipmaps);
}

void getLodTileDimensions(float lod, out ivec2 lower_dim, out ivec2 upper_dim)
{
    float lower_lod = floor(lod);
    lower_dim = tile_size / int(pow(2.0, lower_lod));
    upper_dim = lod > lower_lod ? lower_dim / 2 : lower_dim;
}
#endif

int createMask(bool maskCond) { return int(maskCond) << 31 >> 31; }

vec4 maskf4(vec4 val, int mask)
{
    return intBitsToFloat(floatBitsToInt(val) & ivec4(mask));
}

vec3 maskf3(vec3 val, int mask)
{
    return intBitsToFloat(floatBitsToInt(val) & ivec3(mask));
}

// ============================================================================
//                            Pixel Art UV Correction
// ============================================================================

vec2 calculateFragSize(vec2 uv)
{
    vec2 ddx = dFdxFine(uv);
    vec2 ddy = dFdyFine(uv);
    return sqrt(ddx * ddx + ddy * ddy);
}

// Modified to return the calculated uv
// https://github.com/CptPotato/GodotThings/tree/master/SmoothPixelFiltering
vec2 correctPixelArtUV(vec2 tile_uv, vec2 frag_size_in_uv)
{
    vec2 tex_size = vec2(main_tile_size);
    vec2 tex_coord = tile_uv * tex_size;

    vec2 last_texel_center = round(tex_coord) - vec2(0.5f);
    vec2 dist_to_last_center = tex_coord - last_texel_center;

    vec2 frags_per_texel = 1.0 / (tex_size * frag_size_in_uv);
    vec2 last_center_offset =
        clamp((dist_to_last_center - vec2(0.5f)) * frags_per_texel + vec2(0.5f),
              0.0f, 1.0f);

    return (last_texel_center + last_center_offset) / tex_size;
}

// ============================================================================
//                            Custom Texture Sampling
// ============================================================================

/* NOTE
    Blending rule: any pixel that has its alpha channel set to 0 is assumed to
    be a "blank pixel" and only its alpha channel should be used to affect other
    pixels. The color channels of that pixel will be set to the average of every
    non-blank pixel that is being sampled.

    Ex: In the case of a 4 color blend where only one pixel is blank, that
    pixel's color channels is set to the average of the 3 non-blank pixels.
*/
vec4 blendColor4(vec4 tl, vec4 tr, vec4 bl, vec4 br, vec2 dist)
{
    int tlm = createMask(tl.a == 0.0);
    int trm = createMask(tr.a == 0.0);
    int blm = createMask(bl.a == 0.0);
    int brm = createMask(br.a == 0.0);

    tl.rgb = maskf3(tl.rgb, ~tlm);
    tr.rgb = maskf3(tr.rgb, ~trm);
    bl.rgb = maskf3(bl.rgb, ~blm);
    br.rgb = maskf3(br.rgb, ~brm);

    int count = 4 + tlm + trm + blm + brm; // mask of all 1 bits is equal to -1
    ivec3 avg = floatBitsToInt((tl.rgb + tr.rgb + bl.rgb + br.rgb) /
                               float(max(count, 1)));

    tl.rgb = intBitsToFloat(floatBitsToInt(tl.rgb) | (avg & ivec3(tlm)));
    tr.rgb = intBitsToFloat(floatBitsToInt(tr.rgb) | (avg & ivec3(trm)));
    bl.rgb = intBitsToFloat(floatBitsToInt(bl.rgb) | (avg & ivec3(blm)));
    br.rgb = intBitsToFloat(floatBitsToInt(br.rgb) | (avg & ivec3(brm)));

    // Basic linear filtering
    vec4 top_color = mix(tl, tr, dist.x);
    vec4 bot_color = mix(bl, br, dist.x);
    return mix(top_color, bot_color, dist.y);
}

vec4 sampleTexels(sampler2D texture, vec2 texel_coord, int lod)
{
    ivec2 tl = ivec2(floor(texel_coord));
    ivec2 br = ivec2(ceil(texel_coord));
    ivec2 tr = ivec2(br.x, tl.y);
    ivec2 bl = ivec2(tl.x, br.y);

    vec4 tl_color = texelFetch(texture, tl, lod);
    vec4 tr_color = texelFetch(texture, tr, lod);
    vec4 bl_color = texelFetch(texture, bl, lod);
    vec4 br_color = texelFetch(texture, br, lod);

    return blendColor4(tl_color, tr_color, bl_color, br_color,
                       fract(texel_coord));
}

// ============================================================================
//                                  Fragment
// ============================================================================

#ifdef USE_MIPMAPS
vec4 singleTileSampleLod(TileData tile, vec2 uv, float lod)
{

    ivec2 lower_tile_dim, upper_tile_dim;
    getLodTileDimensions(lod, lower_tile_dim, upper_tile_dim);

    vec2 lower_coord = uv * vec2(lower_tile_dim * tile_scale) - vec2(0.5) +
                       vec2(tile.atlas_coord * lower_tile_dim);
    vec2 upper_coord = uv * vec2(upper_tile_dim * tile_scale) - vec2(0.5) +
                       vec2(tile.atlas_coord * upper_tile_dim);

    vec4 lower_color =
        sampleTexels(atlas[tile.atlas_id], lower_coord, int(floor(lod)));
    vec4 upper_color =
        sampleTexels(atlas[tile.atlas_id], upper_coord, int(ceil(lod)));

    return mix(lower_color, upper_color, fract(lod));
}
#else
vec4 singleTileSample(TileData tile, vec2 uv)
{
    vec2 lower_coord = uv * vec2(tile_size * tile_scale) - vec2(0.5) +
                       vec2(tile.atlas_coord * tile_size);

    return sampleTexels(atlas[tile.atlas_id], lower_coord, 0);
}
#endif

#ifdef USE_MIPMAPS
vec4 multiTileSampleLod(vec2 uv, vec2 dis_from_inner_region, float lod)
{
    // Create bitmasks for the 4 conditions determining tile position
    ivec2 less = ivec2(createMask(dis_from_inner_region.x < 0.0),
                       createMask(dis_from_inner_region.y < 0.0));
    ivec2 great = ivec2(createMask(dis_from_inner_region.x > 0.0),
                        createMask(dis_from_inner_region.y > 0.0));

    // Calculate the map coordinates to fetch tile data from.
    ivec2 map_texel_offset = ivec2(uv * vec2(tile_scale));
    ivec2 adj_map_tex = map_texel + map_texel_offset;
    ivec2 map_tl = ((adj_map_tex - ivec2(1)) & less) | (adj_map_tex & ~less);
    ivec2 map_br = ((adj_map_tex + ivec2(1)) & great) | (adj_map_tex & ~great);
    ivec2 map_tr = ivec2(map_br.x, map_tl.y);
    ivec2 map_bl = ivec2(map_tl.x, map_br.y);

    TileData tl_tile = getTileData(map_tl);
    TileData tr_tile = getTileData(map_tr);
    TileData bl_tile = getTileData(map_bl);
    TileData br_tile = getTileData(map_br);

    float lower_lod = floor(lod);
    float upper_lod = ceil(lod);
    ivec2 lower_tile_dim, upper_tile_dim;
    getLodTileDimensions(lod, lower_tile_dim, upper_tile_dim);
    ivec2 lower_main_dim = lower_tile_dim * tile_scale;
    ivec2 upper_main_dim = upper_tile_dim * tile_scale;

    // Find frag's the relative coord within a standard tile
    // Shift by 0.5 so that floor and ceil find all 4 texels sampled by uv
    vec2 lower_tile_coord = uv * vec2(lower_main_dim) - vec2(0.5);
    vec2 upper_tile_coord = uv * vec2(upper_main_dim) - vec2(0.5);
    lower_tile_coord -= vec2(map_texel_offset * lower_tile_dim);
    upper_tile_coord -= vec2(map_texel_offset * upper_tile_dim);

    // Get tl and br texel coords, not accounting for tile region bounds
    ivec2 lower_tl = ivec2(floor(lower_tile_coord));
    ivec2 lower_br = ivec2(ceil(lower_tile_coord));
    ivec2 upper_tl = ivec2(floor(upper_tile_coord));
    ivec2 upper_br = ivec2(ceil(upper_tile_coord));

    // Adjust coords based on how uv was clamped.
    // If x value was to left of tile bounds, wrap coord around to right.
    // If x value was to right of tile bounds, wrap coord around to left.
    // Same logic holds for the y coord, but with going above or below.
    lower_tl += less & lower_tile_dim;
    lower_br += great & -lower_tile_dim;
    upper_tl += less & upper_tile_dim;
    upper_br += great & -upper_tile_dim;

    // Get tr and bl texel coords using already corrected tl and br values
    ivec2 lower_tr = ivec2(lower_br.x, lower_tl.y);
    ivec2 lower_bl = ivec2(lower_tl.x, lower_br.y);
    ivec2 upper_tr = ivec2(upper_br.x, upper_tl.y);
    ivec2 upper_bl = ivec2(upper_tl.x, upper_br.y);

    // Translate texel coord to the location on the atlas the tile belongs to
    lower_tl += tl_tile.atlas_coord * lower_tile_dim;
    lower_tr += tr_tile.atlas_coord * lower_tile_dim;
    lower_bl += bl_tile.atlas_coord * lower_tile_dim;
    lower_br += br_tile.atlas_coord * lower_tile_dim;
    upper_tl += tl_tile.atlas_coord * upper_tile_dim;
    upper_tr += tr_tile.atlas_coord * upper_tile_dim;
    upper_bl += bl_tile.atlas_coord * upper_tile_dim;
    upper_br += br_tile.atlas_coord * upper_tile_dim;

    // Create masks to mask out colors from invalid tiles
    int tl_mask = createMask(tl_tile.valid);
    int tr_mask = createMask(tr_tile.valid);
    int bl_mask = createMask(bl_tile.valid);
    int br_mask = createMask(br_tile.valid);

    // Lower lod sampling
    // Mask colors to all 0's if atlas id was invalid (0xF)
    int llod = int(lower_lod);
    vec4 lower_tl_color =
        maskf4(texelFetch(atlas[tl_tile.atlas_id], lower_tl, llod), tl_mask);
    vec4 lower_tr_color =
        maskf4(texelFetch(atlas[tr_tile.atlas_id], lower_tr, llod), tr_mask);
    vec4 lower_bl_color =
        maskf4(texelFetch(atlas[bl_tile.atlas_id], lower_bl, llod), bl_mask);
    vec4 lower_br_color =
        maskf4(texelFetch(atlas[br_tile.atlas_id], lower_br, llod), br_mask);

    // Upper lod sampling
    int ulod = int(upper_lod);
    vec4 upper_tl_color =
        maskf4(texelFetch(atlas[tl_tile.atlas_id], upper_tl, ulod), tl_mask);
    vec4 upper_tr_color =
        maskf4(texelFetch(atlas[tr_tile.atlas_id], upper_tr, ulod), tr_mask);
    vec4 upper_bl_color =
        maskf4(texelFetch(atlas[bl_tile.atlas_id], upper_bl, ulod), bl_mask);
    vec4 upper_br_color =
        maskf4(texelFetch(atlas[br_tile.atlas_id], upper_br, ulod), br_mask);

    vec4 lower_color =
        blendColor4(lower_tl_color, lower_tr_color, lower_bl_color,
                    lower_br_color, fract(lower_tile_coord));
    vec4 upper_color =
        blendColor4(upper_tl_color, upper_tr_color, upper_bl_color,
                    upper_br_color, fract(upper_tile_coord));

    return mix(lower_color, upper_color, fract(lod));
}
#else
vec4 multiTileSample(vec2 uv, vec2 dis_from_inner_region)
{
    // Create bitmasks for the 4 conditions determining tile position
    ivec2 less = ivec2(createMask(dis_from_inner_region.x < 0.0),
                       createMask(dis_from_inner_region.y < 0.0));
    ivec2 great = ivec2(createMask(dis_from_inner_region.x > 0.0),
                        createMask(dis_from_inner_region.y > 0.0));

    ivec2 map_texel_offset = ivec2(uv * vec2(tile_scale));
    ivec2 adj_map_tex = map_texel + map_texel_offset;
    ivec2 map_tl = ((adj_map_tex - ivec2(1)) & less) | (adj_map_tex & ~less);
    ivec2 map_br = ((adj_map_tex + ivec2(1)) & great) | (adj_map_tex & ~great);
    ivec2 map_tr = ivec2(map_br.x, map_tl.y);
    ivec2 map_bl = ivec2(map_tl.x, map_br.y);

    TileData tl_tile = getTileData(map_tl);
    TileData tr_tile = getTileData(map_tr);
    TileData bl_tile = getTileData(map_bl);
    TileData br_tile = getTileData(map_br);

    // Find frag's the relative coord within a standard tile
    // Shift by 0.5 so that floor and ceil find all 4 texels sampled by uv
    vec2 tile_coord = uv * vec2(tile_size * tile_scale) - vec2(0.5) -
                      vec2(map_texel_offset * tile_size);

    // Get tl and br texel coords, not accounting for tile region bounds
    ivec2 tl = ivec2(floor(tile_coord));
    ivec2 br = ivec2(ceil(tile_coord));

    // Adjust coords based on how uv was clamped.
    // If x value was to left of tile bounds, wrap coord around to right.
    // If x value was to right of tile bounds, wrap coord around to left.
    // Same logic holds for the y coord, but with going above or below.
    tl += less & tile_size;
    br += great & -tile_size;

    // Get tr and bl texel coords using already corrected tl and br values
    ivec2 tr = ivec2(br.x, tl.y);
    ivec2 bl = ivec2(tl.x, br.y);

    // Translate texel coord to the location on the atlas the tile belongs to
    tl += tl_tile.atlas_coord * tile_size;
    tr += tr_tile.atlas_coord * tile_size;
    bl += bl_tile.atlas_coord * tile_size;
    br += br_tile.atlas_coord * tile_size;

    // Create masks to mask out colors from invalid tiles
    int tl_mask = createMask(tl_tile.valid);
    int tr_mask = createMask(tr_tile.valid);
    int bl_mask = createMask(bl_tile.valid);
    int br_mask = createMask(br_tile.valid);

    // Mask colors to all 0's if atlas id was invalid (0xF)
    vec4 tl_color = maskf4(texelFetch(atlas[tl_tile.atlas_id], tl, 0), tl_mask);
    vec4 tr_color = maskf4(texelFetch(atlas[tr_tile.atlas_id], tr, 0), tr_mask);
    vec4 bl_color = maskf4(texelFetch(atlas[bl_tile.atlas_id], bl, 0), bl_mask);
    vec4 br_color = maskf4(texelFetch(atlas[br_tile.atlas_id], br, 0), br_mask);

    return blendColor4(tl_color, tr_color, bl_color, br_color,
                       fract(tile_coord));
}
#endif

void fragment()
{
    vec4 color = vec4(0.0);
    vec2 frag_size_in_uv = calculateFragSize(UV);

    // Inner region of a tile is where a frag only samples that specific tile
    vec2 low_thresh = frag_size_in_uv * 0.5;
    vec2 dis_from_inner_region = UV - clamp(UV, low_thresh, 1.0 - low_thresh);

    // If frag isn't in the tile's inner region, it must sample neighbors too
    if (dis_from_inner_region != vec2(0.0))
    {
        vec2 adj_uv = correctPixelArtUV(UV, frag_size_in_uv);

#ifdef USE_MIPMAPS
        float lod = calculateLodBuiltIn(map_data, UV);
        color = multiTileSampleLod(adj_uv, dis_from_inner_region, lod);
#else
        color = multiTileSample(adj_uv, dis_from_inner_region);
#endif
    }
    else // Frag is contained entirely within its main tile
    {
        TileData main_tile = getTileData(map_texel);

        if (main_tile.valid)
        {
            vec2 adj_uv = correctPixelArtUV(UV, frag_size_in_uv);

            // Sometimes a frag isn't part of another tile, but has a uv that is
            // just barely past the outermost texel center due to floating point
            // rounding errors. Clamp uv to ensure texels aren't taken from
            // other tile regions.
            vec2 uv_thresh = vec2(0.5) / vec2(main_tile_size);
            adj_uv = clamp(adj_uv, uv_thresh, vec2(1.0) - uv_thresh);

#ifdef USE_MIPMAPS
            float lod = calculateLodBuiltIn(map_data, UV);
            color = singleTileSampleLod(main_tile, adj_uv, lod);
#else
            color = singleTileSample(main_tile, adj_uv);
#endif
        }
    }

    COLOR = color;
}
