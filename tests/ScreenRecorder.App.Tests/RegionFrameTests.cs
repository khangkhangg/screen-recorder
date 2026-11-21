using ScreenRecorder.App;
using Windows.Graphics;

namespace ScreenRecorder.App.Tests;

public class RegionFrameTests
{
    static readonly RectInt32 Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Frame_around_a_region_in_the_middle()
    {
        var f = RegionFrame.Layout(new RectInt32(500, 400, 640, 360), Monitor, 1, 200);
        Assert.Equal([new(498, 398, 644, 2), new(498, 760, 644, 2), new(498, 398, 2, 364), new(1140, 398, 2, 364)], f.Strips);
        Assert.Equal([new(494, 394, 26, 6), new(494, 394, 6, 26), new(1120, 394, 26, 6), new(1140, 394, 6, 26),
            new(1120, 760, 26, 6), new(1140, 740, 6, 26), new(494, 760, 26, 6), new(494, 740, 6, 26)], f.Marks);
        Assert.Equal([new(810, 394, 20, 10), new(1136, 570, 10, 20), new(810, 756, 20, 10), new(494, 570, 10, 20)], f.Handles);
        Assert.Equal(new RectInt32(530, 368, 200, 30), f.Tab);
        Assert.False(f.TabInside);
    }

    [Theory]
    [InlineData(600, 380, FrameGrip.Move)]
    [InlineData(490, 390, FrameGrip.Left | FrameGrip.Top)]
    [InlineData(515, 385, FrameGrip.Left | FrameGrip.Top)]
    [InlineData(1150, 770, FrameGrip.Right | FrameGrip.Bottom)]
    [InlineData(1150, 390, FrameGrip.Right | FrameGrip.Top)]
    [InlineData(480, 770, FrameGrip.Left | FrameGrip.Bottom)]
    [InlineData(820, 390, FrameGrip.Top)]
    [InlineData(820, 770, FrameGrip.Bottom)]
    [InlineData(490, 580, FrameGrip.Left)]
    [InlineData(1150, 580, FrameGrip.Right)]
    [InlineData(800, 580, FrameGrip.None)]  // inside the region
    [InlineData(505, 405, FrameGrip.None)]  // inside, next to a corner mark
    [InlineData(100, 100, FrameGrip.None)]
    public void Hit_test_uses_the_larger_grab_areas(int x, int y, FrameGrip expected) =>
        Assert.Equal(expected, RegionFrame.HitTest(RegionFrame.Layout(new RectInt32(500, 400, 640, 360), Monitor, 1, 200), x, y));

    [Fact]
    public void Frame_at_the_monitor_corner_is_drawn_inside_with_the_tab_inside()
    {
        var f = RegionFrame.Layout(new RectInt32(0, 0, 800, 600), Monitor, 1, 200);
        Assert.Equal(new RectInt32(0, 0, 26, 6), f.Marks[0]);
        Assert.Equal(new RectInt32(0, 0, 6, 26), f.Marks[1]);
        Assert.Equal(new RectInt32(390, 0, 20, 10), f.Handles[0]);
        Assert.Equal(new RectInt32(30, 2, 200, 30), f.Tab); // hangs below the top border
        Assert.True(f.TabInside);
        Assert.Equal(FrameGrip.Left | FrameGrip.Top, RegionFrame.HitTest(f, 5, 5));
        Assert.Equal(FrameGrip.Move, RegionFrame.HitTest(f, 100, 10));
    }

    [Theory]
    [InlineData(20, true, 20)]
    [InlineData(31, true, 31)]
    [InlineData(32, false, 0)]
    [InlineData(1, true, 2)]   // the top border is pushed inside, and the tab hangs below it
    [InlineData(0, true, 2)]
    [InlineData(300, false, 268)]
    public void Tab_goes_inside_when_there_is_no_room_above(int top, bool inside, int tabTop)
    {
        var f = RegionFrame.Layout(new RectInt32(500, top, 640, 360), Monitor, 1, 200);
        Assert.Equal(inside, f.TabInside);
        Assert.Equal(tabTop, f.Tab.Y);
    }

    [Fact]
    public void Tab_of_a_narrow_region_at_the_monitor_right_edge_stays_on_the_monitor()
    {
        var f = RegionFrame.Layout(new RectInt32(1880, 500, 40, 100), Monitor, 1, 120);
        Assert.Equal(new RectInt32(1880, 468, 40, 30), f.Tab);
    }

    [Fact]
    public void Frame_on_a_monitor_at_negative_coordinates()
    {
        var f = RegionFrame.Layout(new RectInt32(-100, 300, 100, 80), new RectInt32(-1920, 0, 1920, 1080), 1, 150);
        Assert.Equal(new RectInt32(-100, 268, 100, 30), f.Tab);
        Assert.Equal(new RectInt32(-26, 294, 26, 6), f.Marks[2]);
        Assert.Equal(new RectInt32(-6, 294, 6, 26), f.Marks[3]);
    }

    [Theory]
    [InlineData(500, 720)]   // on the bottom edge
    [InlineData(0, 720)]     // bottom-left corner
    [InlineData(1280, 720)]  // bottom-right corner
    public void Frame_at_the_bottom_stays_on_the_monitor(int x, int y)
    {
        var f = RegionFrame.Layout(new RectInt32(x, y, 640, 360), Monitor, 1, 200);
        foreach (var r in f.Strips.Concat(f.Marks).Concat(f.Handles).Concat(f.Grabs.Select(g => g.Area)).Append(f.Tab))
            Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= 1920 && r.Y + r.Height <= 1080, $"{r.X},{r.Y} {r.Width}x{r.Height}");
        Assert.Equal(1078, f.Strips[1].Y); // the bottom border is drawn just inside
        Assert.False(f.TabInside);
        Assert.Equal(new RectInt32(x + 30, 688, 200, 30), f.Tab);
    }

    [Fact]
    public void Sizes_scale_with_the_monitor()
    {
        var f = RegionFrame.Layout(new RectInt32(600, 600, 900, 600), new RectInt32(0, 0, 3840, 2160), 1.5, 300);
        Assert.Equal(new RectInt32(591, 591, 39, 9), f.Marks[0]);
        Assert.Equal(new RectInt32(645, 552, 300, 45), f.Tab);
    }

    [Theory]
    [InlineData(300, true, true)]
    [InlineData(230, true, true)]
    [InlineData(229, true, false)]
    [InlineData(150, true, false)]
    [InlineData(149, false, false)]
    [InlineData(30, false, false)]
    public void Tab_drops_the_size_first_then_the_name(int regionWidth, bool name, bool size) =>
        Assert.Equal((name, size), RegionFrame.TabContent(regionWidth, 50, 100, 80));

    [Theory]
    [InlineData(50, 20, 150, 120)]
    [InlineData(-500, 0, 0, 100)]
    [InlineData(5000, 5000, 1720, 980)]
    public void Move_keeps_the_size_and_stays_on_the_monitor(int dx, int dy, int x, int y) =>
        Assert.Equal(new RectInt32(x, y, 200, 100), RegionFrame.Move(new RectInt32(100, 100, 200, 100), dx, dy, Monitor));

    [Theory]
    [InlineData(FrameGrip.Left | FrameGrip.Top, -50, -50, 50, 50, 250, 150)]
    [InlineData(FrameGrip.Right | FrameGrip.Bottom, 100, 100, 100, 100, 300, 200)]
    [InlineData(FrameGrip.Left, 1000, 0, 236, 100, 64, 100)]
    [InlineData(FrameGrip.Top, 0, 1000, 100, 136, 200, 64)]
    [InlineData(FrameGrip.Right, -1000, 0, 100, 100, 64, 100)]
    [InlineData(FrameGrip.Bottom, 0, 5000, 100, 100, 200, 980)]
    [InlineData(FrameGrip.Left, -5000, 0, 0, 100, 300, 100)]
    [InlineData(FrameGrip.Top, 30, 30, 100, 130, 200, 70)]
    public void Resize_moves_only_the_dragged_edges(FrameGrip grip, int dx, int dy, int x, int y, int w, int h) =>
        Assert.Equal(new RectInt32(x, y, w, h), RegionFrame.Resize(new RectInt32(100, 100, 200, 100), grip, dx, dy, Monitor));

    [Theory]
    [InlineData(-100, 0, -600, 100)]
    [InlineData(-5000, 0, -1920, 100)]  // stops at the left edge
    [InlineData(5000, 0, -200, 100)]    // stops at the right edge, which is x 0
    [InlineData(0, -500, -500, 0)]
    public void Move_on_a_monitor_at_negative_coordinates(int dx, int dy, int x, int y) =>
        Assert.Equal(new RectInt32(x, y, 200, 100), RegionFrame.Move(new RectInt32(-500, 100, 200, 100), dx, dy, new RectInt32(-1920, 0, 1920, 1080)));

    [Fact]
    public void Resize_stays_on_a_monitor_at_negative_coordinates() =>
        Assert.Equal(new RectInt32(-300, 100, 300, 100),
            RegionFrame.Resize(new RectInt32(-300, 100, 200, 100), FrameGrip.Right, 1000, 0, new RectInt32(-1920, 0, 1920, 1080)));
}
