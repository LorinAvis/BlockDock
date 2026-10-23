#include "cubiomes/generator.h"
#include "cubiomes/finders.h"
#include "cubiomes/util.h"
#include <stdlib.h>

#define API __declspec(dllexport)
static const int versions[] = { MC_1_7_10, MC_1_8_9, MC_1_9_4, MC_1_10_2,
    MC_1_11_2, MC_1_12_2, MC_1_13_2, MC_1_14_4, MC_1_15_2, MC_1_16_1,
    MC_1_16_5, MC_1_17_1, MC_1_18_2, MC_1_19_2, MC_1_19_4, MC_1_20_6,
    MC_1_21_1, MC_1_21_3 };
static int valid(int index, int dimension) { return index >= 0 && index < (int)(sizeof(versions)/sizeof(versions[0])) && dimension >= -1 && dimension <= 1; }
API int bd_seed_abi(void) { return 1; }
API const char *bd_biome_name(int version, int biome) { if (!valid(version, 0)) return "unknown"; return biome2str(versions[version], biome); }
API int bd_seed_biome(int version, int64_t seed, int x, int z) {
    if (!valid(version, 0)) return -1;
    Generator *g = (Generator *)malloc(sizeof(Generator)); if (!g) return -1;
    setupGenerator(g, versions[version], 0); applySeed(g, DIM_OVERWORLD, (uint64_t)seed);
    int biome = getBiomeAt(g, 1, x, 63, z); free(g); return biome;
}
API int bd_seed_render(int version, int64_t seed, int dimension, int minx, int minz,
    int scale, int width, int large, int *colors, int *biomes) {
    if (!valid(version, dimension) || width < 1 || width > 1024 ||
        (scale != 1 && scale != 4 && scale != 16 && scale != 64)) return -1;
    Generator *g = (Generator *)malloc(sizeof(Generator)); if (!g) return -2;
    setupGenerator(g, versions[version], large ? LARGE_BIOMES : 0);
    applySeed(g, dimension, (uint64_t)seed);
    Range range = {0}; range.scale=scale; range.x=minx/scale; range.z=minz/scale;
    range.sx=width; range.sz=width; range.y=scale == 1 ? 63 : 15; range.sy=1;
    int *cache=allocCache(g, range); if (!cache) { free(g); return -2; }
    int result=genBiomes(g, cache, range);
    unsigned char palette[256][3]; initBiomeColors(palette);
    if (!result) for (int i=0; i<width*width; i++) {
        int biome=cache[i]; biomes[i]=biome;
        colors[i]=biome >= 0 && biome < 256 ? ((int)palette[biome][0]<<16)|((int)palette[biome][1]<<8)|palette[biome][2] : 0x808080;
    }
    free(cache); free(g); return result;
}
static int floor_div(int x, int d) { int q=x/d; return q-(x%d<0); }
API int bd_seed_features(int version, int64_t seed, int dimension, int minx, int minz,
    int span, int large, int *output, int capacity) {
    if (!valid(version, dimension) || span<1 || span>16384 || capacity<0 || capacity>4096) return -1;
    static const int types[]={Village,Desert_Pyramid,Jungle_Temple,Swamp_Hut,Igloo,
        Monument,Mansion,Outpost,Ruined_Portal,Ancient_City,Treasure,Trail_Ruins,
        Trial_Chambers,Fortress,Bastion,End_City,Ruined_Portal_N};
    Generator *g=(Generator *)malloc(sizeof(Generator)); if (!g) return -2;
    setupGenerator(g,versions[version],large ? LARGE_BIOMES : 0); applySeed(g,dimension,(uint64_t)seed);
    int count=0;
    for (int type=0; type<(int)(sizeof(types)/sizeof(types[0])); type++) {
        StructureConfig config;
        if (!getStructureConfig(types[type],versions[version],&config) || config.dim!=dimension || config.regionSize<1) continue;
        int spacing=config.regionSize*16;
        for (int rz=floor_div(minz,spacing); rz<=floor_div(minz+span-1,spacing); rz++)
            for (int rx=floor_div(minx,spacing); rx<=floor_div(minx+span-1,spacing); rx++) {
                Pos pos;
                if (!getStructurePos(types[type],versions[version],(uint64_t)seed,rx,rz,&pos)) continue;
                if (pos.x<minx || pos.z<minz || pos.x>=minx+span || pos.z>=minz+span) continue;
                if (!isViableStructurePos(types[type],g,pos.x,pos.z,0)) continue;
                if (count<capacity) { output[count*3]=type; output[count*3+1]=pos.x; output[count*3+2]=pos.z; count++; }
            }
    }
    free(g); return count;
}
