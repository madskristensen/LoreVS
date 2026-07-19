 using System;
 using System.IO;
 using LoreVS.SourceControl;
 using Microsoft.VisualStudio.TestTools.UnitTesting;
 
 namespace LoreVS.Tests
 {
     [TestClass]
     public class LoreRepositoryMonitorTests
     {
         private string _tempRoot = string.Empty;
 
         [TestInitialize]
         public void Setup()
         {
             _tempRoot = Path.Combine(Path.GetTempPath(), "lorevs_monitor_" + Guid.NewGuid().ToString("N"));
             Directory.CreateDirectory(_tempRoot);
         }
 
         [TestCleanup]
         public void Cleanup()
         {
             try
             {
                 if (Directory.Exists(_tempRoot))
                 {
                     Directory.Delete(_tempRoot, recursive: true);
                 }
             }
             catch
             {
                 // Best effort; the OS reclaims the temp directory eventually.
             }
         }
 
         [TestMethod]
         public void ExpandFileMoves_FileRename_ReturnsExactPair()
         {
             string oldPath = Path.Combine(_tempRoot, "old.txt");
             string newPath = Path.Combine(_tempRoot, "new.txt");
 
             var moves = LoreRepositoryMonitor.ExpandFileMoves(
                 _tempRoot, new[] { oldPath }, new[] { newPath });
 
             Assert.AreEqual(1, moves.Count);
             Assert.AreEqual(oldPath, moves[0].OldPath);
             Assert.AreEqual(newPath, moves[0].NewPath);
         }
 
         [TestMethod]
         public void ExpandFileMoves_PathCrossingRepositoryBoundary_IsIgnored()
         {
             string outside = Path.Combine(Path.GetDirectoryName(_tempRoot), "outside.txt");
             string inside = Path.Combine(_tempRoot, "inside.txt");
 
             var movedIn = LoreRepositoryMonitor.ExpandFileMoves(
                 _tempRoot, new[] { outside }, new[] { inside });
             var movedOut = LoreRepositoryMonitor.ExpandFileMoves(
                 _tempRoot, new[] { inside }, new[] { outside });
 
             Assert.AreEqual(0, movedIn.Count);
             Assert.AreEqual(0, movedOut.Count);
         }
 
         [TestMethod]
         public void ExpandFileMoves_DirectoryRename_ExpandsFilesRecursively()
         {
             string oldDirectory = Path.Combine(_tempRoot, "old");
             string newDirectory = Path.Combine(_tempRoot, "new");
             string topLevel = Path.Combine(newDirectory, "one.txt");
             string nested = Path.Combine(newDirectory, "child", "two.txt");
             Directory.CreateDirectory(Path.GetDirectoryName(nested));
             File.WriteAllText(topLevel, "one");
             File.WriteAllText(nested, "two");
 
             var moves = LoreRepositoryMonitor.ExpandFileMoves(
                 _tempRoot, new[] { oldDirectory }, new[] { newDirectory });
 
             Assert.AreEqual(2, moves.Count);
             Assert.IsTrue(moves.Exists(move =>
                 move.OldPath == Path.Combine(oldDirectory, "one.txt") && move.NewPath == topLevel));
             Assert.IsTrue(moves.Exists(move =>
                 move.OldPath == Path.Combine(oldDirectory, "child", "two.txt") && move.NewPath == nested));
         }
 
         [TestMethod]
         public void ExpandFileMoves_LoreMetadata_IsIgnored()
         {
             string oldPath = Path.Combine(_tempRoot, ".lore", "old");
             string newPath = Path.Combine(_tempRoot, ".lore", "new");
 
             var moves = LoreRepositoryMonitor.ExpandFileMoves(
                 _tempRoot, new[] { oldPath }, new[] { newPath });
 
             Assert.AreEqual(0, moves.Count);
         }
     }
 }
