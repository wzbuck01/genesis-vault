// Program.cs — HiveMind.Geometry Phase 0 entry point
// Runs RFC-007C saturation algorithm, outputs B_G^class seed table
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.IO;
using HiveMind.Geometry;

var outDir = args.Length > 0 ? args[0] : ".";

Console.WriteLine(RelationAlgebra.Report());

var json = RelationAlgebra.ToJson();
var outPath = Path.Combine(outDir, "bg-class-seed.json");
File.WriteAllText(outPath, json);
Console.WriteLine($"Written: {outPath}");
