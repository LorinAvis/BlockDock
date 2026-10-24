using BlockDock;

static class StrongholdChecks
{
    static void Check(bool good, string label) { if (!good) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    static SeedMapResult Map(string version, int x, int z, int span = 1024, bool large = false, int dimension = 0, long seed = 262) =>
        OfflineSeedMap.Render(false, OfflineSeedMap.Match(version)!, seed, x, z, span, dimension, large, null, default);
    static (int X, int Z)[] Strongholds(SeedMapResult map) => map.Features.Where(f => f.Name == "末地要塞").Select(f => (f.X, f.Z)).OrderBy(p => p.X).ThenBy(p => p.Z).ToArray();
    static bool Exactly(SeedMapResult map, params (int X, int Z)[] expected) => Strongholds(map).SequenceEqual(expected.OrderBy(p => p.X).ThenBy(p => p.Z));

    public static void Run()
    {
        // Golden coordinates come from direct, unoptimized initFirstStronghold / nextStronghold
        // iteration in vendored Cubiomes e61f90580cbdd883214a8054670dacae655e59c0.
        // These points identify the stronghold staircase's chunk, not its portal room.
        var normal = Map("1.18.2", 1800, 1200);
        var large = Map("1.18.2", 1800, 1200, large: true);
        Check(Exactly(normal, (1844, 1204)) && Exactly(large, (1796, 1204)),
            "Java 1.18.2 stronghold golden coordinates use the selected normal or large-biome generator");
        Check(normal.Features.Single(f => f.Name == "末地要塞").Description.Contains("主世界")
            && normal.Features.Single(f => f.Name == "末地要塞").Description.Contains("不是末地传送门房间"),
            "stronghold marker describes an Overworld prediction rather than an exact portal-room location");

        var old = Map("1.7.10", 0, 0, 4096);
        Check(Exactly(old, (68, -620), (980, 692), (-620, 500)),
            "pre-1.9 world exposes its three strongholds without an extra iterator result");

        // Span 1,024 has block-scale sampling, allowing exact half-open viewport checks.
        var atStart = Map("1.18.2", 580, -1132);
        var atEnd = Map("1.18.2", -444, -1132);
        Check(atStart.Image.MinX == 68 && atStart.Image.MinZ == -1644 && Exactly(atStart, (68, -1644))
            && atEnd.Image.MinX + 1024 == 68 && Exactly(atEnd),
            "negative-coordinate stronghold view includes its start and excludes its right boundary");

        const long upperBitsSeed = (1L << 60) + 262;
        var newer = Map("1.20.6", 0, -1600, seed: upperBitsSeed);
        Check(Exactly(newer, (-76, -1580)) && !Strongholds(newer).Contains((-12, -1708)),
            "modern stronghold biome relocation retains upper world-seed bits beyond the ring's 48-bit seed");

        var nether = Map("1.20.6", 0, -1600, dimension: 1);
        var end = Map("1.20.6", 0, -1600, dimension: 2);
        Check(Exactly(nether) && Exactly(end), "Overworld strongholds are absent from Nether and End prediction maps");
        Console.WriteLine("STRONGHOLD CHECKS PASSED; offline seed data only");
    }
}
