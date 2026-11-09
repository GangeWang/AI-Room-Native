using System.Security.Cryptography;
using AIROOMCore;
using Xunit;

public class AvatarAndBackupTests
{
    // MARK: Avatar crop math (PortraitFaceAnchor)

    static double Ifinite(double value) => value;

    [Fact]
    public void ConstrainedClampsZoomToTheGivenRange()
    {
        var tight = new PortraitFaceAnchor(0.5, 0.2, 99).Constrained(1000, 1200, (1, 8));
        Assert.Equal(8, tight.Zoom);
        var loose = new PortraitFaceAnchor(0.5, 0.2, 0.2).Constrained(1000, 1200, (1, 8));
        Assert.Equal(1, loose.Zoom);
    }

    [Fact]
    public void ConstrainedKeepsTheCircleInsideThePicture()
    {
        // A face on the far left with a high zoom must be pulled inwards,
        // otherwise the circular viewport shows empty space.
        var anchored = new PortraitFaceAnchor(0.01, 0.5, 6).Constrained(1000, 1200);
        Assert.InRange(anchored.X, 0.5 / 6, 1 - 0.5 / 6);
        Assert.InRange(anchored.Y, 0.5 / (6 * 1.2), 1 - 0.5 / (6 * 1.2));
    }

    [Fact]
    public void TranslatedMovesOppositeToTheDragAndStaysFitted()
    {
        var start = new PortraitFaceAnchor(0.5, 0.3, 4);
        // Dragging the artwork right (positive dragX) decreases X.
        var dragged = start.Translated(dragX: 100, dragY: 0, diameter: 230, sourceWidth: 1000, sourceHeight: 1200);
        Assert.True(dragged.X < start.X);
        Assert.Equal(start.Y, dragged.Y);
        // Repeated drags never leave the fitted range.
        var walk = start;
        for (var i = 0; i < 50; i++)
        {
            walk = walk.Translated(dragX: 25, dragY: -18, diameter: 230, sourceWidth: 1000, sourceHeight: 1200);
            Assert.InRange(walk.X, 0, 1);
            Assert.InRange(walk.Y, 0, 1);
        }
    }

    [Fact]
    public void CropLayoutPutsTheAnchorAtTheCircleCenter()
    {
        var anchor = new PortraitFaceAnchor(0.3, 0.4, 5);
        var layout = PortraitCropLayout.Make(230, 1000, 1500, anchor)!;
        Assert.Equal(230 * 5, layout.ImageWidth);
        Assert.Equal(230 * 5 * 1.5, layout.ImageHeight);
        Assert.Equal(115 + (0.5 - 0.3) * 230 * 5, layout.CenterX, 6);
        Assert.Equal(115 + (0.5 - 0.4) * 230 * 5 * 1.5, layout.CenterY, 6);
        Assert.Equal(layout.CenterX - layout.ImageWidth / 2, layout.OriginX, 6);
    }

    [Fact]
    public void CropLayoutRejectsBrokenInput()
    {
        Assert.Null(PortraitCropLayout.Make(0, 1000, 1500, new PortraitFaceAnchor(0.5, 0.5, 3)));
        Assert.Null(PortraitCropLayout.Make(230, 1000, 1500, new PortraitFaceAnchor(1.5, 0.5, 3)));
        Assert.Null(PortraitCropLayout.Make(230, 1000, 1500, new PortraitFaceAnchor(0.5, 0.5, 0.5)));
    }

    // MARK: Encrypted backups (LocalStateStore)

    static LocalStateStore Store(string root) => new(root, new DpapiStateProtector(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void BackupRoundTripsThroughPreviewAndRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "airoom-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = Store(root);
            var original = store.Load();
            original.Settings.UserName = "測試擁有者";
            original.Conversations.Add(new Conversation(MemberID.Claude));
            store.Save(original);

            var file = store.Backup();
            Assert.True(File.Exists(file));
            var files = store.Backups();
            Assert.Single(files);
            Assert.False(files[0].BeforeRestore);
            Assert.True(files[0].Bytes > 0);

            // The preview reports what the backup holds.
            var summary = store.Preview(file);
            Assert.Equal("測試擁有者", summary.OwnerName);
            Assert.Equal(1, summary.DeskTopics);
            Assert.Contains("Claude Code", summary.MemberNames);

            // A restore swaps the state and leaves a before-restore backup.
            var second = store.Load();
            second.Conversations.Clear();
            store.Save(second);
            Assert.Empty(store.Load().Conversations);

            var restored = store.Restore(file);
            Assert.Equal(1, restored.Conversations.Count);
            var afterFiles = store.Backups();
            Assert.Equal(2, afterFiles.Count);
            Assert.Contains(afterFiles, backup => backup.BeforeRestore);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeleteBackupRejectsPathsOutsideTheBackupFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "airoom-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = Store(root);
            var file = store.Backup();
            var outside = Path.Combine(root, "state.aesgcm");
            store.Save(store.Load());
            Assert.Throws<NativeCLIError>(() => store.DeleteBackup(outside));
            Assert.True(File.Exists(outside));

            store.DeleteBackup(file);
            Assert.False(File.Exists(file));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
