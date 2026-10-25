using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

[TestClass]
public sealed class TerrainTests
{
    [TestMethod]
    public void GzneHeadersMatchReference()
    {
        foreach (var expected in ReferenceVectors.GzneHeaders)
        {
            var actual = GzneHeader.Parse(expected.Data);

            if (!expected.Valid)
            {
                Assert.IsNull(actual, expected.Label);
                continue;
            }

            Assert.IsNotNull(actual, expected.Label);
            Assert.AreEqual(expected.Version, actual.Version, expected.Label + ": version");
            Assert.AreEqual(expected.A, actual.A, expected.Label + ": a");
            Assert.AreEqual(expected.B, actual.B, expected.Label + ": b");
            Assert.AreEqual(expected.Edge, actual.Edge, expected.Label + ": edge");
            Assert.AreEqual(expected.CellSize, actual.CellSize, 0.0, expected.Label + ": cell size");
            Assert.AreEqual(expected.MinX, actual.MinX, expected.Label + ": min x");
            Assert.AreEqual(expected.MinZ, actual.MinZ, expected.Label + ": min z");
            Assert.AreEqual(expected.ExtX, actual.ExtX, expected.Label + ": ext x");
            Assert.AreEqual(expected.ExtZ, actual.ExtZ, expected.Label + ": ext z");
            Assert.AreEqual(expected.BigEndian, actual.BigEndian, expected.Label + ": byte order");
        }
    }

    [TestMethod]
    public void FloorCellMatchesReference()
    {
        foreach (var (value, cellSize, cell) in ReferenceVectors.FloorCells)
            Assert.AreEqual(cell, Terrain.FloorCell(value, cellSize), $"floor({value} / {cellSize})");
    }

    [TestMethod]
    public void TileOriginMatchesReference()
    {
        foreach (var (cell, edge, origin) in ReferenceVectors.TileOrigins)
            Assert.AreEqual(origin, Terrain.TileOrigin(cell, edge), $"tile origin of cell {cell}, edge {edge}");
    }

    [TestMethod]
    public void TilesNearMatchReference()
    {
        var header = GzneHeader.Parse(ReferenceVectors.GzneHeaders[0].Data);

        Assert.IsNotNull(header);

        foreach (var expected in ReferenceVectors.TilesNear)
        {
            var actual = Terrain.TilesNear(header, expected.X, expected.Z, expected.Radius)
                .Select(tile => (tile.X, tile.Z, tile.Inside))
                .ToArray();

            CollectionAssert.AreEqual(
                expected.Tiles.Select(tile => (tile.Ox, tile.Oz, tile.Inside)).ToArray(),
                actual,
                $"tiles near ({expected.X}, {expected.Z}) r={expected.Radius}");
        }
    }

    [TestMethod]
    public void TileFileNamesMatchReference()
    {
        foreach (var (zone, ox, oz, gcnk, gck2) in ReferenceVectors.TileFileNames)
            CollectionAssert.AreEqual(new[] { gcnk, gck2 }, Terrain.TileFileNames(zone, ox, oz), zone);
    }
}
