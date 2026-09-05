namespace EndlessSky.Tests.Presentation
{
    using System;
    using System.IO;
    using EndlessSky.Game;
    using GdUnit4;
    using Godot;
    using static GdUnit4.Assertions;

    [TestSuite]
    public class SaveSlotTest
    {
        [TestCase]
        [RequireGodotRuntime]
        public void ACompleteSaveReplacesThePreviousFile()
        {
            string path = $"user://save-test-{Guid.NewGuid():N}.txt";
            string absolute = ProjectSettings.GlobalizePath(path);
            try
            {
                AssertBool(SaveSlot.Save("first game", path)).IsTrue();
                AssertBool(SaveSlot.Save("replacement game: Δ\n", path)).IsTrue();
                AssertString(SaveSlot.Load(path)!).IsEqual("replacement game: Δ\n");
                AssertArray(Directory.GetFiles(Path.GetDirectoryName(absolute)!,
                    Path.GetFileName(absolute) + ".*.tmp")).IsEmpty();
            }
            finally { File.Delete(absolute); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public void AFailedReplacementLeavesTheDestinationAndNoTemporaryFile()
        {
            string path = $"user://save-test-{Guid.NewGuid():N}";
            string absolute = ProjectSettings.GlobalizePath(path);
            Directory.CreateDirectory(absolute);
            string marker = Path.Combine(absolute, "preserve.txt");
            File.WriteAllText(marker, "preserved");
            try
            {
                // Moving a file over a directory fails on both Windows and Linux.
                AssertBool(SaveSlot.Save("new game", path)).IsFalse();
                AssertString(File.ReadAllText(marker)).IsEqual("preserved");
                AssertArray(Directory.GetFiles(Path.GetDirectoryName(absolute)!,
                    Path.GetFileName(absolute) + ".*.tmp")).IsEmpty();
            }
            finally
            {
                File.Delete(marker);
                Directory.Delete(absolute);
            }
        }
    }
}
