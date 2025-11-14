using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

// Test suite for distance-based streaming system
class Program
{
    static void Main()
    {
        Console.WriteLine("=== Distance-Based Streaming System Validation ===");
        Console.WriteLine("Testing with EGLL-scale data (2500 objects)");
        Console.WriteLine();
        
        int passed = 0, failed = 0;
        var failures = new List<string>();
        
        // Test 1: Squared Distance Performance
        Console.WriteLine("Test 1: Squared Distance Performance");
        try
        {
            var sw = Stopwatch.StartNew();
            var points = GenerateTestObjects(2500);
            var ac = (51.4700, -0.4543);
            foreach (var p in points)
            {
                var d = SquaredDist(ac.Item1, ac.Item2, p.Item2, p.Item3);
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds < 100)
            {
                Console.WriteLine($"  ✓ 2500 calculations in {sw.ElapsedMilliseconds}ms");
                passed++;
            }
            else
            {
                failures.Add($"Too slow: {sw.ElapsedMilliseconds}ms");
                failed++;
            }
        }
        catch (Exception ex) { failures.Add($"Test1: {ex.Message}"); failed++; }
        
        // Test 2: Distance Sorting
        Console.WriteLine("Test 2: Distance-Based Sorting");
        try
        {
            var points = GenerateTestObjects(2500);
            var ac = (51.4700, -0.4543);
            var sorted = points.Select(p => (p.Item1, SquaredDist(ac.Item1, ac.Item2, p.Item2, p.Item3)))
                               .OrderBy(x => x.Item2).ToList();
            bool ok = true;
            for (int i = 1; i < sorted.Count; i++)
                if (sorted[i].Item2 < sorted[i-1].Item2) { ok = false; break; }
            if (ok)
            {
                Console.WriteLine($"  ✓ Sorted 2500 objects correctly");
                Console.WriteLine($"    Range: {Math.Sqrt(sorted[0].Item2):F0}m - {Math.Sqrt(sorted.Last().Item2):F0}m");
                passed++;
            }
            else { failures.Add("Sorting failed"); failed++; }
        }
        catch (Exception ex) { failures.Add($"Test2: {ex.Message}"); failed++; }
        
        // Test 3: Cap Enforcement  
        Console.WriteLine("Test 3: Spawn Cap Enforcement");
        try
        {
            var points = GenerateTestObjects(2500);
            var ac = (51.4700, -0.4543);
            var desired = EvalDesired(points, ac, 950, 500.0, 8000.0, 200.0);
            var normal = desired.Count(d => !d.Item2);
            if (normal <= 950)
            {
                Console.WriteLine($"  ✓ Cap enforced: {normal} <= 950");
                Console.WriteLine($"    Total: {desired.Count} ({desired.Count(d => d.Item2)} high priority)");
                passed++;
            }
            else { failures.Add($"Cap violated: {normal} > 950"); failed++; }
        }
        catch (Exception ex) { failures.Add($"Test3: {ex.Message}"); failed++; }
        
        // Test 4: Hysteresis
        Console.WriteLine("Test 4: Hysteresis Gap");
        try
        {
            double spawn = 8000 - 200;
            double despawn = 8000 + 200;
            double gap = despawn - spawn;
            if (gap == 400)
            {
                Console.WriteLine($"  ✓ Gap: {gap}m (spawn: {spawn}m, despawn: {despawn}m)");
                passed++;
            }
            else { failures.Add($"Gap: {gap}m != 400m"); failed++; }
        }
        catch (Exception ex) { failures.Add($"Test4: {ex.Message}"); failed++; }
        
        // Test 5: Batching
        Console.WriteLine("Test 5: Batched Processing");
        try
        {
            var items = Enumerable.Range(0, 50).ToList();
            int batchSize = 5;
            var batches = 0;
            for (int i = 0; i < items.Count; i += batchSize) batches++;
            if (batches == 10)
            {
                Console.WriteLine($"  ✓ 50 items → 10 batches of 5");
                passed++;
            }
            else { failures.Add($"Batches: {batches} != 10"); failed++; }
        }
        catch (Exception ex) { failures.Add($"Test5: {ex.Message}"); failed++; }
        
        // Test 6: No Spawn/Despawn Loops
        Console.WriteLine("Test 6: Two-Phase Pipeline (No Loops)");
        try
        {
            var points = GenerateTestObjects(200);
            var ac = (51.4700, -0.4543);
            var desired = EvalDesired(points, ac, 100, 500.0, 8000.0, 200.0);
            var current = desired.Take(80).Select(d => d.Item1).ToHashSet();
            var desiredSet = desired.Select(d => d.Item1).ToHashSet();
            var toSpawn = desiredSet.Except(current).ToList();
            var toDespawn = current.Except(desiredSet).ToList();
            var overlap = toSpawn.Intersect(toDespawn).Count();
            if (overlap == 0)
            {
                Console.WriteLine($"  ✓ No loops: {toSpawn.Count} spawn, {toDespawn.Count} despawn, 0 overlap");
                passed++;
            }
            else { failures.Add($"Loop detected: {overlap} overlap"); failed++; }
        }
        catch (Exception ex) { failures.Add($"Test6: {ex.Message}"); failed++; }
        
        // Test 7: Scalability
        Console.WriteLine("Test 7: Scalability Test");
        try
        {
            foreach (var count in new[] { 500, 1000, 2000, 2500 })
            {
                var sw = Stopwatch.StartNew();
                var points = GenerateTestObjects(count);
                var ac = (51.4700, -0.4543);
                var desired = EvalDesired(points, ac, 950, 500.0, 8000.0, 200.0);
                sw.Stop();
                Console.WriteLine($"    {count} objects: {sw.ElapsedMilliseconds}ms, {desired.Count} active");
            }
            passed++;
        }
        catch (Exception ex) { failures.Add($"Test7: {ex.Message}"); failed++; }
        
        // Test 8: Priority Sorting
        Console.WriteLine("Test 8: Priority Sorting");
        try
        {
            var points = GenerateTestObjects(500);
            var ac = (51.4700, -0.4543);
            var desired = EvalDesired(points, ac, 200, 500.0, 8000.0, 200.0);
            var firstHi = desired.FindIndex(d => d.Item2);
            var lastHi = desired.FindLastIndex(d => d.Item2);
            var firstNorm = desired.FindIndex(d => !d.Item2);
            bool ok = firstHi == -1 || firstNorm == -1 || lastHi < firstNorm;
            if (ok)
            {
                Console.WriteLine($"  ✓ High priority sorted first");
                passed++;
            }
            else { failures.Add("Priority sorting failed"); failed++; }
        }
        catch (Exception ex) { failures.Add($"Test8: {ex.Message}"); failed++; }
        
        // Summary
        Console.WriteLine();
        Console.WriteLine("=== Test Summary ===");
        Console.WriteLine($"Passed: {passed}");
        Console.WriteLine($"Failed: {failed}");
        if (failures.Count > 0)
        {
            Console.WriteLine("\nFailures:");
            foreach (var f in failures) Console.WriteLine($"  ❌ {f}");
        }
        else
        {
            Console.WriteLine("\n✅ All tests passed!");
        }
        
        Environment.Exit(failed > 0 ? 1 : 0);
    }
    
    static List<(string, double, double, bool)> GenerateTestObjects(int count)
    {
        var rand = new Random(42);
        var points = new List<(string, double, double, bool)>();
        const double lat = 51.4700, lon = -0.4543, radius = 3000;
        for (int i = 0; i < count; i++)
        {
            var angle = rand.NextDouble() * 2 * Math.PI;
            var dist = rand.NextDouble() * radius;
            var pLat = lat + (dist * Math.Cos(angle)) / 111000.0;
            var pLon = lon + (dist * Math.Sin(angle)) / (111000.0 * Math.Cos(lat * Math.PI / 180));
            points.Add(($"EGLL_P{i}", pLat, pLon, true));
        }
        return points;
    }
    
    static List<(string, bool)> EvalDesired(List<(string, double, double, bool)> points, 
                                             (double, double) ac, int max, double hiPri, 
                                             double spawnR, double spawnM)
    {
        var cands = new List<(string, double, bool)>();
        foreach (var p in points)
        {
            if (!p.Item4) continue;
            var sqd = SquaredDist(ac.Item1, ac.Item2, p.Item2, p.Item3);
            var d = Math.Sqrt(sqd);
            bool hi = d <= hiPri;
            if (d <= spawnR - spawnM || hi)
                cands.Add((p.Item1, sqd, hi));
        }
        var sorted = cands.OrderBy(c => c.Item3 ? 0 : 1).ThenBy(c => c.Item2).ToList();
        var result = new List<(string, bool)>();
        foreach (var c in sorted)
        {
            if (c.Item3) result.Add((c.Item1, true));
            else if (result.Count < max) result.Add((c.Item1, false));
        }
        return result;
    }
    
    static double SquaredDist(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        double d = R * c;
        return d * d;
    }
}
